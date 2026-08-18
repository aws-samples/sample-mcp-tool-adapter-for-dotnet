// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Linq;
using System.Web.UI;

namespace OrderPortal.WebForms
{
    /// <summary>
    /// Binds a <c>GridView</c> to what the existing services already return.
    /// </summary>
    /// <remarks>
    /// The second grid is the one worth looking at: <see cref="OrderService.GetOrderLines"/> returns a
    /// <c>DataTable</c>, so it has no compile-time schema. WebForms binds it by reflecting over the
    /// columns at runtime; the adapter's result shaper does the equivalent, flattening rows to JSON
    /// objects and turning <c>DBNull</c> into <c>null</c>. Neither needed the method changed.
    /// </remarks>
    public partial class Orders : Page
    {
        private readonly OrderService _orders = new OrderService();

        protected void Page_Load(object sender, EventArgs e)
        {
            if (IsPostBack) return;

            var shipped = _orders.Search(new OrderSearch
            {
                Status = OrderStatus.Shipped,
                PageSize = 10
            });

            OrdersGrid.DataSource = shipped.Items;
            OrdersGrid.DataBind();

            // Whichever order came back first, so the page works without a query string.
            var first = shipped.Items.FirstOrDefault();
            if (first == null) return;

            LinesForOrder.Text = first.Id.ToString();
            LinesGrid.DataSource = _orders.GetOrderLines(first.Id);
            LinesGrid.DataBind();
        }
    }
}
