# CDK: AgentCore Gateway for McpToolAdapter endpoints

One gateway, one target per application. Typechecked and synthesised against `aws-cdk-lib` 2.264.0.

## What it creates

| Resource | Why |
|---|---|
| `AWS::BedrockAgentCore::Gateway` plus IAM role | The MCP endpoint agents connect to |
| `AWS::BedrockAgentCore::GatewayTarget`, one per app | Maps each `operationId` in your OpenAPI document to an MCP tool |
| `AWS::BedrockAgentCore::ApiKeyCredentialProvider` | Holds the shared secret, injected as `X-Mcp-Key` on every outbound call |
| Cognito user pool, client, domain, resource server | Default inbound identity provider, created automatically unless you pass `inboundJwt` |

## Three stacks

| Stack | Purpose |
|---|---|
| `McpToolAdapterTestApp` | The quick start sample as a zip Lambda behind an HTTPS Function URL. `cdk synth` runs `dotnet publish`, so no container runtime is needed |
| `McpToolAdapterPrivateApp` | The realistic one: the order portal sample behind a private REST API with no public endpoint. See [`docs/agentcore-test.md`](../docs/agentcore-test.md) |
| `McpToolAdapterGateway` | The gateway, inbound identity, credential providers, and the targets |

## Deploy

```bash
npm install
npx cdk deploy McpToolAdapterPrivateApp McpToolAdapterGateway
```

That is the whole thing, including the private-endpoint target. Nothing is copied between stacks by hand
and no schema is fetched from a running endpoint.

### Account and region

Both come from the environment. There is no account number anywhere in this repository.

| Variable | Purpose |
|---|---|
| `AWS_ACCOUNT_ID` | Explicit account override, takes precedence |
| `CDK_DEFAULT_ACCOUNT` | Filled in by the CDK CLI from the active credentials |
| `CDK_DEFAULT_REGION` | Target region, defaults to `us-east-1` |

```bash
AWS_ACCOUNT_ID=111122223333 CDK_DEFAULT_REGION=us-east-1 npx cdk deploy …
```

Synth fails if no account resolves, rather than carrying on. An undefined account produces an
environment-agnostic stack, and those resolve VPC availability-zone lookups to dummy values, so it
synthesises cleanly and deploys something you did not ask for. A value that is not 12 digits is rejected
too.

Region is read from `CDK_DEFAULT_REGION` only, never `AWS_REGION`. `AWS_REGION` is often set to something
unrelated in a shell and it silently beats `--region` on most tooling, and a gateway in the wrong region
fails in ways that look like a permissions problem. Pass it per command.

`cdk.context.json` caches availability-zone lookups keyed by account id, so it is git-ignored here rather
than committed as CDK normally suggests. Otherwise the account number ends up in version control.

## Two ordering problems, and how they are closed

Both look like they force a manual step. Neither does.

The document is produced by the application but consumed at synth. Rather than asking you to start the
application and curl `/_mcp/openapi.json`, the sample takes `--dump-openapi <path>`, which runs the same
code that serves that route and writes the document without binding a port. `lib/schema.ts` invokes it
during synth, the same way the Lambda's `fromCustomCommand` invokes `dotnet publish`, and caches the result
against the application and adapter sources. The document cannot disagree with the code being deployed.
Treat it as a build artefact and do not edit it.

The endpoint's URL is not known until deploy. An API Gateway id does not exist at synth time, so
`servers[0].url` cannot be baked into the document. Pass `serverUrl` on the application entry and
`GatewayStack` overrides the document's value with it. Because it is a CDK reference it lands in the
template as an `Fn::Join` and resolves at deploy, which also means the same document works for any
deployment.

## Private endpoints

For an endpoint with no public DNS, set `privateEndpoint` on the application entry: VPC, subnets, and the
resource gateway security groups. Those groups need egress to your endpoint, and getting that wrong fails
silently. `routingDomain` is required when the document's host is not publicly resolvable; for a private
API Gateway it is the execute-api interface endpoint's DNS name.

The CDK `Gateway` L2 does not render this property, so `GatewayStack` sets it on the underlying
`CfnGatewayTarget`. CloudFormation supports it, so no custom resource is involved.

`bin/app.ts` wires all of it from `McpToolAdapterPrivateApp`'s properties as a worked example.

## The credential prefix

`ApiKeyCredentialLocation.header()` defaults `credentialPrefix` to `"Bearer "` when you do not pass one,
so the gateway would send `X-Mcp-Key: Bearer <secret>` and the endpoint would reject every call with 401.
`GatewayStack` removes the property unless you set `credentialPrefix` explicitly. An empty string does not
work, because CloudFormation gives `CredentialPrefix` a `minLength` of 1, so it has to be absent and the L2
offers no way to omit it. See defect 6 in [`docs/agentcore-test.md`](../docs/agentcore-test.md).

## Synth-time validation

`cdk synth` reads the document and refuses to deploy on:

- A tool name that would exceed 64 characters once `targetName___` is prepended, reported with the exact
  remaining budget. This is the important one. AgentCore documents that breaching a model's tool-spec
  limit fails in the data plane, so the target would create cleanly and calls would fail later.
- Missing or non-HTTPS `servers[0].url`, missing `operationId`, or duplicate `operationId`.
- `oneOf`, `anyOf`, `allOf`, `not`, `discriminator` or `$ref`, none of which AgentCore supports.
- `securitySchemes` or `security` in the document, which AgentCore does not support. Outbound auth belongs
  on the target.
- An OpenAPI version outside 3.0 and 3.1.

Checked: a 44-character target name against a 21-character operation fails synth with
`"…___orderapp_cancel_order" is 68 characters, over the 64 limit`.

## Least privilege, before production

Passing `sharedSecretArn` lets the stack create the credential provider. That is convenient, and it grants
the gateway role wildcard access to `bedrock-agentcore-identity!*`. CDK warns about it at synth. The cause
is structural: the secret AgentCore creates has no ARN at synth time.

For anything beyond a prototype, create the provider once out of band, see
`../samples/agentcore-target.py`, and reference it:

```ts
existingApiKey: {
  providerArn: 'arn:aws:bedrock-agentcore:…:token-vault/…/apikeycredentialprovider/orderapp-mcp-key',
  secretArn:   'arn:aws:secretsmanager:…:secret:bedrock-agentcore-identity!…',
}
```

With both ARNs literal, the grant is scoped to exactly one secret.

## Not covered here

- OAuth and on-behalf-of. This stack wires API key auth only, because that is what the .NET endpoint can
  verify today. See the authentication section in the root README.
- Stack unit tests. The validation is proven by positive and negative synth runs and by the live
  deployment, not by an assertion suite.
