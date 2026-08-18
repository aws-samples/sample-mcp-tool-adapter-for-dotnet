# ASP.NET WebForms on .NET Framework

The case this project exists for. `ModelContextProtocol.AspNetCore` ships `net8.0`, `net9.0` and
`net10.0` assets only, so it cannot run here at all — and retargeting a WebForms application to .NET 8
is the rewrite the whole exercise is meant to avoid.

## What makes this sample worth reading

The four files holding the business logic and the tool registry are **linked by source** from
[`../OrderPortal`](../OrderPortal), not copied:

```xml
<Compile Include="../OrderPortal/Domain.cs"      Link="Shared/Domain.cs" />
<Compile Include="../OrderPortal/DataAccess.cs"  Link="Shared/DataAccess.cs" />
<Compile Include="../OrderPortal/Services.cs"    Link="Shared/Services.cs" />
<Compile Include="../OrderPortal/PortalTools.cs" Link="Shared/PortalTools.cs" />
```

They are byte-for-byte the files that run on .NET 8 in the other sample. The same 15 operations, the same
`ToolRegistry`, the same `DataTable` and `DataSet` return types — with nothing but a different host
around them. If exposing an application over MCP required editing its business logic, this project would
not compile.

## The installation, in full

1. Reference `McpToolAdapter.Web`.
2. Add the `mcp:*` settings to `Web.config`.

That is all. There is **no** `<httpHandlers>` entry, no `<httpModules>` entry, and no line added to
`Application_Start` — `McpToolAdapter.Web` carries a `PreApplicationStartMethod` attribute, so it
registers its own module before any application code runs. Look at [`Web.config`](Web.config) and note
what is absent.

[`Global.asax.cs`](Global.asax.cs) is included anyway, because it shows the two things only the
application can supply: an audit sink that writes where its operators already look, and a start-up check
that reports configuration problems. Both are optional.

## Running it

**Windows and IIS only.** WebForms compiles `.aspx` markup on the server at request time, which needs
`System.Web` on the .NET Framework runtime.

```
Open McpToolAdapter.slnx in Visual Studio, set OrderPortal.WebForms as the startup project, and run it
under IIS Express.
```

Or publish the project's output plus the `.aspx`, `Global.asax` and `Web.config` files to an IIS
application directory, with the assemblies in `bin`.

Then, having set a real `mcp:sharedSecret`:

```
GET  /Default.aspx                       the application's own page
GET  /_mcp/health                        what the endpoint thinks of its own configuration
GET  /_mcp/openapi.json                  the document AgentCore consumes
POST /_mcp/tools/get_order  {"orderId":10042}
```

Every request needs `X-Mcp-Key: <your secret>`.

## What is verified, and what is not

**This project is compile-verified, not run-verified.** It builds on macOS, Linux and Windows via
`Microsoft.NETFramework.ReferenceAssemblies`, and that build is part of `dotnet build McpToolAdapter.slnx`
— so a change that breaks .NET Framework compatibility fails the build immediately. It has not been
executed under IIS. The `System.Web` host it exercises carries the same caveat, recorded in the root
[README](../../README.md).

Building it surfaced one genuine incompatibility worth knowing about, which is the point of having it:
`Services.cs` used `Math.Clamp`, which arrived in .NET Core 2.0 and **does not exist on .NET
Framework**. It now uses `Math.Min`/`Math.Max`. A shared file compiling for both targets is what caught
that; neither sample alone would have.

## Requirements

- **.NET Framework 4.7.2 or later.**
- **The IIS integrated pipeline.** Under the classic pipeline an extensionless path such as
  `/_mcp/health` never reaches managed code, so the self-registering module never sees it. Use
  `McpHandler` with an explicit `.ashx` there instead — see its remarks in `McpToolAdapter.Web`.
