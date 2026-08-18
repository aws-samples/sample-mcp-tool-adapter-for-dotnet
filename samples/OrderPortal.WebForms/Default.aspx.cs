// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Configuration;
using System.Linq;
using System.Web;
using System.Web.UI;
using McpToolAdapter;

namespace OrderPortal.WebForms
{
    /// <summary>
    /// Ordinary WebForms code-behind. Note what is absent: any reference to MCP in the page logic.
    /// </summary>
    /// <remarks>
    /// The page instantiates <see cref="OrderService"/> and calls it, which is what the equivalent page
    /// in a real application would do. The MCP endpoint calls the same methods on the same class. The
    /// two paths cannot drift apart, because there is only one implementation.
    /// <para>The two properties at the bottom exist only so the page can display what the endpoint is
    /// serving. A real application would not have them.</para>
    /// </remarks>
    public partial class Default : Page
    {
        private readonly OrderService _orders = new OrderService();

        protected void Lookup_Click(object sender, EventArgs e)
        {
            if (!int.TryParse(OrderNumber.Text, out var orderId))
            {
                Result.Text = "<p>Enter a numeric order number.</p>";
                return;
            }

            var order = _orders.GetOrder(orderId);

            if (order == null)
            {
                Result.Text = "<p>No such order.</p>";
                return;
            }

            // Server.HtmlEncode on every value: these come from the data layer, and a sample that
            // demonstrated the adapter while modelling an XSS hole would be teaching the wrong lesson.
            Result.Text =
                "<table border='1' cellpadding='4'>" +
                Row("Order", order.Id.ToString()) +
                Row("Customer", order.CustomerEmail) +
                Row("Status", order.Status.ToString()) +
                Row("Net total", order.NetTotal.ToString("N2")) +
                Row("Placed", order.PlacedUtc.ToString("u")) +
                Row("Shipped", order.ShippedUtc?.ToString("u") ?? "not yet") +
                "</table>";
        }

        // HttpUtility rather than Server.HtmlEncode, because Server is an instance member of Page and
        // this is static.
        private static string Row(string label, string value) =>
            "<tr><th align='left'>" + HttpUtility.HtmlEncode(label) + "</th><td>" +
            HttpUtility.HtmlEncode(value ?? string.Empty) + "</td></tr>";

        /// <summary>The configured endpoint path, for display only.</summary>
        protected string McpBasePath =>
            HttpUtility.HtmlEncode(ConfigurationManager.AppSettings["mcp:basePath"] ?? "/_mcp");

        /// <summary>
        /// Counts the tools this application exposes, for display only.
        /// </summary>
        /// <remarks>
        /// Builds a throwaway catalog from the same registry the endpoint uses. Cheap enough for a
        /// sample page; a real application would not do this per request.
        /// </remarks>
        protected int ToolCount
        {
            get
            {
                try
                {
                    return ToolCatalog
                        .BuildFromAssemblies(new ToolCatalogOptions(), typeof(PortalTools).Assembly)
                        .Tools.Count;
                }
                catch (Exception)
                {
                    // Never let a display-only counter take the page down.
                    return 0;
                }
            }
        }
    }
}
