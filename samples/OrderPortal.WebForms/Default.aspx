<%--
  Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
  SPDX-License-Identifier: MIT-0

  An ordinary WebForms page. It calls OrderService directly, exactly as the MCP tools do — the tools
  are not a parallel implementation, they are the same methods reached over HTTP.
--%>
<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="OrderPortal.WebForms.Default" %>

<!DOCTYPE html>
<html>
<head runat="server">
    <title>Order portal</title>
</head>
<body>
    <form id="form1" runat="server">
        <h1>Order portal</h1>

        <p>
            Order number:
            <asp:TextBox ID="OrderNumber" runat="server" Text="10042" />
            <asp:Button ID="Lookup" runat="server" Text="Look up" OnClick="Lookup_Click" />
        </p>

        <asp:Literal ID="Result" runat="server" />

        <hr />
        <p>
            <a href="Orders.aspx">Browse shipped orders</a>
        </p>
        <p>
            The MCP endpoint for this application is at
            <code><%= McpBasePath %>/openapi.json</code>,
            serving <%= ToolCount %> tools from the same services this page uses.
        </p>
    </form>
</body>
</html>
