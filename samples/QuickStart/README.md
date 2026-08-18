# Quick start

Run the adapter and see exactly what a gateway would receive. No AWS account, no IIS, no gateway.

```bash
dotnet run --project samples/QuickStart
```

Then, in another terminal:

```bash
# The sample generates a secret per run and prints it. Either copy it from the banner,
# or set your own before starting, which is easier to script:
#   MCP_SHARED_SECRET=$(openssl rand -hex 24) dotnet run --project samples/QuickStart
export KEY=<the key printed in the banner>
BASE=http://localhost:5099/_mcp

curl -s -H "X-Mcp-Key: $KEY" $BASE/health
curl -s -H "X-Mcp-Key: $KEY" $BASE/openapi.json
curl -s -H "X-Mcp-Key: $KEY" -d '{"id":7}' $BASE/tools/get_order_by_id
curl -s -H "X-Mcp-Key: $KEY" -d '{"query":{"Status":"Shipped","Take":3}}' $BASE/tools/search
```

> **This is a demonstration, not the recommended path for a real .NET 8 application.** For modern
> .NET, use the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). This sample
> exists so the adapter can be exercised without .NET Framework, and to show how little host-specific
> code the core needs — the entire ASP.NET Core integration in `Program.cs` is about forty lines.

## Behaviour worth poking at

| Try | Result |
|---|---|
| Omit `X-Mcp-Key` | `401 missing_credentials` |
| `{"id":"not-a-number"}` | `400` — *"id: cannot convert 'not-a-number' to Int32"* |
| `{"id":"7"}` | Succeeds. A stringified number is coerced, because models routinely send them |
| `tools/cancel_order` | `403 mutation_disabled` — set `AllowMutating = true` to permit it |
| `{"query":{"Take":50}}` | Succeeds with `"truncated":true, "returnedItems":10, "totalItems":50` |
| `{"id":1,"nope":2}` | `400` — unknown arguments are rejected, not ignored |

Every call prints an audit line. Note that argument *names* appear and values do not.

## What the file shows

`Program.cs` has four parts, in order:

1. **`OrderService`** — a stand-in for business logic you already have and are not changing.
2. **`OrderAppTools`** — the one file you add to an application, declaring what to expose.
3. **The host adapter** — translate the framework's request, call `TryHandle`, write the response.
4. **`SystemTextJsonParser`** — the core takes no JSON dependency, so each host supplies a parser.

Only parts 2 and 3 would be new code in a real application, and part 3 is written once per framework.

## Deliberately wrong for production

`AllowInsecureTransport` and `IncludeExceptionDetail` are both enabled so the sample runs over plain
HTTP on localhost and produces debuggable errors. The shared secret is a constant in source. All three
would be wrong in a deployment — the startup banner shows the compatibility checker flagging the
`http://` server URL for exactly this reason.
