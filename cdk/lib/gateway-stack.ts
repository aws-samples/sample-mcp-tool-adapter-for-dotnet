// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import * as agentcore from 'aws-cdk-lib/aws-bedrockagentcore';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';

/**
 * Longest tool name a model will accept, from Bedrock's ToolSpecification:
 * "Minimum length of 1. Maximum length of 64. Pattern: [a-zA-Z0-9_-]+".
 */
const MAX_TOOL_NAME_LENGTH = 64;

/** AgentCore joins the target name and the operationId with three underscores. */
const TARGET_NAME_DELIMITER = '___';

const TOOL_NAME_PATTERN = /^[a-zA-Z0-9_-]+$/;

export interface ExposedApplicationProps {
  /**
   * Gateway target name. Kept short on purpose: AgentCore exposes every operation as
   * `<targetName>___<operationId>`, so each character here costs a character of the
   * operation-name budget.
   */
  readonly targetName: string;

  /**
   * Path to the OpenAPI document the application serves at `/_mcp/openapi.json`.
   *
   * The application generates this from its own ToolRegistry, so it cannot drift from the code.
   * Fetch it and write it to disk as a build step — see the README — rather than hand-maintaining
   * it here.
   */
  readonly openApiDocumentPath: string;

  /**
   * ARN of the Secrets Manager secret holding the value of the application's `mcp:sharedSecret`.
   * The value is resolved at deploy time and never appears in the synthesized template.
   *
   * Creating the credential provider here is convenient but costs least privilege: because the
   * secret AgentCore creates has no ARN at synth time, the gateway role is granted access by
   * wildcard prefix (`bedrock-agentcore-identity!*`). Prefer {@link existingApiKey} for anything
   * beyond a prototype.
   */
  readonly sharedSecretArn?: string;

  /** Optional JSON key within the secret. Omit if the secret is a plain string. */
  readonly sharedSecretJsonField?: string;

  /**
   * Reference an API key credential provider that already exists, instead of creating one.
   *
   * This is the least-privilege path: with both ARNs known at synth time the gateway role is
   * granted access to exactly one secret rather than a wildcard prefix. Create the provider once
   * with `create_api_key_credential_provider` (see samples/agentcore-target.py) and pass the ARNs
   * it returns.
   */
  readonly existingApiKey?: {
    readonly providerArn: string;
    readonly secretArn: string;
  };

  /**
   * Overrides `servers[0].url` in the document with the endpoint's real base URL.
   *
   * Exists because of an ordering problem: the document is read at synth time, but the URL of the API
   * that will serve it is a deploy-time value — an API Gateway id or a Function URL does not exist
   * until CloudFormation creates it. Passing the CDK token here resolves it in the template, so the
   * document on disk needs no deployment-specific edit and no manual fetch from a running endpoint.
   */
  readonly serverUrl?: string;

  /**
   * Reach the endpoint privately, over a VPC Lattice resource gateway AgentCore provisions and manages
   * on your behalf, instead of over the internet.
   *
   * Set this when the endpoint has no public DNS — which is the case for a private API Gateway or
   * anything behind an internal load balancer.
   *
   * Two details that are easy to get wrong, both of which fail silently:
   *
   * `securityGroupIds` are attached to the **resource gateway**, not to your endpoint, so those groups
   * need **egress** to your endpoint. A group with no egress rule leaves the target READY and
   * `tools/list` working, while every `tools/call` fails with a generic internal error and no log line.
   *
   * `routingDomain` is required whenever the document's host is not publicly resolvable. For a private
   * API Gateway it is the execute-api interface endpoint's DNS name.
   */
  readonly privateEndpoint?: {
    readonly vpcId: string;
    readonly subnetIds: string[];
    /** Resource gateway groups — they need egress to the endpoint. */
    readonly securityGroupIds: string[];
    readonly routingDomain?: string;
    /** Defaults to IPV4. */
    readonly endpointIpAddressType?: 'IPV4' | 'DUALSTACK';
  };

  /**
   * Use an OAuth2 credential provider for the outbound call instead of an API key.
   *
   * This is the path that can carry a caller identity. The gateway obtains a token from the
   * authorization server and presents it as a bearer token, and the application validates it and maps
   * its claims onto a principal its existing authorization checks already read.
   *
   * With client credentials the token names a client rather than a person, which still proves the whole
   * bearer path works. Carrying a named end user needs a user-federation flow, where AgentCore
   * Identity's token vault persists the user's refresh token so only the first call needs consent.
   */
  readonly oauth?: {
    /** Client registered with the authorization server, used by the gateway. */
    readonly clientId: string;
    readonly clientSecret: cdk.SecretValue;

    /**
     * All three endpoints, given explicitly rather than discovered.
     *
     * `CreateOauth2CredentialProvider` rejects a provider without a token endpoint, and the CDK's
     * Cognito factory only accepts an issuer, which fails with "Missing TokenEndpoint". Supplying the
     * metadata directly avoids depending on AgentCore being able to fetch a discovery document, which is
     * not something to assume in a locked-down account. Cognito's discovery document is where these
     * values come from: the issuer is the `cognito-idp` URL, and both endpoints are on the pool domain.
     */
    readonly issuer: string;
    readonly authorizationEndpoint: string;
    readonly tokenEndpoint: string;

    /** Scopes the gateway requests. These end up in the token the application checks. */
    readonly scopes: string[];
  };

  /**
   * Prefix the gateway puts in front of the key, as in `X-Mcp-Key: <prefix><secret>`.
   *
   * Leave unset — the adapter expects the bare secret, and this stack removes the prefix the CDK L2
   * would otherwise inject. Set it only for a target that wants something like `Token ` in front.
   */
  readonly credentialPrefix?: string;

  readonly description?: string;
}

export interface GatewayStackProps extends cdk.StackProps {
  readonly gatewayName: string;

  /** One entry per .NET application being exposed. Each becomes its own gateway target. */
  readonly applications: ExposedApplicationProps[];

  /**
   * Grant the gateway role permission to read outbound credentials from the default token vault.
   *
   * Needed whenever targets are attached **outside** this stack, which is what the reconciler does for
   * applications this repository does not deploy. When CDK declares the target itself it grants this
   * automatically; when something else does, nothing does, and the failure is nasty: the target
   * reports READY, `tools/list` still works because the gateway answers it from cache, and only
   * `tools/call` fails — with a generic internal error and no log line anywhere, because the gateway
   * cannot fetch the API key to make the request.
   *
   * Defaults to true. Scoped to this gateway and the default token vault, but wildcarded across
   * credential providers within it, because the provider names are not known here.
   */
  readonly grantTokenVaultAccess?: boolean;

  /**
   * Deliver the gateway's own application logs to CloudWatch. Defaults to true.
   *
   * This is what shows you the gateway's side of a tool call — which tool it resolved, the request and
   * response bodies it exchanged with your target, and any error. Without it, a failed invocation gives
   * you nothing but "An internal error occurred", which is exactly the situation that cost the most time
   * bringing this up. Worth having on from the start.
   */
  readonly enableGatewayLogs?: boolean;

  /**
   * Additionally deliver gateway traces to X-Ray. Defaults to **false**.
   *
   * Off by default because it needs CloudWatch Transaction Search enabled, which is an account-wide,
   * one-time setting with its own cost, not something a stack should switch on for you. Enable it in the
   * CloudWatch console under Application Signals, then set this to true.
   */
  readonly enableGatewayTraces?: boolean;

  /**
   * Inbound authorization. Omit to let the construct create and configure a Cognito user pool as
   * the default identity provider.
   */
  readonly inboundJwt?: {
    /** Must match `^.+/\.well-known/openid-configuration$`. */
    readonly discoveryUrl: string;
    readonly allowedAudiences?: string[];
    readonly allowedClients?: string[];
  };
}

/**
 * Registers McpToolAdapter endpoints as Bedrock AgentCore Gateway OpenAPI targets.
 *
 * One gateway, one target per application. Targets are separate so that a client can be granted
 * one application's tools without the others, and so that a schema change to one application
 * cannot invalidate another.
 */
export class GatewayStack extends cdk.Stack {
  public readonly gateway: agentcore.Gateway;

  constructor(scope: Construct, id: string, props: GatewayStackProps) {
    super(scope, id, props);

    this.gateway = new agentcore.Gateway(this, 'Gateway', {
      gatewayName: props.gatewayName,
      ...(props.inboundJwt
        ? {
            authorizerConfiguration: agentcore.GatewayAuthorizer.usingCustomJwt({
              discoveryUrl: props.inboundJwt.discoveryUrl,
              allowedAudience: props.inboundJwt.allowedAudiences,
              allowedClients: props.inboundJwt.allowedClients,
            }),
          }
        : {}),
    });

    if (props.grantTokenVaultAccess ?? true) {
      this.grantTokenVaultAccess(props.gatewayName);
    }

    if (props.enableGatewayLogs ?? true) {
      this.enableObservability(props.gatewayName, props.enableGatewayTraces ?? false);
    }

    for (const application of props.applications) {
      this.addApplication(application);
    }

    new cdk.CfnOutput(this, 'GatewayName', { value: props.gatewayName });
  }

  /**
   * Lets the gateway fetch the outbound credential for a target at invocation time.
   *
   * These are the two actions CDK grants when it declares a target with an API key credential
   * provider — verified by synthesising such a target and reading the generated policy, rather than
   * guessed. `GetWorkloadAccessToken` obtains the gateway's workload identity; `GetResourceApiKey`
   * reads the key itself out of the token vault.
   */
  private grantTokenVaultAccess(gatewayName: string): void {
    const vault = `arn:${this.partition}:bedrock-agentcore:${this.region}:${this.account}:token-vault/default`;
    const directory =
      `arn:${this.partition}:bedrock-agentcore:${this.region}:${this.account}:workload-identity-directory/default`;
    const workloadIdentities = `${directory}/workload-identity/${gatewayName}-*`;

    this.gateway.role.addToPrincipalPolicy(new iam.PolicyStatement({
      sid: 'WorkloadAccessToken',
      actions: ['bedrock-agentcore:GetWorkloadAccessToken'],
      resources: [directory, workloadIdentities],
    }));

    this.gateway.role.addToPrincipalPolicy(new iam.PolicyStatement({
      sid: 'ResourceApiKey',
      actions: ['bedrock-agentcore:GetResourceApiKey'],
      resources: [vault, `${vault}/apikeycredentialprovider/*`, directory, workloadIdentities],
    }));

    // AgentCore stores the key in a Secrets Manager secret it owns, under a reserved name prefix.
    // The ARN is unknown here, so the grant is by prefix.
    this.gateway.role.addToPrincipalPolicy(new iam.PolicyStatement({
      sid: 'IdentitySecret',
      actions: ['secretsmanager:GetSecretValue'],
      resources: [
        `arn:${this.partition}:secretsmanager:${this.region}:${this.account}:secret:bedrock-agentcore-identity!*`,
      ],
    }));
  }

  /**
   * Routes the gateway's service-generated logs into CloudWatch using vended log delivery.
   *
   * Three resources per stream: a delivery source naming the gateway and log type, a delivery
   * destination naming where it goes, and a delivery joining them. This is the CloudFormation
   * equivalent of PutDeliverySource / PutDeliveryDestination / CreateDelivery.
   *
   * The log group name must sit under `/aws/vendedlogs/` — vended delivery is only permitted to write
   * there, and a different prefix fails at deploy time rather than silently dropping logs.
   */
  private enableObservability(gatewayName: string, includeTraces: boolean): void {
    const logGroup = new logs.LogGroup(this, 'GatewayLogs', {
      logGroupName: `/aws/vendedlogs/bedrock-agentcore/${gatewayName}`,
      retention: logs.RetentionDays.TWO_WEEKS,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });

    const applicationLogs = new logs.CfnDeliverySource(this, 'GatewayLogSource', {
      name: `${gatewayName}-application-logs`,
      logType: 'APPLICATION_LOGS',
      resourceArn: this.gateway.gatewayArn,
    });

    const logDestination = new logs.CfnDeliveryDestination(this, 'GatewayLogDestination', {
      name: `${gatewayName}-application-logs-destination`,
      destinationResourceArn: logGroup.logGroupArn,
    });

    const logDelivery = new logs.CfnDelivery(this, 'GatewayLogDelivery', {
      deliverySourceName: applicationLogs.name,
      deliveryDestinationArn: logDestination.attrArn,
    });
    logDelivery.addDependency(applicationLogs);
    logDelivery.addDependency(logDestination);

    new cdk.CfnOutput(this, 'GatewayLogGroup', {
      value: logGroup.logGroupName,
      description: "The gateway's own view of each tool call — resolved tool, bodies exchanged, errors",
    });

    if (!includeTraces) return;

    // Requires CloudWatch Transaction Search; see enableGatewayTraces.
    const traceSource = new logs.CfnDeliverySource(this, 'GatewayTraceSource', {
      name: `${gatewayName}-traces`,
      logType: 'TRACES',
      resourceArn: this.gateway.gatewayArn,
    });

    const traceDestination = new logs.CfnDeliveryDestination(this, 'GatewayTraceDestination', {
      name: `${gatewayName}-traces-destination`,
      deliveryDestinationType: 'XRAY',
    });

    const traceDelivery = new logs.CfnDelivery(this, 'GatewayTraceDelivery', {
      deliverySourceName: traceSource.name,
      deliveryDestinationArn: traceDestination.attrArn,
    });
    traceDelivery.addDependency(traceSource);
    traceDelivery.addDependency(traceDestination);
  }

  private addApplication(application: ExposedApplicationProps): void {
    const document = this.loadAndValidateDocument(application);
    const scopeId = pascalCase(application.targetName);

    // The header the .NET endpoint checks. IAM (SigV4) is not an option for an IIS-hosted target:
    // it requires a target that natively verifies SigV4, which rules out anything behind a load
    // balancer.
    //
    // The prefix is deliberately left unset here and deleted from the template below. See the comment
    // at the deletion override — the L2's default breaks the call.
    const credentialLocation = agentcore.ApiKeyCredentialLocation.header({
      credentialParameterName: 'X-Mcp-Key',
      credentialPrefix: application.credentialPrefix,
    });

    // OAuth wins when configured, because it is the stronger of the two and configuring both would be a
    // mistake worth failing on rather than silently resolving.
    const credentialProvider = application.oauth
      ? agentcore.GatewayCredentialProvider.fromOauthIdentity(
          agentcore.OAuth2CredentialProvider.usingCustom(this, `${scopeId}OAuth`, {
            oAuth2CredentialProviderName: `${application.targetName}-mcp-oauth`,
            clientId: application.oauth.clientId,
            clientSecret: application.oauth.clientSecret,
            authorizationServerMetadata: {
              issuer: application.oauth.issuer,
              authorizationEndpoint: application.oauth.authorizationEndpoint,
              tokenEndpoint: application.oauth.tokenEndpoint,
            },
          }),
          { scopes: application.oauth.scopes },
        )
      : application.existingApiKey
      ? // Literal ARNs, so the gateway role is granted access to exactly one secret.
        agentcore.GatewayCredentialProvider.fromApiKeyIdentityArn({
          providerArn: application.existingApiKey.providerArn,
          secretArn: application.existingApiKey.secretArn,
          credentialLocation,
        })
      : agentcore.GatewayCredentialProvider.fromApiKeyIdentity(
          new agentcore.ApiKeyCredentialProvider(this, `${scopeId}ApiKey`, {
            apiKeyCredentialProviderName: `${application.targetName}-mcp-key`,
            apiKey: application.sharedSecretJsonField
              ? cdk.SecretValue.secretsManager(application.sharedSecretArn!, {
                  jsonField: application.sharedSecretJsonField,
                })
              : cdk.SecretValue.secretsManager(application.sharedSecretArn!),
          }),
          { credentialLocation },
        );

    const target = this.gateway.addOpenApiTarget(`${scopeId}Target`, {
      gatewayTargetName: application.targetName,
      description: application.description ?? `Operations exposed by ${application.targetName}`,
      apiSchema: agentcore.ApiSchema.fromInline(JSON.stringify(document)),
      credentialProviderConfigurations: [credentialProvider],
    });

    const cfnTarget = target.node.defaultChild as agentcore.CfnGatewayTarget;

    if (!application.oauth && !application.credentialPrefix) {
      // Remove the prefix the L2 injects. `ApiKeyCredentialLocation.header()` defaults
      // `credentialPrefix` to `"Bearer "` when none is given, so the gateway sends
      // `X-Mcp-Key: Bearer <secret>` rather than the secret. The endpoint compares the whole header
      // value against the configured secret, so every call comes back 401 — and it is a nasty one to
      // find, because the credential is correct, the network path is correct, and the only clue is
      // that the header is a few characters longer than the secret.
      //
      // An empty string will not do: CloudFormation gives CredentialPrefix a minLength of 1, so the
      // property has to be absent, and the L2 offers no way to omit it. Hence the deletion override.
      cfnTarget.addPropertyDeletionOverride(
        'CredentialProviderConfigurations.0.CredentialProvider.ApiKeyCredentialProvider.CredentialPrefix',
      );
    }

    if (application.privateEndpoint) {
      // Set on the L1 because the L2 does not render `privateEndpoint` yet: GatewayTarget builds its
      // CfnGatewayTarget props without it, and keeps the L1 private. CloudFormation itself supports the
      // property, so reaching the L1 is enough — no custom resource needed.
      cfnTarget.privateEndpoint = {
        // A tagged union: exactly one of managedVpcResource or selfManagedLatticeResource, and
        // routingDomain belongs *inside* it. The developer guide's example reads as though routingDomain
        // is a sibling; passing it that way is rejected outright.
        managedVpcResource: {
          vpcIdentifier: application.privateEndpoint.vpcId,
          subnetIds: application.privateEndpoint.subnetIds,
          securityGroupIds: application.privateEndpoint.securityGroupIds,
          routingDomain: application.privateEndpoint.routingDomain,
          endpointIpAddressType: application.privateEndpoint.endpointIpAddressType ?? 'IPV4',
        },
      };
    }
  }

  /**
   * Reads the application's OpenAPI document and fails synthesis if AgentCore would reject it or
   * mis-invoke it.
   *
   * Worth doing here even though the application checks itself at startup. A tool name that
   * breaches the model's limit fails in the AgentCore data plane — the target creates cleanly and
   * calls fail later — so catching it at synth is the difference between a failed deployment and a
   * silently broken tool. The arithmetic needs the target name, which only this stack knows.
   */
  private loadAndValidateDocument(application: ExposedApplicationProps): unknown {
    const absolutePath = path.resolve(application.openApiDocumentPath);

    if (!fs.existsSync(absolutePath)) {
      throw new Error(
        `OpenAPI document not found at ${absolutePath}. Fetch it from the running application's ` +
          `/_mcp/openapi.json and write it to that path before synthesising. See cdk/README.md.`,
      );
    }

    let document: any;
    try {
      document = JSON.parse(fs.readFileSync(absolutePath, 'utf8'));
    } catch (error) {
      throw new Error(`${absolutePath} is not valid JSON: ${(error as Error).message}`);
    }

    // Applied before validation so the checks below see the URL that will actually be deployed.
    if (application.serverUrl) {
      document.servers = [{ url: application.serverUrl }];
    }

    const problems: string[] = [];

    if (application.oauth && (application.existingApiKey || application.sharedSecretArn)) {
      problems.push(
        'Configure either oauth or an API key, not both. Two outbound credentials on one target is ' +
          'ambiguous, and silently preferring one would hide a mistake.',
      );
    }

    if (!application.oauth && !application.existingApiKey && !application.sharedSecretArn) {
      problems.push(
        'Supply oauth, or sharedSecretArn (creates a credential provider), or existingApiKey ' +
          '(references one, and scopes the secret grant to a single ARN).',
      );
    }

    if (typeof document.openapi !== 'string' || !/^3\.[01]/.test(document.openapi)) {
      problems.push(
        `openapi is "${document.openapi}"; AgentCore accepts 3.0 and 3.1 only (Swagger 2.0 is rejected).`,
      );
    }

    const serverUrl = document.servers?.[0]?.url;
    if (typeof serverUrl !== 'string' || serverUrl.length === 0) {
      problems.push(
        'servers[0].url is missing. AgentCore requires the server attribute to carry the real ' +
          "endpoint URL — set the application's mcp:serverUrl.",
      );
    } else if (cdk.Token.isUnresolved(serverUrl)) {
      // A deploy-time value — an API id or Function URL. Nothing about it can be checked here; the
      // stack that produced it is responsible for it being HTTPS.
    } else if (!serverUrl.startsWith('https://')) {
      problems.push(
        `servers[0].url is "${serverUrl}". The gateway sends the API key on every call, so it ` +
          'must not travel over plain HTTP.',
      );
    }

    if (!TOOL_NAME_PATTERN.test(application.targetName)) {
      problems.push(
        `targetName "${application.targetName}" must match ${TOOL_NAME_PATTERN} to produce ` +
          'tool names a model will accept.',
      );
    }

    const budget = MAX_TOOL_NAME_LENGTH - application.targetName.length - TARGET_NAME_DELIMITER.length;

    const paths = document.paths ?? {};
    const operationIds: string[] = [];

    for (const [pathKey, methods] of Object.entries<any>(paths)) {
      for (const [method, operation] of Object.entries<any>(methods ?? {})) {
        const operationId = operation?.operationId;

        if (typeof operationId !== 'string' || operationId.length === 0) {
          problems.push(
            `${method.toUpperCase()} ${pathKey} has no operationId. AgentCore uses it as the ` +
              'tool name and requires it on every exposed operation.',
          );
          continue;
        }

        operationIds.push(operationId);

        if (operationId.length > budget) {
          problems.push(
            `"${application.targetName}${TARGET_NAME_DELIMITER}${operationId}" is ` +
              `${application.targetName.length + TARGET_NAME_DELIMITER.length + operationId.length} ` +
              `characters, over the ${MAX_TOOL_NAME_LENGTH} limit. The target name leaves ` +
              `${budget} characters for the operationId. Shorten it with .Named("...") in the ` +
              'ToolRegistry, or use a shorter target name.',
          );
        }
      }
    }

    if (operationIds.length === 0) {
      problems.push('The document declares no operations, so the target would expose no tools.');
    }

    const duplicates = operationIds.filter((id, index) => operationIds.indexOf(id) !== index);
    if (duplicates.length > 0) {
      problems.push(`Duplicate operationId(s): ${[...new Set(duplicates)].join(', ')}.`);
    }

    for (const keyword of ['oneOf', 'anyOf', 'allOf', 'not', 'discriminator', '$ref']) {
      if (containsKey(document, keyword)) {
        problems.push(
          `The document uses "${keyword}", which AgentCore does not support for OpenAPI targets.`,
        );
      }
    }

    for (const keyword of ['securitySchemes', 'security']) {
      if (containsKey(document, keyword)) {
        problems.push(
          `The document declares "${keyword}". AgentCore does not support specification-level ` +
            'security; outbound auth is configured on the target instead, as this stack does.',
        );
      }
    }

    if (problems.length > 0) {
      throw new Error(
        `OpenAPI document for target "${application.targetName}" is not deployable ` +
          `(${problems.length} problem(s)):\n` +
          problems.map((p) => `  - ${p}`).join('\n'),
      );
    }

    return document;
  }
}

/** Depth-first search for a key anywhere in the document, including inside arrays. */
function containsKey(node: unknown, key: string): boolean {
  if (Array.isArray(node)) {
    return node.some((item) => containsKey(item, key));
  }

  if (node !== null && typeof node === 'object') {
    for (const [candidate, value] of Object.entries(node as Record<string, unknown>)) {
      if (candidate === key) return true;
      if (containsKey(value, key)) return true;
    }
  }

  return false;
}

function pascalCase(value: string): string {
  return value
    .split(/[^a-zA-Z0-9]+/)
    .filter((part) => part.length > 0)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join('');
}
