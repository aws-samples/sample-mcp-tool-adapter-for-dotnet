// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { GatewayStack } from '../lib/gateway-stack';
import { IdentityStack } from '../lib/identity-stack';
import { MemoryStack } from '../lib/memory-stack';
import { PrivateAppStack } from '../lib/private-app-stack';
import { dumpOpenApiDocument } from '../lib/schema';
import { TestAppStack } from '../lib/test-app-stack';

const repositoryRoot = path.join(__dirname, '..', '..');

const app = new cdk.App();

// Account and region come from the environment — never from a literal in this file, which would tie a
// shared sample to one account and is the kind of value that gets committed by accident.
//
// `AWS_ACCOUNT_ID` is the explicit override; `CDK_DEFAULT_ACCOUNT` is what the CDK CLI fills in from
// whichever credentials are active. Resolution is deliberately strict rather than defaulted: leaving
// `account` undefined produces an environment-agnostic stack, which sounds harmless and is not — VPC
// availability-zone lookups then resolve to dummy values, so a stack synthesises cleanly and deploys
// into a shape you did not ask for.
const account = process.env.AWS_ACCOUNT_ID ?? process.env.CDK_DEFAULT_ACCOUNT;

if (!account) {
  throw new Error(
    'No AWS account resolved. Set AWS_ACCOUNT_ID, or run through the CDK CLI with credentials active ' +
      'so CDK_DEFAULT_ACCOUNT is populated:\n' +
      '  AWS_ACCOUNT_ID=111122223333 npx cdk deploy …',
  );
}

if (!/^[0-9]{12}$/.test(account)) {
  throw new Error(`AWS account "${account}" is not a 12-digit account id.`);
}

// Region is read from CDK_DEFAULT_REGION only, not AWS_REGION, on purpose. AWS_REGION is commonly set
// to something unrelated in a developer's shell, and it silently beats --region on most tooling; a
// gateway deployed to the wrong region fails in ways that look like a permissions problem. Pass the
// region explicitly per command.
const env = {
  account,
  region: process.env.CDK_DEFAULT_REGION ?? 'us-east-1',
};

// ------------------------------------------------------------------------------------------------
// Test harness. Deploys the quick start sample behind an HTTPS Function URL, so the AgentCore round
// trip can be exercised without an existing application or an IIS host.
//
//   cdk deploy McpToolAdapterTestApp
//
// Needs only the .NET 8 SDK — synth runs `dotnet publish` and packages the result as a zip.
// ------------------------------------------------------------------------------------------------

new TestAppStack(app, 'McpToolAdapterTestApp', {
  env,
  targetName: 'orderapp',
  allowMutating: false,
});

// ------------------------------------------------------------------------------------------------
// The realistic test: the order portal sample with no public endpoint. A private REST API Gateway
// reachable only through an interface VPC endpoint, which AgentCore reaches over VPC Lattice.
//
//   cdk deploy McpToolAdapterPrivateApp
//
// This is the one that mirrors a real internal application, and it is deployed entirely by
// CloudFormation — including the gateway target, which is declared below.
// ------------------------------------------------------------------------------------------------

// Outbound authentication mode, chosen at synth: `npx cdk deploy -c authMode=jwt …`
//
// apikey is the default because it is the simpler thing to get working and needs no authorization
// server. jwt is what carries a caller identity into the business logic, and it deploys one extra
// stack. Both are real paths through the same code; neither is a mock.
const authMode = app.node.tryGetContext('authMode') === 'jwt' ? 'jwt' : 'apikey';

// Only created for the jwt path, so the default deployment stays two stacks.
const identity =
  authMode === 'jwt' ? new IdentityStack(app, 'McpToolAdapterIdentity', { env }) : undefined;

const privateApp = new PrivateAppStack(app, 'McpToolAdapterPrivateApp', {
  env,
  targetName: 'orderportal',
  allowMutating: false,
  ...(identity
    ? {
        jwt: {
          discoveryUrl: identity.discoveryUrl,
          // The gateway's outbound client is the only caller the application should accept.
          allowedClientIds: [identity.gatewayOutboundClientId],
          requiredScopes: [identity.scope],
          requiredTokenUse: 'access',
        },
      }
    : {}),
});

// ------------------------------------------------------------------------------------------------
// Gateway, with the order portal attached as a target. One `cdk deploy` covers both stacks.
//
// The two seams that used to need a manual step are closed:
//
//   - the OpenAPI document is generated during synth from the application itself, so it cannot drift
//     from the code being deployed;
//   - the server URL and the VPC identifiers are CDK references, so nothing has to be copied from one
//     stack's outputs into another's input.
//
// automation/agentcore_reconcile.py remains the right tool for applications this repository does not
// deploy — an existing IIS estate, where the endpoint's lifecycle is not CloudFormation's to own.
// ------------------------------------------------------------------------------------------------

new GatewayStack(app, 'McpToolAdapterGateway', {
  env,
  gatewayName: 'legacy-app-tools',

  // Omit inboundJwt to have the construct create a Cognito user pool as the default identity
  // provider — which is what you want for a first test.
  //
  // inboundJwt: {
  //   discoveryUrl: 'https://your-idp.example.com/.well-known/openid-configuration',
  //   allowedAudiences: ['legacy-app-tools'],
  // },

  // The agent's own token is validated by the gateway, so the pool that issues it is the same one.
  ...(identity
    ? {
        inboundJwt: {
          discoveryUrl: identity.discoveryUrl,
          allowedClients: [identity.agentClientId],
        },
      }
    : {}),

  applications: [
    {
      targetName: privateApp.targetName,
      description: 'Order portal operations, reached privately over VPC Lattice',

      // Generated by running the sample's own --dump-openapi during synth. Cached against the sample
      // and adapter sources, so an unchanged tree does not rebuild.
      openApiDocumentPath: dumpOpenApiDocument({
        projectPath: path.join(repositoryRoot, 'samples', 'OrderPortal', 'OrderPortal.csproj'),
        outputPath: path.join(__dirname, '..', 'build', 'schemas', 'orderportal.openapi.json'),
        watchPaths: [
          path.join(repositoryRoot, 'samples', 'OrderPortal'),
          path.join(repositoryRoot, 'src'),
        ],
      }),

      // Deploy-time values, referenced rather than copied.
      serverUrl: privateApp.serverUrl,

      // One outbound credential or the other. GatewayStack fails synth if both are supplied.
      ...(identity
        ? {
            oauth: {
              clientId: identity.gatewayOutboundClientId,
              clientSecret: identity.gatewayOutboundClientSecret,
              issuer: identity.issuer,
              authorizationEndpoint: identity.authorizationEndpoint,
              tokenEndpoint: identity.tokenEndpoint,
              scopes: [identity.scope],
            },
          }
        : { sharedSecretArn: privateApp.sharedSecretArn }),

      privateEndpoint: {
        vpcId: privateApp.vpcId,
        subnetIds: privateApp.subnetIds,
        securityGroupIds: [privateApp.resourceGatewaySecurityGroupId],
        routingDomain: privateApp.routingDomain,
      },
    },
  ],
});

// ------------------------------------------------------------------------------------------------
// Memory for the agent that calls these tools.
//
//   cdk deploy McpToolAdapterMemory
//
// Deployed separately and on purpose. Memory belongs to the agent, not to the application behind the
// tools, and nothing in the adapter reads or writes it. This stack is here so the layering is visible
// rather than left for someone to guess at. See lib/memory-stack.ts.
// ------------------------------------------------------------------------------------------------

new MemoryStack(app, 'McpToolAdapterMemory', { env });

app.synth();
