// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import * as apigateway from 'aws-cdk-lib/aws-apigateway';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';

const StageName = 'live';

export interface PrivateAppStackProps extends cdk.StackProps {
  /** Gateway target name. Short, because AgentCore prefixes every tool with it. */
  readonly targetName?: string;

  /** Whether tools marked Mutating() may run. False by default. */
  readonly allowMutating?: boolean;
}

/**
 * Deploys the order portal sample with no public endpoint.
 *
 * The shape mirrors what a real internal application needs, which is the point: a private REST API
 * Gateway reachable only through an interface VPC endpoint, and AgentCore Gateway reaching it over VPC
 * Lattice via a `privateEndpoint`. Nothing is resolvable or reachable from the internet.
 *
 * Two things worth knowing about the design:
 *
 * The Lambda is deliberately **not** attached to the VPC. API Gateway invokes it over the AWS
 * backbone, so putting it in a subnet would add cold-start cost and ENI management for no privacy
 * gain — the privacy boundary is the API's endpoint, not the function.
 *
 * The VPC has no NAT gateway and no internet gateway. Nothing in it needs egress, which keeps it both
 * cheaper and easier to reason about.
 *
 * Everything a gateway target needs is exposed as a property, so `GatewayStack` can declare the target
 * in CloudFormation rather than have it created out of band. The CDK `Gateway` L2 does not render
 * `privateEndpoint`, but CloudFormation supports it, so `GatewayStack` sets it on the L1.
 */
export class PrivateAppStack extends cdk.Stack {
  /** Base URL the gateway will call. A deploy-time value, so it is a token. */
  public readonly serverUrl: string;

  /** execute-api interface endpoint DNS name — the target's `routingDomain`. */
  public readonly routingDomain: string;

  public readonly vpcId: string;
  public readonly subnetIds: string[];

  /**
   * The **resource gateway** group, which is the one that needs egress. Deliberately not the endpoint
   * group: passing that one leaves the target READY and every call silently failing.
   */
  public readonly resourceGatewaySecurityGroupId: string;

  /** Holds the value the gateway must present as `X-Mcp-Key`. */
  public readonly sharedSecretArn: string;

  public readonly targetName: string;

  constructor(scope: Construct, id: string, props: PrivateAppStackProps = {}) {
    super(scope, id, props);

    const targetName = props.targetName ?? 'orderportal';

    const secret = new secretsmanager.Secret(this, 'SharedSecret', {
      description: `McpToolAdapter shared secret for ${targetName}`,
      generateSecretString: { passwordLength: 48, excludePunctuation: true, includeSpace: false },
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });

    // Isolated subnets only: no NAT, no internet gateway, nothing routable outward.
    const vpc = new ec2.Vpc(this, 'Vpc', {
      maxAzs: 2,
      natGateways: 0,
      subnetConfiguration: [
        { name: 'private', subnetType: ec2.SubnetType.PRIVATE_ISOLATED, cidrMask: 24 },
      ],
    });

    // Two groups, referencing each other, because they protect different things.
    //
    // AgentCore's managed private endpoint provisions a VPC Lattice resource gateway in these subnets
    // and attaches `resourceGatewaySecurityGroup` to it. That group needs **egress** to reach the
    // endpoint. Getting this wrong is silent: the target still reports READY, tools/list still works
    // because the gateway answers that from its own cache, and only tools/call fails — with a generic
    // internal error and no log line anywhere, because the request never reaches the function.
    const resourceGatewaySecurityGroup = new ec2.SecurityGroup(this, 'ResourceGatewaySecurityGroup', {
      vpc,
      description: 'AgentCore Lattice resource gateway: egress to the execute-api endpoint',
      allowAllOutbound: false,
    });

    const endpointSecurityGroup = new ec2.SecurityGroup(this, 'EndpointSecurityGroup', {
      vpc,
      description: 'execute-api endpoint: ingress from the resource gateway only',
      allowAllOutbound: false,
    });

    endpointSecurityGroup.addIngressRule(
      resourceGatewaySecurityGroup,
      ec2.Port.tcp(443),
      'HTTPS from the AgentCore Lattice resource gateway',
    );
    resourceGatewaySecurityGroup.addEgressRule(
      endpointSecurityGroup,
      ec2.Port.tcp(443),
      'HTTPS to the execute-api VPC endpoint',
    );

    const apiEndpoint = new ec2.InterfaceVpcEndpoint(this, 'ExecuteApiEndpoint', {
      vpc,
      service: ec2.InterfaceVpcEndpointAwsService.APIGATEWAY,
      subnets: { subnetType: ec2.SubnetType.PRIVATE_ISOLATED },
      securityGroups: [endpointSecurityGroup],
      privateDnsEnabled: true,
    });

    // Private REST API: reachable only through the interface endpoint above.
    const api = new apigateway.RestApi(this, 'PrivateApi', {
      restApiName: `${targetName}-private`,
      description: `Private entry point to the ${targetName} adapter endpoint`,
      endpointConfiguration: {
        types: [apigateway.EndpointType.PRIVATE],
        vpcEndpoints: [apiEndpoint],
      },
      // Without this, a private API rejects everything. Scoped to this one endpoint, so another
      // endpoint in the account cannot reach it.
      policy: new iam.PolicyDocument({
        statements: [
          new iam.PolicyStatement({
            effect: iam.Effect.ALLOW,
            principals: [new iam.AnyPrincipal()],
            actions: ['execute-api:Invoke'],
            resources: ['execute-api:/*'],
            conditions: { StringEquals: { 'aws:SourceVpce': apiEndpoint.vpcEndpointId } },
          }),
        ],
      }),
      // Full request/response visibility at the API Gateway hop, which is where you can see exactly
      // what arrived from Lattice — headers, path, status — independently of the application's own log.
      // dataTraceEnabled logs full bodies; that is a demonstration setting, not a production one.
      cloudWatchRole: true,
      deployOptions: {
        stageName: StageName,
        loggingLevel: apigateway.MethodLoggingLevel.INFO,
        dataTraceEnabled: true,
        metricsEnabled: true,
        tracingEnabled: true,
        accessLogDestination: new apigateway.LogGroupLogDestination(
          new logs.LogGroup(this, 'ApiAccessLogs', {
            retention: logs.RetentionDays.TWO_WEEKS,
            removalPolicy: cdk.RemovalPolicy.DESTROY,
          }),
        ),
        // Includes the caller identity and the source VPC endpoint, which is how you confirm traffic
        // really arrived privately rather than by some other path.
        accessLogFormat: apigateway.AccessLogFormat.custom(JSON.stringify({
          requestId: apigateway.AccessLogField.contextRequestId(),
          httpMethod: apigateway.AccessLogField.contextHttpMethod(),
          path: apigateway.AccessLogField.contextPath(),
          status: apigateway.AccessLogField.contextStatus(),
          responseLatency: apigateway.AccessLogField.contextResponseLatency(),
          sourceIp: apigateway.AccessLogField.contextIdentitySourceIp(),
          userAgent: apigateway.AccessLogField.contextIdentityUserAgent(),
          vpcEndpointId: '$context.identity.vpceId',
          integrationStatus: apigateway.AccessLogField.contextIntegrationStatus(),
          integrationLatency: apigateway.AccessLogField.contextIntegrationLatency(),
          integrationError: '$context.integration.error',
        })),
      },
    });

    // The document's server URL must be the API's own hostname, not the endpoint's, so it is passed
    // explicitly rather than derived from the Host header.

    // Built from the API id rather than api.url on purpose.
    //
    // api.url resolves through the stage, which depends on the method, which depends on this function —
    // referencing it from the function's environment is a circular dependency. The id depends only on
    // the RestApi resource, so this is the same string without the cycle.
    const serverUrl =
      `https://${api.restApiId}.execute-api.${this.region}.amazonaws.com/${StageName}`;

    const handler = new lambda.Function(this, 'Portal', {
      description: `Order portal sample (${targetName})`,
      runtime: lambda.Runtime.DOTNET_8,
      handler: 'OrderPortal',
      code: lambda.Code.fromCustomCommand(
        path.join(__dirname, '..', 'build', 'orderportal'),
        [
          'dotnet',
          'publish',
          path.join(__dirname, '..', '..', 'samples', 'OrderPortal', 'OrderPortal.csproj'),
          '--configuration',
          'Release',
          '--output',
          path.join(__dirname, '..', 'build', 'orderportal'),
          '/p:GenerateDocumentationFile=false',
        ],
      ),
      architecture: lambda.Architecture.X86_64,
      memorySize: 1024,
      // End-to-end traces, so a slow or failing call can be followed across API Gateway and Lambda.
      tracing: lambda.Tracing.ACTIVE,
      timeout: cdk.Duration.seconds(30),
      logGroup: new logs.LogGroup(this, 'PortalLogs', {
        retention: logs.RetentionDays.ONE_WEEK,
        removalPolicy: cdk.RemovalPolicy.DESTROY,
      }),
      environment: {
        // Resolved by CloudFormation at deploy time. It still lands in the function configuration, so
        // a production deployment should read it from Secrets Manager at startup instead.
        MCP_SHARED_SECRET: secret.secretValue.unsafeUnwrap(),
        MCP_TARGET_NAME: targetName,
        MCP_ALLOW_MUTATING: String(props.allowMutating ?? false),
        MCP_SERVER_URL: serverUrl,
        // REST API sends payload format 1.0, unlike a Function URL.
        MCP_LAMBDA_EVENT_SOURCE: 'restapi',
        // Logs the method, path, headers (credential redacted) and body of every inbound request, so
        // what the gateway sends is visible. A demonstration choice: this writes request bodies to
        // CloudWatch, which a real application should not do.
        MCP_LOG_REQUESTS: 'true',
      },
    });


    // Proxy everything under /_mcp to the function, so the adapter keeps owning its own routing.
    api.root
      .addResource('_mcp')
      .addProxy({ defaultIntegration: new apigateway.LambdaIntegration(handler), anyMethod: true });


    const routingDomain =
      cdk.Fn.select(1, cdk.Fn.split(':', cdk.Fn.select(0, apiEndpoint.vpcEndpointDnsEntries)));

    this.serverUrl = serverUrl;
    this.routingDomain = routingDomain;
    this.vpcId = vpc.vpcId;
    this.subnetIds = vpc.selectSubnets({ subnetType: ec2.SubnetType.PRIVATE_ISOLATED }).subnetIds;
    this.resourceGatewaySecurityGroupId = resourceGatewaySecurityGroup.securityGroupId;
    this.sharedSecretArn = secret.secretArn;
    this.targetName = targetName;

    new cdk.CfnOutput(this, 'ServerUrl', {
      value: serverUrl,
      description: 'Private API base URL — resolvable only inside the VPC',
    });
    new cdk.CfnOutput(this, 'RoutingDomain', {
      value: routingDomain,
      description: 'VPC endpoint DNS name — set as routingDomain on the gateway target',
    });
    new cdk.CfnOutput(this, 'VpcId', { value: vpc.vpcId });
    new cdk.CfnOutput(this, 'SubnetIds', {
      value: vpc.selectSubnets({ subnetType: ec2.SubnetType.PRIVATE_ISOLATED }).subnetIds.join(','),
    });
    new cdk.CfnOutput(this, 'SecurityGroupId', {
      value: resourceGatewaySecurityGroup.securityGroupId,
      description: 'Pass this to the gateway target as securityGroupIds — it is the resource ' +
        'gateway group, the one that needs egress, not the endpoint group',
    });
    new cdk.CfnOutput(this, 'EndpointSecurityGroupId', {
      value: endpointSecurityGroup.securityGroupId,
      description: 'Attached to the execute-api endpoint; ingress from the resource gateway only',
    });
    new cdk.CfnOutput(this, 'SharedSecretArn', { value: secret.secretArn });
    new cdk.CfnOutput(this, 'TargetName', { value: targetName });
  }
}
