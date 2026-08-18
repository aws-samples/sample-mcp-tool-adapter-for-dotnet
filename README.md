# McpToolAdapter

MCP tools from ASP.NET Framework applications — the estate the official MCP C# SDK cannot host.

> **On .NET 8 or newer, use the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
> instead.** It is GA, Microsoft-collaborated, and does this properly for modern .NET. This project
> exists because its ASP.NET integration, `ModelContextProtocol.AspNetCore`, ships only
> `net8.0`/`net9.0`/`net10.0` assets, so it cannot run on .NET Framework — which is exactly where
> WebForms and MVC 5 applications live. The `ModelContextProtocol` core package *does* ship
> `netstandard2.0`; [why that is not enough on its own](#relationship-to-the-official-mcp-c-sdk).

No existing line of code is edited: no `Global.asax` change, no `web.config` handler registration, no
new project, and nothing touched in your business logic. You do add code — see
[what this costs](#what-this-costs-per-application) — but it is additive.

## Try it in one command

```bash
dotnet run --project samples/QuickStart
```

A self-contained ASP.NET Core sample that serves the endpoint and prints ready-to-paste `curl`
commands — no AWS account, no IIS, no gateway. Use it to see the OpenAPI document a gateway receives,
and the behaviour of argument coercion, result caps and the mutation gate. See
[`samples/QuickStart`](samples/QuickStart/README.md).

It is a demonstration host, not the recommended way to add MCP tools to a real .NET 8 application —
for that, use the official SDK as noted above.

## Testing the AgentCore round trip

**Verified end to end.** In us-east-1, AgentCore accepted the emitted document, `tools/list` returned
all 15 operations, and `tools/call` returned real data through gateway → VPC Lattice → private API
Gateway → Lambda → business logic. [`docs/agentcore-test.md`](docs/agentcore-test.md) has the procedure,
the six defects the live test exposed, and how to tell two identical-looking silent failures apart.

It deploys a realistic 15-operation application behind a **private** REST API Gateway — no public
endpoint at all — and has AgentCore reach it over VPC Lattice. No IIS, no certificate, no container
runtime; `cdk synth` runs `dotnet publish` itself:

```bash
cd cdk && npx cdk deploy McpToolAdapterPrivateApp McpToolAdapterGateway
```

## Architecture diagrams

Four Mermaid views in [`docs/architecture.md`](docs/architecture.md): runtime request flow with both
trust boundaries, the internal layering, the path from a code change to a registered tool with its
three validation gates, and the on-behalf-of identity sequence.

## Repository layout

| Path | Contents |
|---|---|
| `src/McpToolAdapter.Core` | `netstandard2.0`, zero dependencies. Registration, schema, binding, dispatch, OpenAPI, AgentCore validation |
| `src/McpToolAdapter.Web` | `net472` `System.Web` host. Drop-in HTTP module and handler |
| `src/McpToolAdapter.Jwt` | Optional. Bearer-token validation for on-behalf-of calls |
| `tests/` | 196 tests, runnable on any OS |
| `cdk/` | AgentCore gateway, targets and credentials as CDK, with synth-time validation |
| `automation/` | Idempotent target reconciler, CLI and Lambda entry points, 30 tests |
| `samples/QuickStart` | Minimal runnable ASP.NET Core demonstration — the fastest way to see it work |
| `samples/OrderPortal` | Realistic 15-operation application, `DataTable`/`DataSet` results, deployed privately |
| `samples/OrderPortal.WebForms` | **The case this repository is for**: the same 15 operations on `net472` WebForms, business logic linked by source from `samples/OrderPortal` to prove it is unchanged |
| `samples/` | The one file you add to an application, the `web.config` block, a generated OpenAPI document |
| `docs/` | Architecture diagrams, AgentCore test procedure, one-pager, executive overview |

## What it does

The application serves an OpenAPI 3.0 document plus JSON invocation endpoints for the methods you
nominate. An MCP gateway consumes that document and turns each `operationId` into an MCP tool.

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
your existing business logic — untouched
```

The gateway is deliberately not part of this SDK. Protocol translation is a solved, purchasable
problem; the adapter into legacy code is not, and that is all this builds.

## What this costs, per application

"No rewrite" is accurate. "No code changes" is not. The real footprint depends entirely on whether
the logic you want to expose is already callable without a page, and that ratio is the single biggest
driver of effort across an estate.

**Case 1 — logic sits in service or manager classes.** One new file (the registry) and four
`web.config` lines. Nothing else. This is the case the rest of this README assumes.

**Case 2 — the signature can't be expressed over JSON.** `out`/`ref` parameters, generic methods,
`object` parameters, or a type with no public parameterless constructor. Startup names the method and
the reason. The fix is an additive wrapper, never an edit to the original:

```csharp
// existing, untouched:  bool TryFind(int id, out Customer customer)
public Customer FindCustomer(int id)
{
    Customer customer;
    return TryFind(id, out customer) ? customer : null;
}
```

**Case 3 — logic lives in `.aspx.cs` codebehind.** Entangled with `Page_Load`, event handlers,
`ViewState` and `Session`, so there is no method to point at. You have to lift the logic into a
callable method first. That is genuine refactoring work, proportional to how tangled the page is, and
this SDK does not reduce it — it only makes the result reachable. Nothing here drives a page or
replays `ViewState`, by design: doing so is brittle and breaks on any markup change.

Before estimating an estate, count which case each candidate operation falls into. A codebehind-heavy
application is not a one-file install.

## Installing into an existing application

**1. Reference the package.** `McpToolAdapter.Web` for `System.Web` applications (WebForms, MVC 5,
classic ASP.NET). This registers an HTTP module at application start via
`PreApplicationStartMethod` — the mechanism ELMAH and MiniProfiler used for drop-in installs. No
config entry, no code change.

**2. Add one registry class.** A minimal illustration is
[`samples/OrderApp.Tools.cs.txt`](samples/OrderApp.Tools.cs.txt); for a complete WebForms application
with 15 operations wired up, see [`samples/OrderPortal.WebForms`](samples/OrderPortal.WebForms/README.md).

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

Registration is explicit and lives in your application rather than as attributes on your business
logic. That keeps existing assemblies untouched — which matters most when the logic is in a shared
library used by several applications — and it gives you one greppable file that is the complete list
of what the application exposes. That file is what a security reviewer reads.

The method body is written as a call with dummy arguments because C# forbids bare method groups in
expression trees. It is never executed; only the `MethodInfo` is read, which is what makes the
registration survive a rename.

**3. Enable it.** See [`samples/web.config.snippet.xml`](samples/web.config.snippet.xml). Minimum:

```xml
<add key="mcp:enabled"      value="true" />
<add key="mcp:namePrefix"   value="orderapp" />
<add key="mcp:sharedSecret" value="a-32-plus-character-random-secret" />
```

`namePrefix` keeps tool names from colliding across applications. **Leave it empty if you are using
Bedrock AgentCore Gateway** — it namespaces by target name already, and doing both double-prefixes.
See [AgentCore Gateway](#amazon-bedrock-agentcore-gateway).

Then point your gateway at `https://your-app/_mcp/openapi.json`.
[`samples/openapi.example.json`](samples/openapi.example.json) is a real document generated from the
sample registry, so you can see what the gateway receives before installing anything.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/_mcp/openapi.json` | OpenAPI 3.0 document; one `operationId` per tool |
| `GET` | `/_mcp/health` | Liveness, tool count, AgentCore compatibility issues |
| `GET` | `/_mcp/tools` | Diagnostic listing with target methods and schemas |
| `POST` | `/_mcp/tools/{operation}` | Invoke, with a JSON object body |

Every operation is a POST with a JSON body, including read-only ones: encoding nested objects into
query strings is lossy and length-limited. Read-only operations carry `x-mutating: false` and say so
in their description.

Responses use one envelope:

```json
{ "ok": true, "tool": "orderapp_get_order_by_id", "result": { … }, "durationMs": 12 }
{ "ok": false, "tool": "…", "error": { "code": "invalid_arguments", "message": "…" } }
```

## Amazon Bedrock AgentCore Gateway

Verified against AgentCore's documented OpenAPI target constraints, with a checker that runs at
application start — `AgentCoreCompatibility.Check`. Set `mcp:agentCoreTargetName` and problems are
logged at startup and reported by `/_mcp/health`. [`samples/agentcore-target.py`](samples/agentcore-target.py)
registers the target.

Four findings from those constraints shaped the implementation:

**Tool names are prefixed with the target name.** AgentCore exposes each operation as
`${targetName}___${operationId}` — three underscores. Two consequences. First, **leave `mcp:namePrefix`
empty when using AgentCore**: the gateway already namespaces per target, and setting both yields
`orderapp___orderapp_get_order`. Second, the target name eats the model's 64-character tool-name
budget, so a 20-character target name leaves 41 for the operation. This is the constraint most worth
catching early, because AgentCore documents only that breaching a model's ToolSpec limit fails **in the
data plane** — the target creates cleanly and calls fail later. The checker does the arithmetic and
names the offenders; the catalog independently rejects any name over 64 at startup.

**No security schemes in the document.** AgentCore does not support specification-level security;
outbound authentication is configured on the target's credential provider. The emitted document
therefore declares no `securitySchemes`, and the checker fails the build if one appears — adding one
would seem helpful and would break target creation.

**IAM (SigV4) outbound auth is not an option here.** It requires a target that natively verifies
SigV4 — API Gateway, Lambda function URLs, or another AgentCore Gateway. An IIS application behind a
load balancer does not, so use **API key** (the shared secret, injected into the `X-Mcp-Key` header)
or OAuth.

**No `oneOf`, `anyOf`, `allOf`, `$ref` or `discriminator`.** The schema generator emits none of them —
only plain types, objects and arrays. The checker searches the whole document for them anyway, so a
custom result shaper can't reintroduce one unnoticed.

Also handled: `servers` must carry the real endpoint URL (an error if missing); templated server URLs
are flagged as the SSRF risk the AgentCore guide warns about; `operationId` is required on every
operation; only `application/json` is emitted.

For applications not resolvable from the public internet, AgentCore supports OpenAPI targets with a
private endpoint and a `routingDomain` over VPC Lattice — nothing in this SDK changes for that.

Infrastructure lives in [`cdk/`](cdk/README.md): one gateway, one target per application, with the
OpenAPI document validated at `cdk synth` so a breaching tool name fails the deploy instead of the
call. Typechecked and synthesised against `aws-cdk-lib` 2.264.0.

## Security posture

Defaults are the safe ones, and the endpoint fails closed rather than serving a weakened version.

- **Off until enabled.** Installing the package adds no reachable endpoint. Before
  `mcp:enabled`, every path under the base returns 404 — indistinguishable from not installed.
- **A shared secret is mandatory.** Enabled without one, the endpoint refuses every request. It is
  compared in fixed time, and HTTPS is required unless explicitly overridden.
- **Read-only by default.** A method must be registered `.Mutating()` to change state, and mutating
  tools stay blocked until `mcp:allowMutating` is set.
- **No exception text.** Failures return `"The operation failed."` Legacy exception messages leak
  connection strings, SQL and internal paths; the detail goes to the audit log instead.
- **Every call is audited** — tool, caller, correlation id, outcome, duration, and argument *names*.
  Values are excluded on purpose: they routinely contain customer data.

### Application authentication: making the app's own auth work through AgentCore

Two modes. API key is a service account — no user identity, authorization enforced at the gateway.
Token exchange carries the real end user through to your existing code.

**Mode 1 — API key (default).** `SharedSecretAuthorizer`. The gateway proves it is the gateway;
nothing more. Legacy code reading `HttpContext.Current.User` finds nothing. Zero code.

**Mode 2 — OAuth on-behalf-of.** Configure the gateway target with
`credentialProviderType: OAUTH` and `grantType: TOKEN_EXCHANGE`. AgentCore Identity exchanges the
agent's inbound token for one scoped to your application's audience, preserving the original user's
`sub` (RFC 8693, or RFC 7523 depending on the provider). Then in the application:

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
`Thread.CurrentPrincipal` for the duration of the call and restores them afterwards — restoration is
not optional, since an `HttpContext` outlives the call and a leaked identity would be a real bug.

`McpToolAdapter.Jwt` is a **separate package** so the core keeps its zero-dependency guarantee for
anyone using API key auth. Validation is delegated to Microsoft's token handler: signature, issuer,
audience and lifetime are enforced and JWKS rotation is handled by `ConfigurationManager`. Nothing is
hand-rolled, because this is exactly the code that is dangerous to get subtly wrong. 15 tests sign
real tokens with real RSA keys and prove each rejection — wrong signing key, wrong audience, wrong
issuer, expired, malformed, missing scope. Signing-key retrieval failure returns **503**, not 401: an
availability problem must not read as "unauthorized".

Two honest caveats.

**Roles usually don't live in the token.** `ClaimsPrincipalMapper.FromClaims` reads `roles`, `role`,
`groups` and `cognito:groups`, but most legacy applications hold the authoritative role table
themselves. Supply your own `PrincipalMapper` that looks them up where they actually live.

**`Session` cannot be faked.** Code reading `Session["CurrentUser"]` is bound to a browser session an
agent does not have, and there is no honest way to synthesise one. That code has to change to take
its inputs as parameters. This is the one part of an application's authentication that does not carry
over.

Mode 2 costs two lines in `Application_Start`, unlike mode 1's zero. A small glue package could make
it config-driven and restore the no-code install; not built yet.

### What AgentCore provides that I earlier said it did not

`SharedSecretAuthorizer` establishes only that the caller is your gateway. It carries no end-user
identity, and legacy methods that read `HttpContext.Current.User` or `Session[...]` find no user when
invoked this way.

That gap is **this SDK's**, not AgentCore's. AgentCore Gateway supports OAuth 2.0 on-behalf-of (OBO)
token exchange through AgentCore Identity, using RFC 8693 token exchange or the RFC 7523 JWT
authorization grant, and the original user identity (`sub`) is preserved across every hop with each
token scoped to its intended audience. On an OpenAPI target that is
`credentialProviderType: OAUTH` with `grantType: TOKEN_EXCHANGE`; `CLIENT_CREDENTIALS` (2LO) and
`AUTHORIZATION_CODE` (3LO) are also available. Inbound, Gateway acts as an OAuth resource server
with a `CUSTOM_JWT` authorizer validating audience, client and scope claims.

So the propagation mechanism exists and is a supported configuration. What is missing is the
receiving end: an `IMcpAuthorizer` that validates the incoming JWT and maps its `sub` onto a
principal your legacy code recognises. **This SDK does not ship one** — correct JWT validation means
JWKS retrieval, key rotation and signature verification, which needs a real library and would break
the core's zero-dependency guarantee. Assign `McpEndpoint.Authorizer` to your own implementation;
`SharedSecretAuthorizer` is the floor, not the destination.

Whether you want per-user propagation at all, or a service account with authorization enforced at
the gateway, is still a design decision — but it is a choice between two supported options, not a
limitation.

## Design notes

**Two assemblies, split along a real seam.** `McpToolAdapter.Core` is `netstandard2.0` and holds
everything worth testing: registration, schema generation, argument binding, invocation, result
shaping, routing, authorization, OpenAPI emission. `McpToolAdapter.Web` is `net472` and holds only the
`HttpContext` adapter. The split means the logic is unit-tested on any OS and a second host is a
few hundred lines.

**Zero package dependencies in the core.** Deliberate and load-bearing: installing this into a
15-year-old application must not break it through transitive dependencies or binding-redirect
conflicts. JSON writing is hand-rolled over an already-normalized tree; parsing uses whatever the
host already has.

**Reflection for discovery, compiled delegates for calls.** Schemas are generated once at startup.
Each method is compiled to a delegate via `Expression.Lambda`, so calls do not pay reflection cost
and real exceptions are not buried in `TargetInvocationException`.

**Payloads are normalized before serialization.** Dates become ISO 8601 rather than
`\/Date(…)\/`; enums become names; `DataTable` and `DataSet` flatten to rows; cyclic graphs
terminate with a placeholder; a throwing getter yields a placeholder instead of failing the call.
Both hosts emit identical bytes.

**Startup fails loudly, all at once.** An unbindable parameter, a duplicate name, a missing
description, an `out` parameter, a target with no usable constructor — each is a build-time error
naming the offending method, and every problem is reported in one pass. A tool that silently fails
to appear is far more expensive to diagnose.

**Result caps are on by default** (200 items). A legacy `GetAllCustomers()` returning 50,000 rows
exhausts the calling model's context window and fails the whole conversation, not just the call.
Truncation is reported in the envelope.

**Descriptions are mandatory.** They are what a model reads to decide whether to call a tool, so an
undescribed tool is either ignored or misused. Turn this off with
`ToolCatalogOptions.RequireDescriptions` if you must.

## Relationship to the official MCP C# SDK

Worth being precise about, because the overlap is real.

`ModelContextProtocol` (24M+ downloads) generates tool schemas from method signatures and hosts an
MCP server. For a .NET 8+ application that is strictly the better choice — it is maintained by the
people who define the protocol.

`ModelContextProtocol.Core` does ship a `netstandard2.0` asset, so a .NET Framework application
*could* reference it, and this project could have been built on its server primitives instead of
generating schemas itself. Two reasons it was not:

- Referencing it brings **20** packages. Measured rather than estimated: a throwaway `netstandard2.0`
  project referencing `ModelContextProtocol.Core` 2.2.0, then `dotnet list package
  --include-transitive`. (The full `ModelContextProtocol` package brings 27; the smaller number is used
  here because it is the one a reader checking this claim would compute.) Among them are
  `System.Text.Json`, `System.Memory`, `System.Buffers`, `System.Runtime.CompilerServices.Unsafe`,
  `System.Collections.Immutable`, `System.IO.Pipelines` and `Microsoft.Extensions.AI.Abstractions` —
  close to a list of the assemblies most likely to cause a binding-redirect conflict in a long-lived
  `System.Web` application. Introducing them is the most likely way to break it on install day. This
  core has zero.
- It speaks MCP directly, which needs Streamable HTTP or SSE. Long-lived connections under
  `System.Web` fight the ASP.NET thread model and do not survive app-pool recycles. Emitting OpenAPI
  and letting the gateway translate keeps every request a plain synchronous round trip.

There is a legitimate alternative that was not taken: AgentCore Gateway also supports **MCP server
targets**, so hosting a real MCP server would remove the OpenAPI document from the picture entirely.
It was rejected on the two grounds above, not because it does not work. If those constraints change,
revisit it. The document is no longer a manual step in any case — the CDK generates it during synth from
the application itself, see [`cdk/README.md`](cdk/README.md).

What is genuinely specific to this project: `System.Web` hosting, zero dependencies, `DataTable` and
legacy return-type shaping, result caps, AgentCore-specific validation, and installation without
editing existing code.

## Requirements and limits

- **IIS integrated pipeline** for the zero-config module: under the classic pipeline, extensionless
  paths never reach managed code. Use `McpHandler` with an explicit `.ashx` there — see the
  `McpHandler` remarks.
- **.NET Framework 4.7.2+** for the `System.Web` host. The core needs only `netstandard2.0`.
- **Not exposable:** generic methods, `out`/`ref` parameters, `object` parameters, and types with no
  public parameterless constructor. Each is rejected at startup with a message and a suggested fix.
- **No streaming.** Requests are synchronous request/response. Long-running operations should return
  a handle and be polled.

## Building

```bash
dotnet build                                                  # all three projects, any OS
dotnet test tests/McpToolAdapter.Core.Tests/McpToolAdapter.Core.Tests.csproj
```

`McpToolAdapter.Web` targets `net472` but compiles anywhere via
`Microsoft.NETFramework.ReferenceAssemblies`. It can only be **run** under IIS on Windows.

## What "MCP endpoint" does and does not mean here

This project exposes **no MCP endpoints**. `/_mcp/...` is a path name, not a protocol claim: those
routes are plain JSON over HTTP, with no `initialize`, no `tools/list`, no JSON-RPC. The MCP surface is
the gateway's, which is the whole point of not building one.

That leaves a seam worth testing: can a real MCP translator consume the document and invoke the tools?
Verified against [FastMCP](https://github.com/jlowin/fastmcp)'s `from_openapi`, an independent
OpenAPI-to-MCP implementation, driven over the MCP protocol against the running quick start:

- `tools/list` returned all three tools, with `required` derived correctly from the schema and the
  descriptions carried through, including the appended "This operation changes state."
- `tools/call get_order_by_id {"id": 7}` returned the order, with the enum as `"Shipped"` and the date
  as ISO 8601.
- `tools/call search {"query": {...}}` bound the nested object and enum correctly.
- `tools/call cancel_order` surfaced the mutation gate as a tool error carrying
  `mutation_disabled` — the refusal reaches the caller rather than being swallowed.

This is evidence the emitted document is consumable by an independent MCP implementation. It is **not**
evidence about AgentCore Gateway specifically, which needs an AWS account to confirm.

## Verified against sources, and what is still assumed

Claims in this repository are split deliberately. These are checked against primary documentation:

| Claim | Source |
|---|---|
| Tool names max 64 chars, pattern `[a-zA-Z0-9_-]+` | Bedrock `ToolSpecification` API reference: "Maximum length of 64. Pattern: `[a-zA-Z0-9_-]+`" |
| AgentCore tools are named `targetName___toolName` | AgentCore developer guide, "Understand how AgentCore Gateway tools are named" |
| `oneOf`/`anyOf`/`allOf` unsupported; spec-level security schemes unsupported; `servers` must be the real endpoint; `operationId` required; only `application/json` fully supported | AgentCore developer guide, OpenAPI feature support table |
| IAM (SigV4) outbound needs a target that verifies SigV4 (API Gateway, Lambda URLs, AgentCore Gateway) | AgentCore developer guide, OpenAPI target authorization strategy |
| AgentCore supports OBO token exchange (RFC 8693 / RFC 7523) preserving `sub` across hops; Gateway inbound is an OAuth resource server with `CUSTOM_JWT` | AgentCore developer guide + "Extending MCP support for AgentCore Gateway" |
| `Request.InputStream` is buffered and preserves `Form`/`Files` for downstream `.aspx` | `HttpRequest.GetBufferedInputStream` remarks, which contrast it with the bufferless variant |
| `PreApplicationStartMethod` ordering is not guaranteed between assemblies | `PreApplicationStartMethodAttribute` remarks |
| `JavaScriptSerializer.MaxJsonLength` default is 2,097,152 characters | `JavaScriptSerializer.MaxJsonLength` reference |

These are **not** verified and should be treated as assumptions until someone runs the host in IIS:

- **`DynamicModuleUtility.RegisterModule` semantics.** `Microsoft.Web.Infrastructure` has no published
  API documentation — the reference pages 404. The zero-config module registration pattern is
  long-established in practice (ELMAH, Glimpse, MiniProfiler) and the package restores and compiles,
  but no primary source was found stating its constraints. If it turns out not to work in a given
  application, `McpHandler` with an explicit `.ashx` is the fallback.
- **IIS integrated pipeline requirement.** The claim that extensionless paths never reach managed
  modules under the classic pipeline is from experience, not a cited document.
- **`additionalProperties: false` acceptability to AgentCore.** It appears in neither the supported nor
  the unsupported column of the feature support table. It is emitted because it usefully rejects
  unknown arguments, and if a target refuses it the failure is immediate at `CreateGatewayTarget`
  rather than silent.
- **`x-mutating` extension.** Custom `x-` extensions are legal OpenAPI and expected to be ignored, but
  no statement was found confirming AgentCore tolerates them.

## Status

| Area | State |
|---|---|
| Schema generation, argument binding, dispatch, result shaping, OpenAPI, AgentCore validation | Complete, 196 unit tests |
| AgentCore round trip | Verified live in us-east-1 — 15 operations through gateway, VPC Lattice, private API Gateway and Lambda |
| Target registration | Verified live via CloudFormation and via the reconciler, 30 tests |
| `net472` / WebForms build | Verified on macOS, Linux and Windows as part of `dotnet build` |
| `System.Web` host **running under IIS** | **Not yet verified** — see below |

The one gap worth planning around: the `System.Web` host has no automated coverage, because its
behaviour is a property of IIS rather than of the code. Module registration, path resolution under a
virtual directory, and buffered body reading are each things this repository asserts and has not
demonstrated. Run [`samples/OrderPortal.WebForms`](samples/OrderPortal.WebForms/README.md) under IIS
Express against your own application shape before relying on them.

That sample narrows the gap without closing it. It is a real `net472` WebForms application exercising
the host, and it builds with the solution on any operating system, so a change that breaks .NET
Framework compatibility fails the build instead of surfacing later — it caught one immediately, shared
business logic using `Math.Clamp`, which does not exist on .NET Framework. What it cannot do here is
*run*: that needs IIS on Windows.

## Security

See [CONTRIBUTING](CONTRIBUTING.md#security-issue-notifications) for more information.

If you believe you have found a security issue, please notify AWS/Amazon Security via the
[vulnerability reporting page](http://aws.amazon.com/security/vulnerability-reporting/) rather than
opening a public issue.

## License

This library is licensed under the MIT-0 License. See the [LICENSE](LICENSE) file.
