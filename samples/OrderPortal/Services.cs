// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Data;

namespace OrderPortal;

// The application's existing business logic. None of it knows the adapter exists, and none of it was
// changed to accommodate it — which is the property the whole project is trying to preserve.

public sealed class OrderService
{
    private readonly PortalDataAccess _data = new();

    public Order? GetOrder(int orderId) =>
        _data.Orders.FirstOrDefault(o => o.Id == orderId);

    /// <summary>
    /// Paged search. Returning a page rather than a list means a caller can ask for the rest, instead
    /// of the adapter silently truncating a large result.
    /// </summary>
    public PagedOrders Search(OrderSearch search)
    {
        search ??= new OrderSearch();

        var query = _data.Orders.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search.CustomerEmail))
            query = query.Where(o => o.CustomerEmail.Equals(search.CustomerEmail, StringComparison.OrdinalIgnoreCase));
        if (search.Status.HasValue)
            query = query.Where(o => o.Status == search.Status.Value);
        if (search.Placed?.From is { } from)
            query = query.Where(o => o.PlacedUtc >= from);
        if (search.Placed?.To is { } to)
            query = query.Where(o => o.PlacedUtc <= to);
        if (search.MinimumNetTotal.HasValue)
            query = query.Where(o => o.NetTotal >= search.MinimumNetTotal.Value);

        var matching = query.OrderBy(o => o.Id).ToList();
        var page = Math.Max(1, search.Page);
        var pageSize = Math.Clamp(search.PageSize <= 0 ? 25 : search.PageSize, 1, 100);
        var items = matching.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new PagedOrders
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalMatching = matching.Count,
            HasMore = page * pageSize < matching.Count
        };
    }

    public DataTable GetOrderLines(int orderId) => _data.GetOrderLines(orderId);

    public IList<Order> GetCustomerOrders(int customerId, int maxResults) =>
        _data.Orders.Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.PlacedUtc)
            .Take(Math.Clamp(maxResults <= 0 ? 20 : maxResults, 1, 200))
            .ToList();

    public void CancelOrder(int orderId, string reason)
    {
        var order = GetOrder(orderId) ?? throw new InvalidOperationException($"No order {orderId}.");
        if (order.Status is OrderStatus.Shipped or OrderStatus.Delivered)
            throw new InvalidOperationException($"Order {orderId} has already shipped and cannot be cancelled.");

        order.Status = OrderStatus.Cancelled;
        order.CancellationReason = reason;
    }

    public string FlagForReview(int orderId, string note)
    {
        var order = GetOrder(orderId) ?? throw new InvalidOperationException($"No order {orderId}.");
        return $"Order {order.Id} flagged for review: {note}";
    }
}

public sealed class CustomerService
{
    private readonly PortalDataAccess _data = new();

    public Customer? GetCustomer(int customerId) =>
        _data.Customers.FirstOrDefault(c => c.Id == customerId);

    public Customer? FindByEmail(string email) =>
        _data.Customers.FirstOrDefault(c => c.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

    public IList<Customer> ListOnCreditHold() =>
        _data.Customers.Where(c => c.OnCreditHold).ToList();
}

public sealed class InvoiceService
{
    private readonly PortalDataAccess _data = new();

    public Invoice? GetInvoice(string invoiceNumber) =>
        _data.Invoices.FirstOrDefault(i =>
            i.Number.Equals(invoiceNumber, StringComparison.OrdinalIgnoreCase));

    public DataTable GetUnpaidInvoices(DateTime? dueBefore, int maxRows) =>
        _data.GetUnpaidInvoices(dueBefore, maxRows);
}

public sealed class ShipmentService
{
    private readonly PortalDataAccess _data = new();

    public Shipment? GetByTrackingNumber(string trackingNumber) =>
        _data.Shipments.FirstOrDefault(s =>
            s.TrackingNumber.Equals(trackingNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Uses an <c>out</c> parameter, which cannot be expressed over JSON.
    /// </summary>
    /// <remarks>
    /// Left exactly as it is. <see cref="PortalFacade.FindShipment"/> wraps it — the additive fix the
    /// README describes, rather than editing working code to suit the adapter.
    /// </remarks>
    public bool TryGetByOrder(int orderId, out Shipment? shipment)
    {
        shipment = _data.Shipments.FirstOrDefault(s => s.OrderId == orderId);
        return shipment is not null;
    }
}

/// <summary>
/// Needs a connection string, so it cannot be constructed by the adapter's default factory.
/// </summary>
/// <remarks>
/// Registered with <c>.Using(() =&gt; ...)</c>, which is the supported way to supply an instance the
/// adapter could not build itself.
/// </remarks>
public sealed class ReportingService
{
    private readonly PortalDataAccess _data = new();

    public ReportingService(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public decimal RevenueForMonth(int year, int month) =>
        _data.Orders.Where(o => o.PlacedUtc.Year == year && o.PlacedUtc.Month == month)
            .Sum(o => o.NetTotal);

    /// <summary>Multi-table report — the shaper turns each table into its own key.</summary>
    public DataSet MonthlyReport(int year, int month) => _data.GetMonthlyReport(year, month);
}

/// <summary>
/// Thin additions that make otherwise-unexposable methods reachable.
/// </summary>
/// <remarks>
/// This is the pattern for the awkward cases: a new method beside the old one, never a change to it.
/// Both wrappers here exist because the original signature cannot cross a JSON boundary.
/// </remarks>
public static class PortalFacade
{
    private static readonly ShipmentService Shipments = new();

    /// <summary>Wraps a <c>bool TryGet(int, out Shipment)</c> as something returnable.</summary>
    public static Shipment? FindShipment(int orderId)
    {
        return Shipments.TryGetByOrder(orderId, out var shipment) ? shipment : null;
    }
}
