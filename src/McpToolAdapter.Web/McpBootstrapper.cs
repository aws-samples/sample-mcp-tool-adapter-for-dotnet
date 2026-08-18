// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Web;
using McpToolAdapter.Web;
using Microsoft.Web.Infrastructure.DynamicModuleHelper;

[assembly: PreApplicationStartMethod(typeof(McpBootstrapper), "Start")]

namespace McpToolAdapter.Web
{
    /// <summary>
    /// Registers <see cref="McpEndpointModule"/> at application start, before the first request.
    /// </summary>
    /// <remarks>
    /// <para><see cref="PreApplicationStartMethodAttribute"/> plus
    /// <see cref="DynamicModuleUtility"/> is the established way to inject an
    /// <see cref="IHttpModule"/> into an existing ASP.NET application with no <c>web.config</c>
    /// edit and no <c>Global.asax</c> change — the same mechanism ELMAH, Glimpse and MiniProfiler
    /// used for drop-in installs. Adding the package reference is the entire installation step.</para>
    /// <para>Because that means an endpoint can appear in an application without anything in the
    /// repository showing it, the module serves nothing until <c>mcp:enabled</c> is set to
    /// true in <c>web.config</c>. Installation is invisible; activation is an auditable diff.</para>
    /// </remarks>
    public static class McpBootstrapper
    {
        public static void Start()
        {
            DynamicModuleUtility.RegisterModule(typeof(McpEndpointModule));
        }
    }
}
