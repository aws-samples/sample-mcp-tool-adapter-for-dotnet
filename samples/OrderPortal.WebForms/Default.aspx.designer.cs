// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

// Designer file: declares the controls that Default.aspx marks runat="server", so the code-behind can
// reference them. Visual Studio maintains this automatically when you edit the markup; it is committed
// here by hand so the project builds without Visual Studio, on any operating system.
//
// Nothing about the adapter requires it. It is here because a WebForms project has one.

namespace OrderPortal.WebForms
{
    public partial class Default
    {
        /// <summary>Order number entry.</summary>
        protected global::System.Web.UI.WebControls.TextBox OrderNumber;

        /// <summary>Submits the lookup.</summary>
        protected global::System.Web.UI.WebControls.Button Lookup;

        /// <summary>Receives the rendered result table.</summary>
        protected global::System.Web.UI.WebControls.Literal Result;
    }
}
