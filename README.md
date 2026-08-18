# McpToolAdapter

Turns methods in an existing ASP.NET Framework application into MCP tools, without rewriting the
application.

> On .NET 8 or newer, use the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
> instead. It is GA, Microsoft-collaborated, and does this properly for modern .NET. This project exists
> because the official SDK's ASP.NET integration, `ModelContextProtocol.AspNetCore`, ships only
> `net8.0`, `net9.0` and `net10.0` assets. It cannot run on .NET Framework, which is where WebForms and
> MVC 5 applications live. The `ModelContextProtocol` core package does ship `netstandard2.0`, but that
> is not sufficient either; [the reasons are below](#relationship-to-the-official-mcp-c-sdk).

You do not edit any existing code. There is no `Global.asax` change, no `web.config` handler
registration, no new project, and nothing to change in your business logic. You do write some new code,
one registry class per application. [What this costs](#what-this-costs-per-application) covers the cases
where it is more than that.

## Try it

```bash
dotnet run --project samples/QuickStart
```

A self-contained ASP.NET Core sample. It serves the endpoint and prints `curl` commands you can paste.
No AWS account, no IIS, no gateway. Use it to look at the OpenAPI document a gateway receives, and to see
how argument coercion, result caps and the mutation gate behave. See
[`samples/QuickStart`](samples/QuickStart/README.md).

For an application on the platform this project is actually for, see
[`samples/OrderPortal.WebForms`](samples/OrderPortal.WebForms/README.md): a `net472` WebForms application
exposing 15 operations.

## What it does

Your application serves an OpenAPI 3.0 document and a JSON invocation endpoint for each method you
nominate. An MCP gateway reads that document and turns every `operationId` into an MCP tool.

```
MCP client (Claude Code, agents, …)
      │  MCP
      ▼
MCP gateway  ──  Bedrock AgentCore Gateway, Azure API Management,
      │          or a self-hosted OpenAPI-to-MCP bridge
      │  HTTPS + JSON
      ▼
/_mcp/openapi.json          ← this SDK, inside your existing app
/_mcp/tools/{operation}
      │
      ▼
your existing business logic, untouched
```

The gateway is not part of this project. Protocol translation is already solved and purchasable. The
adapter into legacy code is not, so that is all this builds.

## Repository layout

| Path | Contents |
|---|---|
| `src/McpToolAdapter.Core` | `netstandard2.0`, zero dependencies. Registration, schema, binding, dispatch, OpenAPI, AgentCore validation |
| `src/McpToolAdapter.Web` | `net472` `System.Web` host. Drop-in HTTP module and handler |
| `src/McpToolAdapter.Jwt` | Optional. Bearer-token validation for on-behalf-of calls |
| `tests/` | 196 tests, runnable on any OS |
| `cdk/` | AgentCore gateway, targets and credentials as CDK, with synth-time validation |
| `automation/` | Idempotent target reconciler, CLI and Lambda entry points, 30 tests |
| `samples/QuickStart` | Minimal ASP.NET Core demonstration, the fastest way to see it work |
| `samples/OrderPortal` | 15-operation application with `DataTable`/`DataSet` results, deployed privately |
| `samples/OrderPortal.WebForms` | The same 15 operations on `net472` WebForms, with the business logic linked by source from `samples/OrderPortal` so it is demonstrably unchanged |
| `docs/` | Architecture diagrams, AgentCore test procedure, one-pager, executive overview |

## Testing the AgentCore round trip

This has been run end to end in us-east-1. AgentCore accepted the emitted document, `tools/list`
returned all 15 operations, and `tools/call` returned real data through the gateway, VPC Lattice, a
private API Gateway, Lambda and into the business logic.

```bash
cd cdk && npx cdk deploy McpToolAdapterPrivateApp McpToolAdapterGateway
```

That deploys the 15-operation sample behind a private REST API Gateway with no public endpoint, and has
AgentCore reach it over VPC Lattice. No IIS, no certificate and no container runtime are involved;
`cdk synth` runs `dotnet publish` and generates the OpenAPI document itself.

[`docs/agentcore-test.md`](docs/agentcore-test.md) has the full procedure, the six defects the live test
exposed, and how to tell apart two failures that look identical from the client.

## Architecture diagrams

[`docs/architecture.md`](docs/architecture.md) has four Mermaid views: the runtime request flow with both
trust boundaries, the internal layering, the path from a code change to a registered tool with its three
validation gates, and the on-behalf-of identity sequence.

## What this costs, per application

"No rewrite" is accurate. "No code changes" is not. How much you write depends on whether the logic you
want to expose is already callable without a page. Across an estate, that ratio drives the effort more
than anything else.

Case 1, logic already sits in service or manager classes. One new file, the registry, plus four
`web.config` lines. Nothing else. The rest of this README assumes this case.

Case 2, the signature cannot be expressed over JSON. This covers `out` and `ref` parameters, generic
methods, `object` parameters, and types with no public parameterless constructor. Startup names the
method and the reason. Fix it by adding a wrapper rather than editing the original:

```csharp
// existing, untouched:  bool TryFind(int id, out Customer customer)
public Customer FindCustomer(int id)
{
    Customer customer;
    return TryFind(id, out customer) ? customer : null;
}
```

Case 3, logic lives in `.aspx.cs` codebehind, tangled up with `Page_Load`, event handlers, `ViewState`
and `Session`. There is no method to point at, so you have to lift the logic into a callable method
first. That is real refactoring work, proportional to how tangled the page is, and this project does not
reduce it. It only makes the result reachable. Nothing here drives a page or replays `ViewState`, because
that approach breaks on any markup change.

Count which case each candidate operation falls into before estimating an estate. A codebehind-heavy
application is not a one-file install.

## Installing into an existing application

1. Reference `McpToolAdapter.Web`. That covers WebForms, MVC 5 and classic ASP.NET. It registers an HTTP
   module at application start using `PreApplicationStartMethod`, the same mechanism ELMAH and
   MiniProfiler used for drop-in installs. No config entry and no code change.

2. Add one registry class. [`samples/OrderApp.Tools.cs.txt`](samples/OrderApp.Tools.cs.txt) is a minimal
   illustration; [`samples/OrderPortal.WebForms`](samples/OrderPortal.WebForms/README.md) is a complete
   application with 15 operations.

```csharp
public sealed class OrderAppTools : ToolRegistry
{
    public override void Configure(IToolBuilder b)
    {
        b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
         .Describes("Fetch a single order by its numeric order ID.");

        b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string)))
         .Describes("Cancel an order that has not yet shipped.")
         .Mutating();
    }
}
```

Registration is explicit, and it lives in your application rather than as attributes on your business
logic. That keeps existing assemblies untouched, which matters most when the logic sits in a shared
library used by several applications. It also gives you one file that is the complete list of what the
application exposes, which is what a security reviewer will want to read.

The method body is written as a call with dummy arguments because C# does not allow bare method groups in
expression trees. It is never executed. Only the `MethodInfo` is read, so the registration survives a
rename.

3. Enable it. [`samples/web.config.snippet.xml`](samples/web.config.snippet.xml) has the full set. The
   minimum is:

```xml
<add key="mcp:enabled"      value="true" />
<add key="mcp:namePrefix"   value="orderapp" />
<add key="mcp:sharedSecret" value="a-32-plus-character-random-secret" />
```

`namePrefix` stops tool names colliding across applications. Leave it empty if you are using Bedrock
AgentCore Gateway, which namespaces by target name already; setting both double-prefixes every tool. See
[AgentCore Gateway](#amazon-bedrock-agentcore-gateway).

Then point your gateway at `https://your-app/_mcp/openapi.json`.
[`samples/openapi.example.json`](samples/openapi.example.json) is a real document generated from the
sample registry, so you can see what a gateway receives before installing anything.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/_mcp/openapi.json` | OpenAPI 3.0 document, one `operationId` per tool |
| `GET` | `/_mcp/health` | Liveness, tool count, AgentCore compatibility issues |
| `GET` | `/_mcp/tools` | Diagnostic listing with target methods and schemas |
| `POST` | `/_mcp/tools/{operation}` | Invoke, with a JSON object body |

Every operation is a POST with a JSON body, including the read-only ones, because encoding nested objects
into query strings is lossy and length-limited. Read-only operations carry `x-mutating: false` and say so
in their description.

Responses use one envelope:

```json
{ "ok": true, "tool": "orderapp_get_order_by_id", "result": { … }, "durationMs": 12 }
{ "ok": false, "tool": "…", "error": { "code": "invalid_arguments", "message": "…" } }
```

## Amazon Bedrock AgentCore Gateway

The emitted document is checked against AgentCore's documented OpenAPI target constraints at application
start, by `AgentCoreCompatibility.Check`. Set `mcp:agentCoreTargetName` and any problems are logged at
startup and reported by `/_mcp/health`.
[`samples/agentcore-target.py`](samples/agentcore-target.py) registers a target.

Four of those constraints shaped the implementation.

Tool names are prefixed with the target name. AgentCore exposes each operation as
`${targetName}___${operationId}`, with three underscores. Two things follow. First, leave
`mcp:namePrefix` empty when using AgentCore, or you get `orderapp___orderapp_get_order`. Second, the
target name spends part of the model's 64-character tool-name budget, so a 20-character target name
leaves 41 for the operation. This is the constraint most worth catching early: AgentCore documents that
breaching a model's ToolSpec limit fails in the data plane, so the target creates cleanly and calls fail
later. The checker does the arithmetic and names the offenders, and the catalog independently rejects any
name over 64 characters at startup.

No security schemes in the document. AgentCore does not support specification-level security; outbound
authentication is configured on the target's credential provider. The emitted document declares no
`securitySchemes`, and the checker fails the build if one appears. Adding one looks helpful and breaks
target creation.

IAM (SigV4) outbound auth is not usable here. It requires a target that verifies SigV4 natively, meaning
API Gateway, Lambda function URLs, or another AgentCore Gateway. An IIS application behind a load
balancer does not, so use an API key (the shared secret, sent in the `X-Mcp-Key` header) or OAuth.

No `oneOf`, `anyOf`, `allOf`, `$ref` or `discriminator`. The schema generator emits none of these, only
plain types, objects and arrays. The checker searches the whole document for them anyway, so a custom
result shaper cannot reintroduce one unnoticed.

Also handled: `servers` must carry the real endpoint URL, and its absence is an error; templated server
URLs are flagged as the SSRF risk the AgentCore guide warns about; `operationId` is required on every
operation; only `application/json` is emitted.

Applications that are not resolvable from the public internet can be reached by an OpenAPI target with a
private endpoint and a `routingDomain` over VPC Lattice. Nothing in this project changes for that case.

Infrastructure is in [`cdk/`](cdk/README.md): one gateway, one target per application, with the OpenAPI
document validated at `cdk synth` so a breaching tool name fails the deploy rather than the call.
Typechecked and synthesised against `aws-cdk-lib` 2.264.0.

## Security posture

The defaults are the safe ones, and the endpoint fails closed rather than serving a weakened version.

- Off until enabled. Installing the package adds no reachable endpoint. Until `mcp:enabled` is set,
  every path under the base returns 404, indistinguishable from not installed.
- A shared secret is mandatory. If the endpoint is enabled without one it refuses every request. The
  secret is compared in fixed time, and HTTPS is required unless explicitly overridden.
- Read-only by default. A method must be registered `.Mutating()` to change state, and mutating tools
  stay blocked until `mcp:allowMutating` is set.
- No exception text in responses. Failures return `"The operation failed."` Legacy exception messages
  leak connection strings, SQL and internal paths, so the detail goes to the audit log instead.
- Every call is audited: tool, caller, correlation id, outcome, duration, and argument names. Values are
  excluded, because they routinely contain customer data.

### Authentication, and carrying the end user through

There are two modes. An API key is a service account with no user identity, where authorization is
enforced at the gateway. Token exchange carries the real end user through to your existing code.

Mode 1, API key. This is the default, `SharedSecretAuthorizer`. The gateway proves it is the gateway and
nothing more. Legacy code reading `HttpContext.Current.User` will find nothing. It needs no code.

Mode 2, OAuth on-behalf-of. Configure the gateway target with `credentialProviderType: OAUTH` and
`grantType: TOKEN_EXCHANGE`. AgentCore Identity exchanges the agent's inbound token for one scoped to
your application's audience, preserving the original user's `sub` under RFC 8693, or RFC 7523 depending
on the provider. `CLIENT_CREDENTIALS` (2LO) and `AUTHORIZATION_CODE` (3LO) are also available. Inbound,
Gateway acts as an OAuth resource server with a `CUSTOM_JWT` authorizer validating audience, client and
scope claims. In the application:

```csharp
// Application_Start
McpEndpoint.Authorizer = new JwtBearerAuthorizer(new JwtBearerOptions {
    DiscoveryUrl = "https://your-idp/.well-known/openid-configuration",
    Audience     = "orderapp",
    RequiredScope = "tools.invoke",
});

// Establish the principal your existing authorization checks already read.
McpEndpoint.PrincipalMapper = ClaimsPrincipalMapper.FromClaims;
```

With those two lines, `User.Identity.Name` and `User.IsInRole("Approver")` inside your business logic
behave as they do for a browser request. `PrincipalScope` sets `HttpContext.Current.User` and
`Thread.CurrentPrincipal` for the duration of the call and restores them afterwards. The restore matters:
an `HttpContext` outlives the call, and a leaked identity would be a real bug.

`McpToolAdapter.Jwt` is a separate package so the core keeps its zero-dependency guarantee for anyone
using API key auth. Validation is delegated to Microsoft's token handler, which enforces signature,
issuer, audience and lifetime, and handles JWKS rotation through `ConfigurationManager`. None of it is
hand-rolled, because this is the code that is dangerous to get subtly wrong. 15 tests sign real tokens
with real RSA keys and check each rejection: wrong signing key, wrong audience, wrong issuer, expired,
malformed, missing scope. Failure to retrieve a signing key returns 503 rather than 401, since an
availability problem should not read as "unauthorized".

Two caveats.

Roles usually do not live in the token. `ClaimsPrincipalMapper.FromClaims` reads `roles`, `role`,
`groups` and `cognito:groups`, but most legacy applications hold the authoritative role table
themselves. Supply your own `PrincipalMapper` that looks them up where they actually live.

`Session` cannot be faked. Code reading `Session["CurrentUser"]` depends on a browser session that an
agent does not have, and there is no honest way to synthesise one. That code has to change to take its
inputs as parameters. It is the one part of an application's authentication that does not carry over.

Mode 2 costs two lines in `Application_Start` where mode 1 costs none. A small glue package could make it
config-driven and restore the no-code install. Not built yet.

## Design notes

Two assemblies, split along a real seam. `McpToolAdapter.Core` is `netstandard2.0` and holds everything
worth testing: registration, schema generation, argument binding, invocation, result shaping, routing,
authorization and OpenAPI emission. `McpToolAdapter.Web` is `net472` and holds only the `HttpContext`
adapter. Because of that split the logic is unit-tested on any OS, and a second host is a few hundred
lines.

Zero package dependencies in the core. Installing this into a fifteen-year-old application must not break
it through transitive dependencies or binding-redirect conflicts, and that constraint drives several
decisions below. JSON writing is hand-rolled over an already-normalized tree. Parsing uses whatever the
host already has.

Reflection for discovery, compiled delegates for calls. Schemas are generated once at startup. Each
method is compiled to a delegate with `Expression.Lambda`, so calls do not pay a reflection cost and real
exceptions are not buried inside `TargetInvocationException`.

Payloads are normalized before serialization. Dates become ISO 8601 rather than `\/Date(…)\/`, enums
become names, `DataTable` and `DataSet` flatten to rows, cyclic graphs terminate with a placeholder, and
a getter that throws yields a placeholder instead of failing the call. Both hosts emit identical bytes.

Startup fails loudly, and reports everything at once. An unbindable parameter, a duplicate name, a
missing description, an `out` parameter, or a target with no usable constructor is a startup error naming
the offending method. All problems are reported in one pass, because a tool that silently fails to appear
is expensive to diagnose.

Result caps are on by default, at 200 items. A legacy `GetAllCustomers()` returning 50,000 rows will
exhaust the calling model's context window and fail the whole conversation, not just the call. Truncation
is reported in the envelope.

Descriptions are mandatory. A model reads them to decide whether to call a tool, so an undescribed tool
is either ignored or misused. `ToolCatalogOptions.RequireDescriptions` turns this off if you need it.

## Relationship to the official MCP C# SDK

The overlap is real, so it is worth being precise.

`ModelContextProtocol` has more than 24M downloads. It generates tool schemas from method signatures and
hosts an MCP server. For a .NET 8 or newer application it is the better choice, maintained by the people
who define the protocol.

`ModelContextProtocol.Core` does ship a `netstandard2.0` asset, so a .NET Framework application could
reference it, and this project could have been built on its server primitives instead of generating
schemas itself. Two reasons it was not.

Referencing it brings 20 packages. That is measured, not estimated: a throwaway `netstandard2.0` project
referencing `ModelContextProtocol.Core` 2.2.0, then `dotnet list package --include-transitive`. The full
`ModelContextProtocol` package brings 27; the smaller number is quoted here because it is the one a
reader checking the claim would compute. Among them are `System.Text.Json`, `System.Memory`,
`System.Buffers`, `System.Runtime.CompilerServices.Unsafe`, `System.Collections.Immutable`,
`System.IO.Pipelines` and `Microsoft.Extensions.AI.Abstractions`, which is close to a list of the
assemblies most likely to cause a binding-redirect conflict in a long-lived `System.Web` application.
This core has zero.

It also speaks MCP directly, which needs Streamable HTTP or SSE. Long-lived connections under
`System.Web` fight the ASP.NET thread model and do not survive app-pool recycles. Emitting OpenAPI and
letting the gateway translate keeps every request a plain synchronous round trip.

There is a legitimate alternative that was not taken. AgentCore Gateway also supports MCP server targets,
so hosting a real MCP server would remove the OpenAPI document from the picture. It was rejected on the
two grounds above, not because it does not work, and it is worth revisiting if those constraints change.
The document is not a manual step in any case; the CDK generates it during synth from the application
itself, see [`cdk/README.md`](cdk/README.md).

What is specific to this project: `System.Web` hosting, zero dependencies, `DataTable` and legacy
return-type shaping, result caps, AgentCore-specific validation, and installation without editing
existing code.

## Requirements and limits

- The IIS integrated pipeline, for the zero-config module. Under the classic pipeline, extensionless
  paths never reach managed code. Use `McpHandler` with an explicit `.ashx` there; see the `McpHandler`
  remarks.
- .NET Framework 4.7.2 or later for the `System.Web` host. The core needs only `netstandard2.0`.
- Not exposable: generic methods, `out` and `ref` parameters, `object` parameters, and types with no
  public parameterless constructor. Each is rejected at startup with a message and a suggested fix.
- No streaming. Requests are synchronous request/response. Long-running operations should return a handle
  and be polled.

## Building

```bash
dotnet build                                                  # every project, any OS
dotnet test tests/McpToolAdapter.Core.Tests/McpToolAdapter.Core.Tests.csproj
```

`McpToolAdapter.Web` targets `net472` but compiles anywhere via
`Microsoft.NETFramework.ReferenceAssemblies`. It can only be run under IIS on Windows.

## This is not an MCP server

`/_mcp/...` is a path name, not a protocol claim. Those routes are plain JSON over HTTP: no `initialize`,
no `tools/list`, no JSON-RPC. The MCP surface belongs to the gateway, which is the reason for not
building one here.

That leaves a seam worth testing. Can a real MCP translator read the document and invoke the tools? This
was checked against [FastMCP](https://github.com/jlowin/fastmcp)'s `from_openapi`, an independent
OpenAPI-to-MCP implementation, driven over the MCP protocol against the running quick start:

- `tools/list` returned all three tools, with `required` derived correctly from the schema and the
  descriptions carried through, including the appended "This operation changes state."
- `tools/call get_order_by_id {"id": 7}` returned the order, with the enum as `"Shipped"` and the date as
  ISO 8601.
- `tools/call search {"query": {...}}` bound the nested object and enum correctly.
- `tools/call cancel_order` surfaced the mutation gate as a tool error carrying `mutation_disabled`, so
  the refusal reaches the caller instead of being swallowed.

That is evidence the emitted document is consumable by an independent MCP implementation. It says nothing
about AgentCore Gateway specifically, which is covered in
[`docs/agentcore-test.md`](docs/agentcore-test.md).

## Sources checked, and what is still assumed

Claims in this repository are split on purpose. These are checked against primary documentation:

| Claim | Source |
|---|---|
| Tool names max 64 chars, pattern `[a-zA-Z0-9_-]+` | Bedrock `ToolSpecification` API reference: "Maximum length of 64. Pattern: `[a-zA-Z0-9_-]+`" |
| AgentCore tools are named `targetName___toolName` | AgentCore developer guide, "Understand how AgentCore Gateway tools are named" |
| `oneOf`/`anyOf`/`allOf` unsupported; spec-level security schemes unsupported; `servers` must be the real endpoint; `operationId` required; only `application/json` fully supported | AgentCore developer guide, OpenAPI feature support table |
| IAM (SigV4) outbound needs a target that verifies SigV4 (API Gateway, Lambda URLs, AgentCore Gateway) | AgentCore developer guide, OpenAPI target authorization strategy |
| AgentCore supports OBO token exchange (RFC 8693 / RFC 7523) preserving `sub` across hops; Gateway inbound is an OAuth resource server with `CUSTOM_JWT` | AgentCore developer guide, and "Extending MCP support for AgentCore Gateway" |
| `ModelContextProtocol.AspNetCore` 2.2.0 ships `net8.0`, `net9.0` and `net10.0` lib assets only | The published package on nuget.org |
| `Request.InputStream` is buffered and preserves `Form`/`Files` for downstream `.aspx` | `HttpRequest.GetBufferedInputStream` remarks, which contrast it with the bufferless variant |
| `PreApplicationStartMethod` ordering is not guaranteed between assemblies | `PreApplicationStartMethodAttribute` remarks |
| `JavaScriptSerializer.MaxJsonLength` default is 2,097,152 characters | `JavaScriptSerializer.MaxJsonLength` reference |

These are not verified, and should be treated as assumptions until someone runs the host in IIS:

- `DynamicModuleUtility.RegisterModule` semantics. `Microsoft.Web.Infrastructure` has no published API
  documentation; the reference pages 404. The zero-config module registration pattern is long-established
  in practice, in ELMAH, Glimpse and MiniProfiler, and the package restores and compiles, but no primary
  source was found stating its constraints. If it does not work in a given application, `McpHandler` with
  an explicit `.ashx` is the fallback.
- The IIS integrated pipeline requirement. The claim that extensionless paths never reach managed modules
  under the classic pipeline comes from experience, not a cited document.
- Whether AgentCore accepts `additionalProperties: false`. It appears in neither the supported nor the
  unsupported column of the feature support table. It is emitted because it usefully rejects unknown
  arguments, and if a target refuses it the failure is immediate at `CreateGatewayTarget` rather than
  silent.
- Whether AgentCore tolerates the `x-mutating` extension. Custom `x-` extensions are legal OpenAPI and
  are expected to be ignored, but no statement was found confirming it.

## Status

| Area | State |
|---|---|
| Schema generation, argument binding, dispatch, result shaping, OpenAPI, AgentCore validation | Complete, 196 unit tests |
| AgentCore round trip | Verified live in us-east-1: 15 operations through gateway, VPC Lattice, private API Gateway and Lambda |
| Target registration | Verified live through CloudFormation and through the reconciler, 30 tests |
| `net472` / WebForms build | Verified on macOS, Linux and Windows as part of `dotnet build` |
| `System.Web` host running under IIS | Not yet verified, see below |

The `System.Web` host has no automated coverage, because what it does is a property of IIS rather than of
the code. Module registration, path resolution under a virtual directory, and buffered body reading are
all asserted here and not demonstrated. Run
[`samples/OrderPortal.WebForms`](samples/OrderPortal.WebForms/README.md) under IIS Express against your
own application's shape before relying on them.

That sample narrows the gap without closing it. It is a real `net472` WebForms application exercising the
host, and it builds with the solution on any operating system, so a change that breaks .NET Framework
compatibility fails the build instead of surfacing later. It caught one immediately: shared business
logic using `Math.Clamp`, which does not exist on .NET Framework. What it cannot do outside Windows is
run.

## Security

See [CONTRIBUTING](CONTRIBUTING.md#security-issue-notifications) for more information.

If you believe you have found a security issue, please notify AWS/Amazon Security via the
[vulnerability reporting page](http://aws.amazon.com/security/vulnerability-reporting/) rather than
opening a public issue.

## License

This library is licensed under the MIT-0 License. See the [LICENSE](LICENSE) file.
