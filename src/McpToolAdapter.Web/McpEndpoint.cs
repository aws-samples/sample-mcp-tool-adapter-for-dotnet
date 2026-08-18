// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Web.Compilation;
using System.Web.Script.Serialization;
using McpToolAdapter.Dispatch;
using McpToolAdapter.Hosting;

namespace McpToolAdapter.Web
{
    /// <summary>
    /// Configuration surface and lazily built runtime for the endpoint.
    /// </summary>
    /// <remarks>
    /// Set any of the hooks here from <c>Application_Start</c> when the defaults are not enough.
    /// Nothing needs to be set for the common case: a <see cref="ToolRegistry"/> anywhere in the
    /// application's own assemblies is discovered automatically.
    /// </remarks>
    public static class McpEndpoint
    {
        private static readonly object Gate = new object();
        private static McpRuntime _runtime;

        /// <summary>Adjusts catalog options — instance factory, schema depth, extra result shapers.</summary>
        public static Action<ToolCatalogOptions> ConfigureCatalog { get; set; }

        /// <summary>Adjusts endpoint options after they are read from configuration.</summary>
        public static Action<McpEndpointOptions> ConfigureEndpoint { get; set; }

        /// <summary>
        /// Replaces shared-secret authorization — with OAuth bearer validation, a client-certificate
        /// check, or an internal SSO token. This is the extension point that matters most.
        /// </summary>
        public static IMcpAuthorizer Authorizer { get; set; }

        /// <summary>Receives one entry per tool call. Wire this to the application's existing logger.</summary>
        public static Action<ToolAuditEntry> Audit { get; set; }

        /// <summary>Receives diagnostic messages, notably catalog build failures. Defaults to <see cref="Trace"/>.</summary>
        public static Action<string> Log { get; set; }

        /// <summary>
        /// Turns a call's verified claims into a principal, established on
        /// <c>HttpContext.Current.User</c> and <c>Thread.CurrentPrincipal</c> for the duration of the
        /// invocation so existing authorization checks inside the business logic keep working.
        /// </summary>
        /// <remarks>
        /// Null by default: with no mapper, tools run as whatever identity the application already had,
        /// which is the correct behaviour for API-key (service-account) calls that carry no user.
        /// Set it to <see cref="ClaimsPrincipalMapper.FromClaims"/> for token-based roles, or to your
        /// own function to look roles up in the application's own store.
        /// </remarks>
        public static Func<ToolCallContext, IPrincipal> PrincipalMapper { get; set; }

        /// <summary>Overrides which assemblies are scanned for registries.</summary>
        public static Func<IEnumerable<Assembly>> RegistryAssemblies { get; set; }

        /// <summary>
        /// Problems found while building the catalog, or empty when it built cleanly. Surface this
        /// from an existing health page: a misconfigured catalog serves no tools at all, and this is
        /// how an operator finds out why.
        /// </summary>
        public static IReadOnlyList<string> StartupErrors
        {
            get { return Runtime.StartupErrors; }
        }

        /// <summary>Discards the cached runtime so the next request rebuilds it. For tests and config reloads.</summary>
        public static void Reset()
        {
            lock (Gate) _runtime = null;
        }

        internal static McpRuntime Runtime
        {
            get
            {
                var current = _runtime;
                if (current != null) return current;

                lock (Gate)
                {
                    if (_runtime == null) _runtime = McpRuntime.Build();
                    return _runtime;
                }
            }
        }

        internal static void Report(string message)
        {
            var log = Log;
            if (log != null)
            {
                try
                {
                    log(message);
                    return;
                }
                catch
                {
                    // Fall through to Trace rather than let a logging failure escape.
                }
            }

            Trace.WriteLine("[McpToolAdapter] " + message);
        }
    }

    /// <summary>
    /// The built endpoint: options, catalog, dispatcher and processor, or the reasons there is none.
    /// </summary>
    /// <remarks>
    /// Built once on first matching request rather than at application start, so a catalog problem
    /// cannot stop the host application from serving its own pages. Failures are cached, so a broken
    /// catalog is not rebuilt on every request.
    /// </remarks>
    internal sealed class McpRuntime
    {
        private McpRuntime(McpEndpointOptions options, McpRequestProcessor processor, IReadOnlyList<string> startupErrors)
        {
            Options = options;
            Processor = processor;
            StartupErrors = startupErrors;
        }

        public McpEndpointOptions Options { get; }

        /// <summary>Null when the catalog failed to build.</summary>
        public McpRequestProcessor Processor { get; }

        public IReadOnlyList<string> StartupErrors { get; }

        public static McpRuntime Build()
        {
            var options = ReadOptions();

            if (!options.Enabled)
                return new McpRuntime(options, null, new string[0]);

            try
            {
                var catalogOptions = new ToolCatalogOptions
                {
                    NamePrefix = options.NamePrefix,
                    DefaultMaxResultItems = options.MaxResultItems
                };

                var configureCatalog = McpEndpoint.ConfigureCatalog;
                if (configureCatalog != null) configureCatalog(catalogOptions);

                var catalog = ToolCatalog.BuildFromAssemblies(catalogOptions, ScanTargets().ToArray());

                var dispatcher = new ToolDispatcher(catalog, new ToolDispatcherOptions
                {
                    AllowMutatingTools = options.AllowMutating,
                    IncludeExceptionDetail = options.IncludeExceptionDetail,
                    Audit = McpEndpoint.Audit,
                    InvocationScope = callContext =>
                        PrincipalScope.TryCreate(callContext, McpEndpoint.PrincipalMapper)
                });

                var processor = new McpRequestProcessor(
                    dispatcher, options, new JavaScriptSerializerJsonParser(), McpEndpoint.Authorizer);

                McpEndpoint.Report("Endpoint enabled at " + options.NormalizedBasePath +
                                 " with " + catalog.Tools.Count + " tool(s).");

                // Surfaced at startup because the worst of these — a tool name over the model's
                // limit — otherwise fails at invocation, long after the target was created cleanly.
                foreach (var issue in processor.GatewayIssues)
                    McpEndpoint.Report("AgentCore compatibility: " + issue);

                return new McpRuntime(options, processor, new string[0]);
            }
            catch (ToolRegistrationException ex)
            {
                foreach (var error in ex.Errors) McpEndpoint.Report("Registration error: " + error);
                return new McpRuntime(options, null, ex.Errors);
            }
            catch (Exception ex)
            {
                var message = ex.GetType().Name + ": " + ex.Message;
                McpEndpoint.Report("Failed to build the tool catalog. " + message);
                return new McpRuntime(options, null, new[] { message });
            }
        }

        private static McpEndpointOptions ReadOptions()
        {
            var options = McpEndpointOptionsReader.FromAppSettings();

            var configure = McpEndpoint.ConfigureEndpoint;
            if (configure != null) configure(options);

            return options;
        }

        private static IEnumerable<Assembly> ScanTargets()
        {
            var supplied = McpEndpoint.RegistryAssemblies;
            if (supplied != null) return supplied().Where(a => a != null);

            // The canonical ASP.NET list: bin plus App_Code. Framework assemblies are skipped
            // because scanning them is pure cost — no application registry lives there.
            return BuildManager.GetReferencedAssemblies()
                .Cast<Assembly>()
                .Where(a => a != null && !IsFrameworkAssembly(a));
        }

        private static bool IsFrameworkAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? string.Empty;

            return name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("WebGrease", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Antlr3", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reads endpoint options from <c>&lt;appSettings&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Lives in this assembly rather than the core because <c>ConfigurationManager</c> is a
    /// .NET Framework concern; the core stays free of it so it can host anywhere. Absent or
    /// unparseable values fall back to the safe default rather than throwing at startup.
    /// </remarks>
    internal static class McpEndpointOptionsReader
    {
        public const string Prefix = "mcp:";

        public static McpEndpointOptions FromAppSettings(Func<string, string> read = null)
        {
            read = read ?? (key => ConfigurationManager.AppSettings[key]);

            var options = new McpEndpointOptions
            {
                Enabled = Bool(read, "enabled", false),
                BasePath = Text(read, "basePath", "/_mcp"),
                NamePrefix = Text(read, "namePrefix", null),
                AllowMutating = Bool(read, "allowMutating", false),
                SharedSecret = Text(read, "sharedSecret", null),
                AllowInsecureTransport = Bool(read, "allowInsecureTransport", false),
                IncludeExceptionDetail = Bool(read, "includeExceptionDetail", false),
                ServerUrl = Text(read, "serverUrl", null),
                Title = Text(read, "title", null),
                DocumentVersion = Text(read, "documentVersion", "1.0.0"),
                AgentCoreTargetName = Text(read, "agentCoreTargetName", null)
            };

            var maxItems = Text(read, "maxResultItems", null);
            if (!string.IsNullOrWhiteSpace(maxItems))
            {
                int parsed;
                options.MaxResultItems =
                    int.TryParse(maxItems, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0
                        ? parsed
                        : (int?)null;
            }

            var allowedAddresses = Text(read, "allowedIpAddresses", null);
            if (!string.IsNullOrWhiteSpace(allowedAddresses))
            {
                foreach (var address in allowedAddresses.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0))
                    options.AllowedIpAddresses.Add(address);
            }

            return options;
        }

        private static string Text(Func<string, string> read, string key, string fallback)
        {
            var value = read(Prefix + key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static bool Bool(Func<string, string> read, string key, bool fallback)
        {
            var value = read(Prefix + key);
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            bool parsed;
            return bool.TryParse(value.Trim(), out parsed) ? parsed : fallback;
        }
    }

    /// <summary>
    /// Parses request bodies with the serializer already present in <c>System.Web.Extensions</c>.
    /// </summary>
    /// <remarks>
    /// Used for reading only. Its output shape — <c>Dictionary&lt;string, object&gt;</c> for objects
    /// and <c>object[]</c> for arrays — is exactly what the argument binder expects. Writing goes
    /// through <see cref="Json"/> instead, because this serializer renders dates as
    /// <c>\/Date(...)\/</c>.
    /// </remarks>
    internal sealed class JavaScriptSerializerJsonParser : IJsonObjectParser
    {
        public IDictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // MaxJsonLength counts characters and MaxRequestBytes counts bytes, but the module has
            // already rejected bodies larger than MaxRequestBytes and UTF-8 uses at least one byte per
            // character, so this is a safe upper bound rather than a coincidence.
            var serializer = new JavaScriptSerializer
            {
                MaxJsonLength = McpEndpointModule.MaxRequestBytes,
                RecursionLimit = 64
            };

            var parsed = serializer.DeserializeObject(json);

            var map = parsed as IDictionary<string, object>;
            if (map == null)
                throw new FormatException("The request body must be a JSON object.");

            return new Dictionary<string, object>(map, StringComparer.OrdinalIgnoreCase);
        }
    }
}
