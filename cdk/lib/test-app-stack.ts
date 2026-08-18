// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';

export interface TestAppStackProps extends cdk.StackProps {
  /**
   * Gateway target name this application will be registered as. Present here only so the deployed
   * application can run its own AgentCore compatibility checks and report them on /_mcp/health.
   */
  readonly targetName?: string;

  /**
   * Whether tools marked Mutating() may run. False by default, matching the library's own default.
   */
  readonly allowMutating?: boolean;
}

/**
 * Deploys the quick start sample so the AgentCore round trip can be tested end to end.
 *
 * Lambda with a Function URL, for two reasons. A Function URL comes with a valid HTTPS endpoint on an
 * AWS-managed domain — AgentCore requires the OpenAPI `servers` URL to be the real endpoint and the
 * adapter refuses plain HTTP, so anything else means an ACM certificate and a DNS name before the
 * first test. And a zip package needs no container runtime, so `cdk deploy` works on any machine with
 * the .NET SDK.
 *
 * `cdk synth` runs `dotnet publish` itself, so there is no separate build step to forget.
 *
 * Deliberately a test harness, not a production pattern. See the caveats at the end of this file.
 */
export class TestAppStack extends cdk.Stack {
  public readonly functionUrl: lambda.FunctionUrl;
  public readonly secret: secretsmanager.Secret;

  constructor(scope: Construct, id: string, props: TestAppStackProps = {}) {
    super(scope, id, props);

    const targetName = props.targetName ?? 'orderapp';

    // Generated, never written down. The reconciler reads it from here, and the same value is what
    // AgentCore injects as X-Mcp-Key.
    this.secret = new secretsmanager.Secret(this, 'SharedSecret', {
      description: `McpToolAdapter shared secret for ${targetName}`,
      generateSecretString: {
        passwordLength: 48,
        excludePunctuation: true,
        includeSpace: false,
      },
      removalPolicy: cdk.RemovalPolicy.DESTROY, // test stack; delete cleanly
    });

    const repositoryRoot = path.join(__dirname, '..', '..');
    const project = path.join(repositoryRoot, 'samples', 'QuickStart', 'QuickStart.csproj');
    const publishDirectory = path.join(__dirname, '..', 'build', 'quickstart');

    const handler = new lambda.Function(this, 'Endpoint', {
      description: `McpToolAdapter quick start (${targetName})`,
      runtime: lambda.Runtime.DOTNET_8,

      // The published assembly name. Amazon.Lambda.AspNetCoreServer.Hosting turns the executable into
      // a Lambda handler, so there is no Function/Handler method to name.
      handler: 'QuickStart',

      // Built during synth. Framework-dependent, so it runs on the managed .NET 8 runtime and is
      // architecture-neutral — switch to ARM_64 below for a cheaper function if you prefer.
      code: lambda.Code.fromCustomCommand(publishDirectory, [
        'dotnet',
        'publish',
        project,
        '--configuration',
        'Release',
        '--output',
        publishDirectory,
        '/p:GenerateDocumentationFile=false',
      ]),
      architecture: lambda.Architecture.X86_64,
      memorySize: 1024,
      // Generous: covers a cold start plus catalog build. Steady-state calls are single-digit ms.
      timeout: cdk.Duration.seconds(30),
      logGroup: new logs.LogGroup(this, 'EndpointLogs', {
        retention: logs.RetentionDays.ONE_WEEK,
        removalPolicy: cdk.RemovalPolicy.DESTROY,
      }),
      environment: {
        // Resolved by CloudFormation at deploy time, so the plaintext is not in the template.
        //
        // It does land in the function's configuration, readable by anyone holding
        // lambda:GetFunctionConfiguration. Acceptable for a test harness; a production deployment
        // should read the secret from Secrets Manager at startup instead.
        MCP_SHARED_SECRET: this.secret.secretValue.unsafeUnwrap(),
        MCP_TARGET_NAME: targetName,
        MCP_ALLOW_MUTATING: String(props.allowMutating ?? false),
      },
    });

    // Function URLs are HTTPS-only, which is what makes this the cheap path to a valid server URL.
    //
    // AuthType.NONE means the URL is publicly reachable and the adapter's shared secret is the only
    // thing in front of it. That is a deliberate trade for a test: AWS_IAM would require the gateway
    // to sign with SigV4, and it can attach only one outbound credential per target, so it could no
    // longer send the X-Mcp-Key the adapter checks. The secret is 48 random characters, and the stack
    // is meant to be torn down after testing.
    this.functionUrl = handler.addFunctionUrl({
      authType: lambda.FunctionUrlAuthType.NONE,
    });

    new cdk.CfnOutput(this, 'EndpointUrl', {
      value: this.functionUrl.url,
      description: 'Base URL of the deployed adapter',
    });

    new cdk.CfnOutput(this, 'SharedSecretArn', {
      value: this.secret.secretArn,
      description: 'Secrets Manager ARN holding the shared secret',
    });

    new cdk.CfnOutput(this, 'HealthCheckCommand', {
      value:
        `SECRET=$(aws secretsmanager get-secret-value --secret-id ${this.secret.secretArn} ` +
        `--query SecretString --output text) && ` +
        `curl -s -H "X-Mcp-Key: $SECRET" ${this.functionUrl.url}_mcp/health`,
      description: 'Confirm the deployed endpoint is serving before registering it',
    });

    new cdk.CfnOutput(this, 'TargetName', { value: targetName });
  }
}
