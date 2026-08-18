// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Data;
using McpToolAdapter;

namespace OrderPortal;

/// <summary>
/// The complete list of what this application exposes. The only file added to it.
/// </summary>
/// <remarks>
/// <para>Thirteen operations across five existing services. Nothing above this file was written for
/// the adapter, and nothing in it was edited to suit the adapter.</para>
/// <para>This is the file a security reviewer reads. It is deliberately the whole answer to "what can
/// an agent do to this system" — there is no second place to look, no attribute scattered through the
/// business logic, and no convention that quietly includes something.</para>
/// </remarks>
public sealed class PortalTools : ToolRegistry
{
    public override void Configure(IToolBuilder b)
    {
        // ---------- Orders ----------

        b.Expose<OrderService, Order?>(s => s.GetOrder(default(int)))
         .Named("get_order")
         .Describes("Fetch one order by its numeric order ID. Returns null when no such order exists.")
         .Describes("orderId", "Numeric order identifier, for example 10042.");

        b.Expose<OrderService, PagedOrders>(s => s.Search(default(OrderSearch)!))
         .Named("search_orders")
         .Describes("Search orders by customer email, status, placed-date range and minimum value. " +
                    "Returns one page at a time with a total count, so ask for the next page rather " +
                    "than expecting every match at once.");

        b.Expose<OrderService, DataTable>(s => s.GetOrderLines(default(int)))
         .Named("get_order_lines")
         .Describes("List the line items on an order: SKU, description, quantity, unit price and " +
                    "discount. Serial numbers are present only for serialised items.")
         .Describes("orderId", "Numeric order identifier.");

        b.Expose<OrderService, IList<Order>>(s => s.GetCustomerOrders(default(int), default(int)))
         .Named("list_customer_orders")
         .Describes("List a customer's most recent orders, newest first.")
         .Describes("customerId", "Numeric customer identifier.")
         .Describes("maxResults", "How many to return, 1 to 200. Defaults to 20 when omitted or zero.")
         .MaxResultItems(200);

        // ---------- Customers ----------

        b.Expose<CustomerService, Customer?>(s => s.GetCustomer(default(int)))
         .Named("get_customer")
         .Describes("Fetch one customer account by numeric ID, including credit limit and hold status.");

        b.Expose<CustomerService, Customer?>(s => s.FindByEmail(default(string)!))
         .Named("find_customer_by_email")
         .Describes("Find a customer account by its registered accounts-payable email address. " +
                    "Matching is case-insensitive and exact — this is not a fuzzy search.");

        b.Expose<CustomerService, IList<Customer>>(s => s.ListOnCreditHold())
         .Named("list_customers_on_credit_hold")
         .Describes("List every customer currently on credit hold. Takes no arguments.");

        // ---------- Invoices ----------

        b.Expose<InvoiceService, Invoice?>(s => s.GetInvoice(default(string)!))
         .Named("get_invoice")
         .Describes("Fetch one invoice by its number, for example INV-10042, including how much of it " +
                    "has been paid.");

        b.Expose<InvoiceService, DataTable>(s => s.GetUnpaidInvoices(default(DateTime?), default(int)))
         .Named("list_unpaid_invoices")
         .Describes("Aged debt: open and part-paid invoices with the outstanding amount and days " +
                    "overdue, due date ascending.")
         .Describes("dueBefore", "Only include invoices due on or before this date. Omit for all.")
         .Describes("maxRows", "Maximum rows to return.")
         .MaxResultItems(100);

        // ---------- Shipments ----------

        b.Expose<ShipmentService, Shipment?>(s => s.GetByTrackingNumber(default(string)!))
         .Named("get_shipment")
         .Describes("Fetch a shipment by carrier tracking number, including delivery date when delivered.");

        // ShipmentService.TryGetByOrder uses an out parameter and cannot be exposed directly. The
        // wrapper is additive; the original method is untouched.
        b.ExposeStatic<Shipment?>(() => PortalFacade.FindShipment(default(int)))
         .Named("find_shipment_for_order")
         .Describes("Find the shipment raised for an order, or null if it has not shipped yet.");

        // ---------- Reporting ----------
        //
        // ReportingService needs a connection string, so the adapter cannot construct it. Supplying a
        // factory is the supported answer.

        b.Expose<ReportingService, decimal>(s => s.RevenueForMonth(default(int), default(int)))
         .Named("revenue_for_month")
         .Describes("Total net revenue recognised in a given calendar month, excluding tax.")
         .Describes("year", "Four-digit year.")
         .Describes("month", "Month number, 1 to 12.")
         .Using(() => new ReportingService(PortalConfiguration.ReportingConnectionString));

        b.Expose<ReportingService, DataSet>(s => s.MonthlyReport(default(int), default(int)))
         .Named("monthly_report")
         .Describes("Monthly trading report with three sections: overall summary, a breakdown by order " +
                    "status, and the five largest customers by revenue.")
         .Using(() => new ReportingService(PortalConfiguration.ReportingConnectionString));

        // ---------- Mutating ----------
        //
        // Both stay refused until mutation is explicitly enabled, and each had to be declared
        // Mutating() to be exposed at all.

        b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string)!))
         .Named("cancel_order")
         .Describes("Cancel an order that has not yet shipped. Fails if the order has shipped or been " +
                    "delivered.")
         .Describes("reason", "Why it is being cancelled. Recorded against the order.")
         .Mutating();

        b.Expose<OrderService, string>(s => s.FlagForReview(default(int), default(string)!))
         .Named("flag_order_for_review")
         .Describes("Flag an order for manual review by the operations team.")
         .Mutating();
    }
}

/// <summary>Configuration the application already has; read from the environment when deployed.</summary>
public static class PortalConfiguration
{
    public static string ReportingConnectionString =>
        Environment.GetEnvironmentVariable("REPORTING_CONNECTION_STRING")
        ?? "Server=(local);Database=Portal;Integrated Security=true";
}
