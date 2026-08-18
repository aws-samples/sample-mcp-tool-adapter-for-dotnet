# Testing the AgentCore round trip

Deploys the order portal sample behind a **private** REST API Gateway — no public endpoint — registers
it as an AgentCore Gateway target reached over VPC Lattice, and calls tools through the gateway.

**This has been run end to end.** Verified in us-east-1: `tools/list` returned all 15 operations and
`tools/call` returned real data through gateway → Lattice → private API Gateway → Lambda → business
logic. The findings below are what it took to get there; every one of them is now fixed in this
repository, and they are recorded because each cost real debugging time.

## Prerequisites

- An AWS account, and credentials able to create Lambda, API Gateway, VPC, Secrets Manager, IAM,
  Cognito and Bedrock AgentCore resources.
- **`boto3` ≥ 1.43** — earlier versions do not know the `bedrock-agentcore-control` service at all, and
  neither does AWS CLI 2.24. Create a virtualenv: `python3 -m venv automation/.venv &&
  automation/.venv/bin/pip install -U boto3`.
- The .NET 8 SDK. `cdk synth` runs `dotnet publish` itself.
- `cdk bootstrap` in the target account and region.
- `iam:CreateServiceLinkedRole` for `bedrock-agentcore.amazonaws.com`, so AgentCore can create
  `AWSServiceRoleForBedrockAgentCoreGatewayNetwork` and manage the Lattice resources.

**Watch the region.** If `AWS_REGION` is set in your environment it beats `--region` on most tooling,
and CDK will happily target an un-bootstrapped region. Pass it explicitly:
`env AWS_REGION=us-east-1 AWS_DEFAULT_REGION=us-east-1 CDK_DEFAULT_REGION=us-east-1 npx cdk deploy …`

## Step 1 — deploy

Everything, in one command. No values are copied between stacks by hand, and no schema is fetched from
a running endpoint:

```bash
cd cdk && npm install
npx cdk deploy McpToolAdapterPrivateApp McpToolAdapterGateway --require-approval never
```

`McpToolAdapterPrivateApp` creates a VPC with **no** NAT or internet gateway, two isolated subnets, an
execute-api interface endpoint, a private REST API, a generated 48-character secret, and the Lambda.

`McpToolAdapterGateway` creates the gateway, a Cognito user pool as the default inbound identity
provider, the role policy that lets the gateway read its outbound credential, the API key credential
provider, and the target itself — with `PrivateEndpoint` set, pointing at the other stack's VPC.

Two ordering problems are worth understanding, because they are the reason this used to take four
manual steps:

**The document has to exist at synth time, but it belongs to the application.** Synth runs the sample's
own `--dump-openapi`, which produces the document through the same code path that serves
`/_mcp/openapi.json` without binding a port. It cannot drift from the deployed code, and it is cached
against the sample and adapter sources so an unchanged tree does not rebuild. See `cdk/lib/schema.ts`.

**The server URL is not known until deploy.** An API Gateway id does not exist at synth. So the target's
`serverUrl` is a CDK reference to the other stack's API, and `GatewayStack` overrides `servers[0].url`
with it — the URL lands in the template as an `Fn::Join`, not as a string someone pasted.

Target creation takes **3–5 minutes**, because AgentCore provisions a VPC Lattice resource gateway
behind the scenes. CloudFormation waits for `CREATING` → `READY`.

## Step 2 — call tools through the gateway

Get a client-credentials token from the Cognito pool the gateway stack created, then speak MCP to the
gateway URL. Tools appear as `orderportal___<operationId>`, plus AgentCore's own
`x_amz_bedrock_agentcore_search`.

Verified output:

```
tools/list                    16 tools (15 ours + 1 built-in)
orderportal___get_order       {"ok":true,"result":{"Id":10042,"Status":"Draft",
                               "PlacedUtc":"2026-01-10T03:00:00.0000000Z","ShippedUtc":null}}
orderportal___get_order_lines [{"LineNumber":1,"Sku":"SKU-243","SerialNumber":"SN100421"},
                               {"LineNumber":2,...,"SerialNumber":null}]
orderportal___monthly_report  {"Summary":[{"Metric":"OrderCount","Value":146},...],
                               "ByStatus":[...],"TopCustomers":[...]}
orderportal___search_orders   {"Items":[...],"Page":1,"PageSize":2,
                               "TotalMatching":19,"HasMore":true}
orderportal___cancel_order    403 mutation_disabled — reaches the caller as an internal error
```

`DataTable` flattened with `DBNull` → `null`, `DataSet` keyed by table name, enums as names, dates as
ISO 8601 — all of it surviving the gateway's translation.

The `search_orders` call is the interesting one: its single argument is a nested object carrying an enum,
a decimal, a nested date range and paging fields. It round-trips, which is the case that matters for
real business signatures — a gateway that only handled flat scalars would force a rewrite of exactly the
methods you least want to touch.

## What went wrong, and what it taught

Six real defects, in the order they surfaced. All are fixed; they are listed because each presents as
something other than its cause.

**1. `routingDomain` in the wrong place.** The developer guide's example reads as though it sits beside
`managedVpcResource`. It does not — `privateEndpoint` is a tagged union permitting only
`managedVpcResource` or `selfManagedLatticeResource`, and `routingDomain` goes *inside*
`managedVpcResource`. The SDK rejects the sibling form outright. Introspecting the live service model
settled it where the documentation could not.

**2. No egress on the resource gateway's security group.** AgentCore attaches the security group you
pass to the Lattice resource gateway, and that group needs **egress** to reach your endpoint. A single
group with `allowAllOutbound: false` and only an ingress rule silently blackholes everything. The
stack now uses two groups referencing each other. Symptom: target `READY`, `tools/list` fine,
`tools/call` returning a generic internal error, **no log line anywhere**, and zero API Gateway
metrics.

**3. The gateway role had no permissions.** The CDK `Gateway` L2 creates a service role with a correct
trust policy and *no* permissions policy. When CDK also declares the target it grants what is needed;
when anything else creates the target — the reconciler, say — nothing does, and the gateway cannot fetch
the outbound API key. Same symptom as above and equally silent. `GatewayStack` now always grants
`bedrock-agentcore:GetResourceApiKey` and `GetWorkloadAccessToken`, whichever route created the target.

**4. Wrong Lambda payload format.** A Function URL sends payload format 2.0; a REST API sends 1.0.
`AddAWSLambdaHosting(LambdaEventSource.HttpApi)` behind a REST API throws `NullReferenceException`
inside `APIGatewayHttpApiV2ProxyFunction.MarshallRequest` and surfaces as a bare 502. The samples now
take `MCP_LAMBDA_EVENT_SOURCE`, and the private stack sets it to `restapi`.

**5. Over-strict `required` in generated schemas.** AgentCore enforces `required` strictly and rejected
a real call with *"Missing required field(s): '/search/Page'"* — for a property whose C# type
initialises it to `1`. Nested-object members are no longer marked required, because an omitted property
keeps its initialiser. Method parameters still are, since they have nothing to fall back on.

**6. The CDK L2 puts `Bearer ` in front of the API key.** `ApiKeyCredentialLocation.header()` defaults
`credentialPrefix` to `"Bearer "` when you do not pass one, so the gateway sends
`X-Mcp-Key: Bearer <secret>`. The endpoint compares the whole header against the configured secret, so
every call returns 401 while the credential, the network path and the target are all correct. An empty
string is not a fix — CloudFormation gives `CredentialPrefix` a `minLength` of 1, so the property has to
be *absent*, and the L2 offers no way to omit it. `GatewayStack` deletes it with a property deletion
override unless you ask for a prefix explicitly.

This one is worth dwelling on because of how it was found: the only visible clue was the Lambda's
request log reporting `X-Mcp-Key: <redacted, 56 chars>` for a 48-character secret. Redacting a
credential to its *length* rather than to a fixed mask is what turned an opaque 401 into an eight-
character arithmetic problem.

Three behavioural notes, not defects:

**A 403 becomes an opaque error.** The mutation gate returns 403 with a JSON body explaining
`mutation_disabled`. Through AgentCore that reaches the caller as "An internal error occurred", body
discarded. An independent MCP translator surfaced the body. So a blocked mutating tool looks like a
fault rather than a policy decision — worth knowing before someone debugs it as an outage.

**AgentCore validates argument types before your code sees them, and it does not coerce.** The adapter's
binder is deliberately forgiving — it turns `"42"` into `42` — but a call passing `"7"` for an integer
parameter never arrives: the gateway rejects it with *"Field '/customerId' has invalid type: string
found, integer expected"*. The forgiveness still matters for callers that reach the endpoint directly;
through a gateway, the schema is enforced upstream.

**Target updates create a second resource gateway.** Changing the security group produced a new Lattice
resource gateway and left the old one ACTIVE, which held the old security group and blocked the
CloudFormation cleanup of that group for a long time. Expect slow updates and stale resources.

## Diagnosing a silent failure

"An internal error occurred" is what the client sees for almost everything — a blocked mutation, a
missing permission, a blackholed network path and a rejected credential all look the same. The stacks
therefore enable three independent log layers, and between them the ambiguity disappears. Read them in
this order, outside in:

**1. The gateway's own log** — `/aws/vendedlogs/bedrock-agentcore/<gatewayName>`, created by
`enableGatewayLogs` (on by default). This is the highest-value one, and it is the layer people usually do
not know exists. It logs the request body it received, which tool it resolved against which target, and
**the status and body your endpoint returned it**:

```
Executing tool orderportal___get_order_lines from target K7QF2MXJ9B
Client error: API request failed with status: 401 -
  {"ok":false,"error":{"code":"invalid_credentials","message":"The supplied key is not valid."}}
```

That single line distinguishes "never arrived" from "arrived and was refused", which is the fork
everything else hangs off. It is also where the response body your endpoint sent survives — the gateway
discards it before answering the caller, but it writes it here first.

**2. The API Gateway access log** — proves traffic arrived, and by which path. The custom format includes
`vpcEndpointId`, so a request that came through the interface endpoint is distinguishable from one that
did not, plus `integrationStatus`, `integrationLatency` and `integrationError`:

```json
{"path":"/live/_mcp/tools/cancel_order","status":"403","sourceIp":"10.0.0.215",
 "vpcEndpointId":"vpce-0abc123def4567890","integrationStatus":"200","integrationError":"-"}
```

**3. The application's own log** — `MCP_LOG_REQUESTS`. Method, path, source IP, every header, and the
body, followed by the response status, duration and body. Credentials are redacted **to their length**,
not to a fixed mask, which is what caught defect 6 above: `<redacted, 56 chars>` for a 48-character
secret says the value is being prefixed, and nothing else in the system says that.

X-Ray tracing is on for both the API and the Lambda, so a slow call can be followed across the hops. The
gateway's own traces need account-wide Transaction Search, so they are behind `enableGatewayTraces`
(default false).

If all three layers are silent, nothing reached your endpoint at all. Then check, in order:

1. **Any Lambda log group?** If the log group does not exist, nothing reached the function. Note that a
   CDK-declared `LogGroup` is *not* under `/aws/lambda`, so search by name rather than prefix.
2. **Any API Gateway `Count` metric?** Zero means traffic never reached the API — networking or
   credentials, not the application.
3. **Does the gateway role have a permissions policy?** `aws iam list-role-policies`. Empty is the
   answer.
4. **Does the resource gateway's security group have egress?** CDK's `allowAllOutbound: false`
   placeholder rule is ICMP to `255.255.255.255/32` and permits nothing.

## Tear down

```bash
cd cdk && npx cdk destroy McpToolAdapterGateway McpToolAdapterPrivateApp
```

The gateway stack owns the target and the credential provider, so that is all of it. If you attached any
target with the reconciler instead, prune those first — CloudFormation does not know about them:

```bash
cd automation && .venv/bin/python agentcore_reconcile.py applications.json --apply --prune
```

Then check for orphaned Lattice resource gateways: `aws vpc-lattice list-resource-gateways`. They can
outlive the target and block VPC deletion, and they are **not deletable by you** — they are tagged
`BedrockAgentCoreGatewayManaged: true` and the API answers `AccessDeniedException: VPC Resource Gateway
is managed`. AgentCore reaps them on its own schedule, which can take a while. Every target update
provisions a new one and leaves the previous one ACTIVE for a period, so expect more of them than you
have targets.

## Not a production pattern

The shared secret is injected as a Lambda environment variable, so it is readable by anyone with
`lambda:GetFunctionConfiguration`. A production deployment should read it from Secrets Manager at
startup. The API Gateway resource policy allows any principal arriving via the one VPC endpoint, which
is appropriate here but is coarser than an authorizer.

**The logging is set to demonstrate, not to run.** Two settings in particular:

`dataTraceEnabled` on the API Gateway stage writes full request bodies *and headers* to the execution
log — including `X-Mcp-Key` in cleartext. The application's own logging redacts that header to its
length; API Gateway data tracing does not redact anything. Anyone with read access to
`API-Gateway-Execution-Logs_<apiId>/<stage>` can therefore read the shared secret. Turn it off outside a
test account.

`MCP_LOG_REQUESTS` writes request and response bodies to CloudWatch, which for a real application means
business data in logs. It defaults on in the samples so the round trip is visible; it should be off
anywhere real.
