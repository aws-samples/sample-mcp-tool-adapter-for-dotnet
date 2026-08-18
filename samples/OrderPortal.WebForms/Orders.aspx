<%--
  Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
  SPDX-License-Identifier: MIT-0

  A GridView bound to a DataTable, which is how a great deal of long-lived .NET code presents data.
  The same DataTable, returned by the same method, is what the adapter flattens into JSON for the
  get_order_lines tool.
--%>
<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Orders.aspx.cs" Inherits="OrderPortal.WebForms.Orders" %>

<!DOCTYPE html>
<html>
<head runat="server">
    <title>Shipped orders</title>
</head>
<body>
    <form id="form1" runat="server">
        <h1>Shipped orders</h1>

        <asp:GridView ID="OrdersGrid" runat="server" AutoGenerateColumns="true"
                      CellPadding="4" GridLines="Both" />

        <h2>Lines for order
            <asp:Label ID="LinesForOrder" runat="server" /></h2>

        <%-- Bound to a DataTable with no compile-time schema — the interesting case for the adapter. --%>
        <asp:GridView ID="LinesGrid" runat="server" AutoGenerateColumns="true"
                      CellPadding="4" GridLines="Both" />

        <p><a href="Default.aspx">Back</a></p>
    </form>
</body>
</html>
