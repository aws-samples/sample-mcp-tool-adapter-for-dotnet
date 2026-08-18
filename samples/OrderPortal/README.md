# Order portal — realistic application sample

A line-of-business ASP.NET Core application, used to test the AgentCore round trip against something
the size and shape of a real system rather than a three-tool demo.

```bash
dotnet run --project samples/OrderPortal
```

```bash
# The sample generates a secret per run and prints it. Either copy it from the banner,
# or set your own before starting, which is easier to script:
#   MCP_SHARED_SECRET=$(openssl rand -hex 24) dotnet run --project samples/OrderPortal
export KEY=<the key printed in the banner>
BASE=http://localhost:5200/_mcp

curl -s -H "X-Mcp-Key: $KEY" $BASE/tools
curl -s -H "X-Mcp-Key: $KEY" -d '{"orderId":10042}' $BASE/tools/get_order_lines
curl -s -H "X-Mcp-Key: $KEY" -d '{"year":2026,"month":1}' $BASE/tools/monthly_report
```

## What makes it realistic

**15 operations across five existing services** — orders, customers, invoices, shipments, reporting.
Enough that the emitted document is 41 KB, which is the scale worth testing a gateway against.

**Return types that have no compile-time schema.** `get_order_lines` and `list_unpaid_invoices` return
`DataTable`; `monthly_report` returns a `DataSet` with three tables. The shaper has to discover columns
at runtime, turn `DBNull` into JSON null, and key a `DataSet` by table name. This is the case that
matters most, because ADO.NET-era code returns these constantly.

**Signatures that resist exposure.** `ShipmentService.TryGetByOrder` uses an `out` parameter and cannot
cross a JSON boundary. It is left exactly as it is; `PortalFacade.FindShipment` wraps it. That is the
pattern — a new method beside the old one, never an edit to it.

**A service the adapter cannot construct.** `ReportingService` needs a connection string, so it is
registered with `.Using(() => ...)`.

**Paging instead of truncation.** `search_orders` returns a page with `TotalMatching` and `HasMore`, so
a caller can ask for the rest. Compare `list_unpaid_invoices`, which is capped and reports
`truncated: true` — both are legitimate, and the difference is worth understanding before choosing.

**Nested arguments.** `search_orders` takes an object containing an enum, a nested date range, a
nullable decimal and paging fields.

## Structure

| File | Role |
|---|---|
| `Domain.cs` | Models and enums. Untouched by the adapter |
| `DataAccess.cs` | ADO.NET-shaped data layer returning `DataTable` / `DataSet` |
| `Services.cs` | The business logic. Nothing here knows the adapter exists |
| `PortalTools.cs` | **The only file added.** The complete list of what is exposed |
| `Program.cs` | Host adapter — the same forty lines as the QuickStart sample |

`PortalTools.cs` is the file a security reviewer reads. It is the whole answer to "what can an agent do
to this system": no attributes scattered through the business logic, and no convention that quietly
includes something.

## Two honest notes

**The data is in memory.** Only so the sample deploys without a database. In a real application these
methods open a `SqlConnection` and fill the same `DataTable`; nothing above `DataAccess.cs` changes, and
neither does anything the adapter does.

**`DataTable` dates carry no timezone.** `DueUtc` comes back as `2026-02-01T14:00:00.0000000` with no
`Z`, because a `DataTable` column has no `DateTimeKind`. Compare `Order.PlacedUtc`, a real
`DateTime` with `Kind.Utc`, which serialises with `Z`. That is a property of the source data rather
than of the adapter, and worth knowing before a model reasons about the difference.

## Deployed privately

`cdk deploy McpToolAdapterPrivateApp` puts this behind a private REST API Gateway reachable only
through an interface VPC endpoint — no public endpoint at all. See
[`docs/agentcore-test.md`](../../docs/agentcore-test.md).
