// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import * as cdk from 'aws-cdk-lib';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import { Construct } from 'constructs';

export interface IdentityStackProps extends cdk.StackProps {
  /**
   * Resource server identifier, which becomes the audience-ish prefix on scope names. Kept short
   * because it is repeated in every scope string a caller has to type.
   */
  readonly resourceServerId?: string;

  /** Scope an agent must hold to invoke tools. */
  readonly scopeName?: string;
}

/**
 * The OAuth authorization server for the samples, owned by its own stack.
 *
 * There is a reason this is separate rather than folded into the gateway stack, and it is worth
 * knowing before you rearrange it. The gateway stack already depends on the application stack, for the
 * VPC and the endpoint URL. In bearer-token mode the application also has to validate tokens, so it
 * needs the issuer's discovery URL. Putting the user pool in the gateway stack would make the
 * application depend on the gateway and the gateway depend on the application, which CloudFormation
 * refuses. One stack owning identity gives a clean order: identity, then application, then gateway.
 *
 * It also matches how a real estate is organised. An identity provider is shared infrastructure, not
 * something each application or gateway brings along.
 *
 * Two clients, because they are two different callers:
 *
 * `agentClient` is what an agent presents to the gateway. The gateway validates it as an OAuth
 * resource server.
 *
 * `gatewayOutboundClient` is what the gateway uses to obtain a token for the application, through an
 * AgentCore Identity OAuth2 credential provider. The application validates that one.
 *
 * Both use client credentials, so a token carries a client rather than a person. That is honest about
 * what this proves: bearer-token validation, scope enforcement and client allowlisting all work end to
 * end. Carrying a named end user needs a user-federation flow with browser consent, which is a
 * deliberate next step rather than something to fake here.
 */
export class IdentityStack extends cdk.Stack {
  public readonly userPool: cognito.UserPool;

  /** `https://…/.well-known/openid-configuration`, which the application's JWT authorizer reads. */
  public readonly discoveryUrl: string;

  /** Token endpoint, for scripts and agents fetching a token. */
  public readonly tokenEndpoint: string;

  /** Authorization endpoint. Unused by client credentials, required when registering the provider. */
  public readonly authorizationEndpoint: string;

  public readonly agentClientId: string;
  public readonly agentClientSecret: cdk.SecretValue;

  public readonly gatewayOutboundClientId: string;
  public readonly gatewayOutboundClientSecret: cdk.SecretValue;

  /** Fully qualified scope, as it appears in a token's `scope` claim. */
  public readonly scope: string;

  public readonly issuer: string;

  constructor(scope: Construct, id: string, props: IdentityStackProps = {}) {
    super(scope, id, props);

    const resourceServerId = props.resourceServerId ?? 'orderportal';
    const scopeName = props.scopeName ?? 'tools.invoke';

    this.userPool = new cognito.UserPool(this, 'Pool', {
      userPoolName: `${this.stackName}-tools`,
      selfSignUpEnabled: false,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });

    // Needed for the client-credentials token endpoint to exist at all.
    const domain = this.userPool.addDomain('Domain', {
      cognitoDomain: { domainPrefix: `${this.stackName}-${this.account}`.toLowerCase().slice(0, 63) },
    });

    const invokeScope = new cognito.ResourceServerScope({
      scopeName,
      scopeDescription: 'Invoke tools exposed by the application',
    });

    const resourceServer = this.userPool.addResourceServer('ToolsResourceServer', {
      identifier: resourceServerId,
      scopes: [invokeScope],
    });

    const agentClient = this.userPool.addClient('AgentClient', {
      userPoolClientName: 'agent',
      generateSecret: true,
      authFlows: {},
      oAuth: {
        flows: { clientCredentials: true },
        scopes: [cognito.OAuthScope.resourceServer(resourceServer, invokeScope)],
      },
    });

    const gatewayOutboundClient = this.userPool.addClient('GatewayOutboundClient', {
      userPoolClientName: 'gateway-outbound',
      generateSecret: true,
      authFlows: {},
      oAuth: {
        flows: { clientCredentials: true },
        scopes: [cognito.OAuthScope.resourceServer(resourceServer, invokeScope)],
      },
    });

    this.issuer = `https://cognito-idp.${this.region}.amazonaws.com/${this.userPool.userPoolId}`;
    this.discoveryUrl = `${this.issuer}/.well-known/openid-configuration`;
    this.tokenEndpoint = `${domain.baseUrl()}/oauth2/token`;
    this.authorizationEndpoint = `${domain.baseUrl()}/oauth2/authorize`;
    this.scope = `${resourceServerId}/${scopeName}`;

    this.agentClientId = agentClient.userPoolClientId;
    this.agentClientSecret = agentClient.userPoolClientSecret;

    this.gatewayOutboundClientId = gatewayOutboundClient.userPoolClientId;
    this.gatewayOutboundClientSecret = gatewayOutboundClient.userPoolClientSecret;

    new cdk.CfnOutput(this, 'DiscoveryUrl', { value: this.discoveryUrl });
    new cdk.CfnOutput(this, 'TokenEndpoint', { value: this.tokenEndpoint });
    new cdk.CfnOutput(this, 'AuthorizationEndpoint', { value: this.authorizationEndpoint });
    new cdk.CfnOutput(this, 'Scope', { value: this.scope });
    new cdk.CfnOutput(this, 'AgentClientId', { value: this.agentClientId });
    new cdk.CfnOutput(this, 'GatewayOutboundClientId', { value: this.gatewayOutboundClientId });
    new cdk.CfnOutput(this, 'UserPoolId', { value: this.userPool.userPoolId });
  }
}
