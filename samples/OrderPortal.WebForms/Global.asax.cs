// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Diagnostics;
using System.Web;
using McpToolAdapter.Web;

namespace OrderPortal.WebForms
{
    /// <summary>
    /// Application start-up. Optional as far as the adapter is concerned.
    /// </summary>
    /// <remarks>
    /// Worth being clear about what is and is not required here.
    /// <para><b>Not required:</b> registering the endpoint. The
    /// <c>McpToolAdapter.Web</c> assembly carries a <c>PreApplicationStartMethod</c> attribute, so
    /// referencing it is enough — the HTTP module registers itself before this file runs. There is no
    /// <c>web.config</c> handler entry and no line to add to <c>Application_Start</c>. That is what makes
    /// installation a package reference and a configuration block rather than a code change.</para>
    /// <para><b>What this file is actually for:</b> the hooks that only the application can supply — an
    /// audit sink that matches how it already logs, and a startup check that surfaces configuration
    /// problems where its operators will see them.</para>
    /// </remarks>
    public class Global : HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            // Route the adapter's own diagnostics wherever this application already sends them.
            McpEndpoint.Log = message => Trace.WriteLine("[mcp] " + message);

            // One line per tool invocation. Deliberately wired to the application's logging rather than
            // being written by the adapter, so it lands in the same place as everything else operators
            // already read.
            McpEndpoint.Audit = entry => Trace.WriteLine(
                string.Format(
                    "[mcp] tool={0} caller={1} ok={2} {3}ms args=[{4}]",
                    entry.ToolName,
                    entry.Caller ?? "-",
                    entry.Succeeded,
                    entry.DurationMilliseconds,
                    string.Join(",", entry.ArgumentNames)));

            // Configuration problems are reported rather than thrown.
            //
            // The endpoint validates itself at start-up — every problem at once, not the first one — and
            // a broken MCP configuration must never stop the application serving its own pages. That
            // trade-off is the whole reason these are surfaced as a list instead of an exception.
            foreach (var problem in McpEndpoint.StartupErrors)
            {
                Trace.TraceError("[mcp] configuration problem: " + problem);
            }
        }
    }
}
