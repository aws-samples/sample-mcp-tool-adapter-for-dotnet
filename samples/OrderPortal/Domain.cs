// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

namespace OrderPortal;

// The kind of domain that actually lives in a long-running line-of-business application: enums,
// nullable dates, money as decimal, nested query objects, and a couple of shapes that are awkward to
// describe. Nothing here is written for the adapter's benefit.

public enum OrderStatus { Draft, Submitted, Picking, Shipped, Delivered, Cancelled }

public enum InvoiceStatus { Open, PartPaid, Paid, WrittenOff }

public enum ShipmentCarrier { RoyalMail, DHL, FedEx, Collection }

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? AccountManager { get; set; }
    public decimal CreditLimit { get; set; }
    public bool OnCreditHold { get; set; }
    public DateTime RegisteredUtc { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string CustomerEmail { get; set; } = "";
    public OrderStatus Status { get; set; }
    public decimal NetTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public DateTime PlacedUtc { get; set; }

    /// <summary>Null until the order ships — the common nullable-date case.</summary>
    public DateTime? ShippedUtc { get; set; }

    public string? CancellationReason { get; set; }
}

public sealed class Invoice
{
    public string Number { get; set; } = "";
    public int OrderId { get; set; }
    public int CustomerId { get; set; }
    public InvoiceStatus Status { get; set; }
    public decimal GrossTotal { get; set; }
    public decimal AmountPaid { get; set; }
    public DateTime IssuedUtc { get; set; }
    public DateTime DueUtc { get; set; }
}

public sealed class Shipment
{
    public string TrackingNumber { get; set; } = "";
    public int OrderId { get; set; }
    public ShipmentCarrier Carrier { get; set; }
    public string Status { get; set; } = "";
    public DateTime DispatchedUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }
}

/// <summary>Nested query object — a realistic multi-field search rather than a single id.</summary>
public sealed class OrderSearch
{
    public string? CustomerEmail { get; set; }
    public OrderStatus? Status { get; set; }
    public DateRange? Placed { get; set; }
    public decimal? MinimumNetTotal { get; set; }

    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 25;
}

public sealed class DateRange
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

/// <summary>A page of results, so a caller can ask for the rest rather than being silently truncated.</summary>
public sealed class PagedOrders
{
    public IList<Order> Items { get; set; } = new List<Order>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalMatching { get; set; }
    public bool HasMore { get; set; }
}
