# CDK — AgentCore Gateway for McpToolAdapter endpoints

One gateway, one target per application. Typechecked and synthesised against `aws-cdk-lib` 2.264.0.

## What it creates

| Resource | Why |
|---|---|
| `AWS::BedrockAgentCore::Gateway` + IAM role | The MCP endpoint agents connect to |
| `AWS::BedrockAgentCore::GatewayTarget` (one per app) | Maps each `operationId` in your OpenAPI document to an MCP tool |
| `AWS::BedrockAgentCore::ApiKeyCredentialProvider` | Holds the shared secret, injected as `X-Mcp-Key` on every outbound call |
| Cognito user pool, client, domain, resource server | Default inbound identity provider — created automatically unless you pass `inboundJwt` |

## Three stacks

| Stack | Purpose |
|---|---|
| `McpToolAdapterTestApp` | The quick start sample as a zip Lambda behind an HTTPS Function URL. `cdk synth` runs `dotnet publish` — no container runtime needed |
| `McpToolAdapterPrivateApp` | The realistic one: the order portal sample behind a **private** REST API, no public endpoint. See [`docs/agentcore-test.md`](../docs/agentcore-test.md) |
| `McpToolAdapterGateway` | The gateway, inbound identity, credential providers, and the targets |

## Deploy

```bash
npm install
npx cdk deploy McpToolAdapterPrivateApp McpToolAdapterGateway
```

That is the whole thing, including the private-endpoint target. Nothing is copied between stacks by
hand and no schema is fetched from a running endpoint.

### Account and region

Both come from the environment; there is no account number anywhere in this repository.

| Variable | Purpose |
|---|---|
| `AWS_ACCOUNT_ID` | Explicit account override. Takes precedence |
| `CDK_DEFAULT_ACCOUNT` | Filled in by the CDK CLI from the active credentials |
| `CDK_DEFAULT_REGION` | Target region. Defaults to `us-east-1` |

```bash
AWS_ACCOUNT_ID=111122223333 CDK_DEFAULT_REGION=us-east-1 npx cdk deploy …
```

Synth **fails** if no account resolves, rather than carrying on. That is deliberate: an undefined account
produces an environment-agnostic stack, and those resolve VPC availability-zone lookups to dummy values —
so it synthesises cleanly and deploys something you did not ask for. A non-12-digit value is rejected too.

Region is read from `CDK_DEFAULT_REGION` only, never `AWS_REGION`. `AWS_REGION` is often set to something
unrelated in a shell and silently beats `--region` on most tooling, and a gateway in the wrong region
fails in ways that look like a permissions problem. Pass it per command.

Note that `cdk.context.json` caches availability-zone lookups **keyed by account id**, so it is
git-ignored here rather than committed as CDK normally suggests — otherwise the account number ends up in
version control.

## The two ordering problems, and how they are closed

Both of these look like they force a manual step. Neither does.

**The document is produced by the application but consumed at synth.** Rather than ask you to start the
application and curl `/_mcp/openapi.json`, the sample takes `--dump-openapi <path>`, which runs the same
code that serves that route and writes the document without binding a port. `lib/schema.ts` invokes it
during synth, exactly as the Lambda's `fromCustomCommand` invokes `dotnet publish`, and caches the result
against the application and adapter sources. The document therefore cannot disagree with the code being
deployed — treat it as a build artefact and never edit it.

**The endpoint's URL is not known until deploy.** An API Gateway id does not exist at synth time, so
`servers[0].url` cannot be baked into the document. Pass `serverUrl` on the application entry and
`GatewayStack` overrides the document's value with it; being a CDK reference, it lands in the template as
an `Fn::Join` and resolves at deploy. This also means the same document works for any deployment.

## Private endpoints

For an endpoint with no public DNS, set `privateEndpoint` on the application entry — VPC, subnets, and
the **resource gateway** security groups, which need *egress* to your endpoint. `routingDomain` is
required when the document's host is not publicly resolvable; for a private API Gateway it is the
execute-api interface endpoint's DNS name.

The CDK `Gateway` L2 does not render this property, so `GatewayStack` sets it on the underlying
`CfnGatewayTarget`. CloudFormation supports it, so no custom resource is involved.

`bin/app.ts` wires all of it from `McpToolAdapterPrivateApp`'s properties as a worked example.

## Synth-time validation

`cdk synth` reads the document and refuses to deploy on:

- A tool name that would exceed 64 characters once `targetName___` is prepended — reported with the
  exact remaining budget. **This is the important one**: AgentCore documents that breaching a model's
  tool-spec limit fails in the *data plane*, so the target would create cleanly and calls would fail
  later.
- Missing or non-HTTPS `servers[0].url`, missing `operationId`, duplicate `operationId`
- `oneOf` / `anyOf` / `allOf` / `not` / `discriminator` / `$ref` — unsupported by AgentCore
- `securitySchemes` or `security` in the document — unsupported; outbound auth belongs on the target
- OpenAPI version outside 3.0/3.1

Verified working: a 44-character target name against a 21-character operation fails synth with
`"…___orderapp_cancel_order" is 68 characters, over the 64 limit`.

## Least privilege: read this before production

Passing `sharedSecretArn` lets the stack create the credential provider, which is convenient and
**grants the gateway role wildcard access** to `bedrock-agentcore-identity!*`. CDK warns about it at
synth. The cause is structural: the secret AgentCore creates has no ARN at synth time.

For anything beyond a prototype, create the provider once out of band (see
`../samples/agentcore-target.py`) and reference it:

```ts
existingApiKey: {
  providerArn: 'arn:aws:bedrock-agentcore:…:token-vault/…/apikeycredentialprovider/orderapp-mcp-key',
  secretArn:   'arn:aws:secretsmanager:…:secret:bedrock-agentcore-identity!…',
}
```

With both ARNs literal, the grant is scoped to exactly one secret.

## Not covered here

- **Private connectivity.** If the application is not resolvable from the public internet you need an
  OpenAPI target with a private endpoint and a `routingDomain` over VPC Lattice. Not modelled in this
  stack.
- **OAuth / on-behalf-of.** This stack wires API key auth only, because that is what the .NET
  endpoint can verify today. See "End-user identity" in the root README.
- **Stack tests.** Validation is proven by positive and negative synth runs, not by an assertion
  suite.
