// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Data;

namespace OrderPortal;

/// <summary>
/// Stands in for the data layer of an existing application.
/// </summary>
/// <remarks>
/// <para>Deliberately shaped like ADO.NET-era code: several methods hand back <see cref="DataTable"/>
/// and one hands back a <see cref="DataSet"/> with several tables, because that is what a great deal
/// of long-lived .NET code actually returns. Those return types are the interesting case for the
/// adapter — they have no compile-time schema, so the result shaper has to flatten them at runtime.</para>
/// <para>The rows are held in memory rather than in a database purely to keep the sample deployable.
/// In a real application these methods would open a <c>SqlConnection</c> and fill the same
/// <c>DataTable</c> from a <c>DataAdapter</c> or <c>DataTable.Load(reader)</c>; nothing above this
/// class would change, and neither would anything the adapter does.</para>
/// </remarks>
public sealed class PortalDataAccess
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly List<Customer> CustomerRows = Enumerable.Range(1, 40).Select(i => new Customer
    {
        Id = i,
        Name = $"Customer {i:D3} Ltd",
        Email = $"accounts{i:D3}@example.com",
        AccountManager = i % 5 == 0 ? null : $"manager{i % 7}@example.com",
        CreditLimit = 500m * ((i % 9) + 1),
        OnCreditHold = i % 11 == 0,
        RegisteredUtc = Epoch.AddDays(-i * 9)
    }).ToList();

    private static readonly List<Order> OrderRows = Enumerable.Range(1, 400).Select(i =>
    {
        var status = (OrderStatus)(i % 6);
        var customer = CustomerRows[i % CustomerRows.Count];
        return new Order
        {
            Id = 10_000 + i,
            CustomerId = customer.Id,
            CustomerEmail = customer.Email,
            Status = status,
            NetTotal = 25.50m + (i % 40) * 12.25m,
            TaxTotal = Math.Round((25.50m + (i % 40) * 12.25m) * 0.2m, 2),
            PlacedUtc = Epoch.AddHours(i * 5),
            ShippedUtc = status is OrderStatus.Shipped or OrderStatus.Delivered
                ? Epoch.AddHours(i * 5 + 36)
                : null,
            CancellationReason = status == OrderStatus.Cancelled ? "Customer request" : null
        };
    }).ToList();

    private static readonly List<Invoice> InvoiceRows = OrderRows
        .Where(o => o.Status is not (OrderStatus.Draft or OrderStatus.Cancelled))
        .Select((o, i) => new Invoice
        {
            Number = $"INV-{o.Id}",
            OrderId = o.Id,
            CustomerId = o.CustomerId,
            Status = (InvoiceStatus)(i % 4),
            GrossTotal = o.NetTotal + o.TaxTotal,
            AmountPaid = (i % 4) switch
            {
                0 => 0m,
                1 => Math.Round((o.NetTotal + o.TaxTotal) / 2, 2),
                _ => o.NetTotal + o.TaxTotal
            },
            IssuedUtc = o.PlacedUtc.AddDays(1),
            DueUtc = o.PlacedUtc.AddDays(31)
        }).ToList();

    private static readonly List<Shipment> ShipmentRows = OrderRows
        .Where(o => o.ShippedUtc.HasValue)
        .Select((o, i) => new Shipment
        {
            TrackingNumber = $"TRK{o.Id}{(char)('A' + i % 26)}",
            OrderId = o.Id,
            Carrier = (ShipmentCarrier)(i % 4),
            Status = o.Status == OrderStatus.Delivered ? "Delivered" : "In transit",
            DispatchedUtc = o.ShippedUtc!.Value,
            DeliveredUtc = o.Status == OrderStatus.Delivered ? o.ShippedUtc.Value.AddDays(2) : null
        }).ToList();

    public IReadOnlyList<Customer> Customers => CustomerRows;
    public IReadOnlyList<Order> Orders => OrderRows;
    public IReadOnlyList<Invoice> Invoices => InvoiceRows;
    public IReadOnlyList<Shipment> Shipments => ShipmentRows;

    /// <summary>
    /// Order lines as a <see cref="DataTable"/>. No compile-time schema, so the adapter discovers the
    /// columns at runtime.
    /// </summary>
    public DataTable GetOrderLines(int orderId)
    {
        var table = new DataTable("OrderLines");
        table.Columns.Add("LineNumber", typeof(int));
        table.Columns.Add("Sku", typeof(string));
        table.Columns.Add("Description", typeof(string));
        table.Columns.Add("Quantity", typeof(int));
        table.Columns.Add("UnitPrice", typeof(decimal));
        table.Columns.Add("DiscountPercent", typeof(decimal));
        // Nullable column, so DBNull reaches the shaper and has to become JSON null.
        table.Columns.Add("SerialNumber", typeof(string));

        var order = OrderRows.FirstOrDefault(o => o.Id == orderId);
        if (order is null) return table;

        var lines = (orderId % 4) + 1;
        for (var line = 1; line <= lines; line++)
        {
            table.Rows.Add(
                line,
                $"SKU-{(orderId + line) % 900 + 100}",
                $"Widget, size {line}",
                line * 2,
                Math.Round(order.NetTotal / lines / (line * 2), 2),
                line == 1 ? 5.0m : 0m,
                line % 2 == 0 ? DBNull.Value : $"SN{orderId}{line}");
        }

        return table;
    }

    /// <summary>Aged debt as a <see cref="DataTable"/>, the classic report shape.</summary>
    public DataTable GetUnpaidInvoices(DateTime? dueBefore, int maxRows)
    {
        var table = new DataTable("UnpaidInvoices");
        table.Columns.Add("InvoiceNumber", typeof(string));
        table.Columns.Add("CustomerName", typeof(string));
        table.Columns.Add("GrossTotal", typeof(decimal));
        table.Columns.Add("Outstanding", typeof(decimal));
        table.Columns.Add("DueUtc", typeof(DateTime));
        table.Columns.Add("DaysOverdue", typeof(int));

        var cutoff = dueBefore ?? Epoch.AddYears(1);

        foreach (var invoice in InvoiceRows
                     .Where(i => i.Status is InvoiceStatus.Open or InvoiceStatus.PartPaid)
                     .Where(i => i.DueUtc <= cutoff)
                     .OrderBy(i => i.DueUtc)
                     .Take(Math.Max(1, maxRows)))
        {
            var customer = CustomerRows.First(c => c.Id == invoice.CustomerId);
            table.Rows.Add(
                invoice.Number,
                customer.Name,
                invoice.GrossTotal,
                invoice.GrossTotal - invoice.AmountPaid,
                invoice.DueUtc,
                Math.Max(0, (int)(Epoch - invoice.DueUtc).TotalDays));
        }

        return table;
    }

    /// <summary>
    /// A multi-table <see cref="DataSet"/> — a single report with several result sets, which the
    /// shaper turns into an object keyed by table name.
    /// </summary>
    public DataSet GetMonthlyReport(int year, int month)
    {
        var set = new DataSet($"MonthlyReport-{year}-{month:D2}");

        var summary = new DataTable("Summary");
        summary.Columns.Add("Metric", typeof(string));
        summary.Columns.Add("Value", typeof(decimal));

        var inMonth = OrderRows
            .Where(o => o.PlacedUtc.Year == year && o.PlacedUtc.Month == month)
            .ToList();

        summary.Rows.Add("OrderCount", inMonth.Count);
        summary.Rows.Add("NetRevenue", inMonth.Sum(o => o.NetTotal));
        summary.Rows.Add("TaxCollected", inMonth.Sum(o => o.TaxTotal));
        summary.Rows.Add("CancelledCount", inMonth.Count(o => o.Status == OrderStatus.Cancelled));
        set.Tables.Add(summary);

        var byStatus = new DataTable("ByStatus");
        byStatus.Columns.Add("Status", typeof(string));
        byStatus.Columns.Add("OrderCount", typeof(int));
        byStatus.Columns.Add("NetRevenue", typeof(decimal));
        foreach (var group in inMonth.GroupBy(o => o.Status).OrderBy(g => g.Key))
            byStatus.Rows.Add(group.Key.ToString(), group.Count(), group.Sum(o => o.NetTotal));
        set.Tables.Add(byStatus);

        var topCustomers = new DataTable("TopCustomers");
        topCustomers.Columns.Add("CustomerName", typeof(string));
        topCustomers.Columns.Add("OrderCount", typeof(int));
        topCustomers.Columns.Add("NetRevenue", typeof(decimal));
        foreach (var group in inMonth.GroupBy(o => o.CustomerId)
                     .OrderByDescending(g => g.Sum(o => o.NetTotal))
                     .Take(5))
        {
            topCustomers.Rows.Add(
                CustomerRows.First(c => c.Id == group.Key).Name,
                group.Count(),
                group.Sum(o => o.NetTotal));
        }
        set.Tables.Add(topCustomers);

        return set;
    }
}
