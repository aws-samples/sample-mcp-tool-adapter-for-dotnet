// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using McpToolAdapter.Invocation;
using McpToolAdapter.Shaping;

namespace McpToolAdapter.Dispatch
{
    /// <summary>Who is calling, for audit and authorization decisions made by the host.</summary>
    public sealed class ToolCallContext
    {
        private static readonly IReadOnlyDictionary<string, string> NoClaims =
            new Dictionary<string, string>(0);

        public ToolCallContext(
            string caller = null,
            string correlationId = null,
            IReadOnlyDictionary<string, string> claims = null)
        {
            Caller = caller;
            CorrelationId = correlationId;
            Claims = claims ?? NoClaims;
        }

        /// <summary>Caller identity as established by the host, never by the caller itself.</summary>
        public string Caller { get; }

        public string CorrelationId { get; }

        /// <summary>
        /// Verified claims about the end user this call is on behalf of, empty for a service-account
        /// call. Read by <see cref="ToolDispatcherOptions.InvocationScope"/> to establish a principal.
        /// </summary>
        public IReadOnlyDictionary<string, string> Claims { get; }

        public static ToolCallContext Anonymous
        {
            get { return new ToolCallContext(); }
        }
    }

    /// <summary>One line of the audit trail. Emitted for every call, successful or not.</summary>
    public sealed class ToolAuditEntry
    {
        internal ToolAuditEntry(
            string toolName, bool isMutating, string caller, string correlationId,
            IEnumerable<string> argumentNames, bool succeeded, string errorCode, long durationMilliseconds)
        {
            ToolName = toolName;
            IsMutating = isMutating;
            Caller = caller;
            CorrelationId = correlationId;
            ArgumentNames = (argumentNames ?? Enumerable.Empty<string>()).ToList();
            Succeeded = succeeded;
            ErrorCode = errorCode;
            DurationMilliseconds = durationMilliseconds;
            TimestampUtc = DateTime.UtcNow;
        }

        public string ToolName { get; }
        public bool IsMutating { get; }
        public string Caller { get; }
        public string CorrelationId { get; }

        /// <summary>
        /// Argument <em>names</em> only. Values are deliberately excluded: they routinely contain
        /// customer data, and an audit log is a poor place to accumulate it.
        /// </summary>
        public IReadOnlyList<string> ArgumentNames { get; }

        public bool Succeeded { get; }
        public string ErrorCode { get; }
        public long DurationMilliseconds { get; }
        public DateTime TimestampUtc { get; }
    }

    public static class ToolErrorCodes
    {
        public const string UnknownTool = "unknown_tool";
        public const string MutationDisabled = "mutation_disabled";
        public const string InvalidArguments = "invalid_arguments";
        public const string InvocationFailed = "invocation_failed";
    }

    /// <summary>Outcome of one tool call, ready for a host to serialize.</summary>
    public sealed class ToolInvocationResult
    {
        private ToolInvocationResult(
            string toolName, bool isSuccess, object payload, string errorCode, string errorMessage,
            bool truncated, int? totalItems, int? returnedItems, long durationMilliseconds)
        {
            ToolName = toolName;
            IsSuccess = isSuccess;
            Payload = payload;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            Truncated = truncated;
            TotalItems = totalItems;
            ReturnedItems = returnedItems;
            DurationMilliseconds = durationMilliseconds;
        }

        public string ToolName { get; }
        public bool IsSuccess { get; }
        public object Payload { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public bool Truncated { get; }
        public int? TotalItems { get; }
        public int? ReturnedItems { get; }
        public long DurationMilliseconds { get; }

        internal static ToolInvocationResult Success(string toolName, ShapedResult shaped, long elapsed)
        {
            return new ToolInvocationResult(toolName, true, shaped.Payload, null, null,
                shaped.Truncated, shaped.TotalItems, shaped.ReturnedItems, elapsed);
        }

        internal static ToolInvocationResult Failure(string toolName, string code, string message, long elapsed)
        {
            return new ToolInvocationResult(toolName, false, null, code, message, false, null, null, elapsed);
        }

        /// <summary>
        /// The wire format. Both hosts serialize this, so the HTTP contract does not drift between
        /// the .NET Framework host and the modern one.
        /// </summary>
        public JsonObject ToEnvelope()
        {
            var envelope = new JsonObject
            {
                ["ok"] = IsSuccess,
                ["tool"] = ToolName
            };

            if (IsSuccess)
            {
                envelope["result"] = Payload;
                if (Truncated)
                {
                    envelope["truncated"] = true;
                    envelope["totalItems"] = TotalItems;
                    envelope["returnedItems"] = ReturnedItems;
                    envelope["truncationNotice"] =
                        "Results were truncated to " + ReturnedItems + " of " + TotalItems +
                        " items. Narrow the query to see the rest.";
                }
            }
            else
            {
                envelope["error"] = new JsonObject
                {
                    ["code"] = ErrorCode,
                    ["message"] = ErrorMessage
                };
            }

            envelope["durationMs"] = DurationMilliseconds;
            return envelope;
        }
    }

    public sealed class ToolDispatcherOptions
    {
        /// <summary>
        /// Whether tools marked <c>Mutating()</c> may run. Defaults to false: a read-only rollout
        /// first is the cheap decision now and an expensive one to retrofit later.
        /// </summary>
        public bool AllowMutatingTools { get; set; }

        /// <summary>
        /// Include exception type and message in error responses. Defaults to false, since legacy
        /// exception text leaks connection strings, SQL and internal paths with some regularity.
        /// Useful in development.
        /// </summary>
        public bool IncludeExceptionDetail { get; set; }

        /// <summary>Receives one entry per call. Wire this to the application's existing logger.</summary>
        public Action<ToolAuditEntry> Audit { get; set; }

        /// <summary>
        /// Wraps the invocation itself, so a host can establish ambient state the legacy code expects
        /// and tear it down afterwards. Returns null to establish nothing.
        /// </summary>
        /// <remarks>
        /// This is the seam that makes an application's own authentication keep working. The
        /// <c>System.Web</c> host uses it to set <c>HttpContext.Current.User</c> and
        /// <c>Thread.CurrentPrincipal</c> from <see cref="ToolCallContext.Claims"/> for the duration
        /// of the call, so existing <c>User.Identity.Name</c> and <c>IsInRole</c> checks inside the
        /// business logic behave as they do for a browser request.
        /// <para>Disposal is guaranteed: the previous state is restored even when the method throws.</para>
        /// </remarks>
        public Func<ToolCallContext, IDisposable> InvocationScope { get; set; }
    }

    /// <summary>
    /// Host-agnostic execution of a tool call: authorize, bind, invoke, shape, audit.
    /// </summary>
    /// <remarks>
    /// This is the seam that lets a <c>System.Web</c> handler and an ASP.NET Core endpoint stay
    /// thin and behave identically. It never throws for an ordinary failure — a bad argument or a
    /// faulting method produces a failed <see cref="ToolInvocationResult"/> — so hosts do not each
    /// invent their own error mapping.
    /// </remarks>
    public sealed class ToolDispatcher
    {
        private readonly ToolCatalog _catalog;
        private readonly ToolDispatcherOptions _options;
        private readonly ArgumentBinder _binder = new ArgumentBinder();

        public ToolDispatcher(ToolCatalog catalog, ToolDispatcherOptions options = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _options = options ?? new ToolDispatcherOptions();
        }

        public ToolCatalog Catalog
        {
            get { return _catalog; }
        }

        public ToolInvocationResult Invoke(
            string toolName,
            IReadOnlyDictionary<string, object> arguments,
            ToolCallContext context = null)
        {
            context = context ?? ToolCallContext.Anonymous;
            var argumentNames = arguments == null ? new List<string>() : arguments.Keys.ToList();
            var stopwatch = Stopwatch.StartNew();

            ToolDescriptor tool;
            if (!_catalog.TryGet(toolName, out tool))
            {
                stopwatch.Stop();
                return Audited(
                    ToolInvocationResult.Failure(toolName, ToolErrorCodes.UnknownTool,
                        "No tool named '" + toolName + "'.", stopwatch.ElapsedMilliseconds),
                    false, context, argumentNames);
            }

            if (tool.IsMutating && !_options.AllowMutatingTools)
            {
                stopwatch.Stop();
                return Audited(
                    ToolInvocationResult.Failure(tool.Name, ToolErrorCodes.MutationDisabled,
                        "Tool '" + tool.Name + "' changes state and mutating tools are disabled here.",
                        stopwatch.ElapsedMilliseconds),
                    tool.IsMutating, context, argumentNames);
            }

            object[] bound;
            try
            {
                bound = _binder.Bind(tool, arguments);
            }
            catch (ArgumentBindingException ex)
            {
                stopwatch.Stop();
                return Audited(
                    ToolInvocationResult.Failure(tool.Name, ToolErrorCodes.InvalidArguments,
                        ex.Message, stopwatch.ElapsedMilliseconds),
                    tool.IsMutating, context, argumentNames);
            }

            try
            {
                object raw;
                // The scope wraps instance creation too: a factory that resolves per-user state needs
                // the principal established before it runs.
                using (var scope = _options.InvocationScope?.Invoke(context))
                {
                    var instance = tool.IsStatic ? null : tool.InstanceFactory();
                    raw = tool.Invoker(instance, bound);
                }

                var shaped = _catalog.ShapingPipeline.Shape(raw, new ShapingContext(tool, tool.MaxResultItems));
                stopwatch.Stop();

                return Audited(
                    ToolInvocationResult.Success(tool.Name, shaped, stopwatch.ElapsedMilliseconds),
                    tool.IsMutating, context, argumentNames);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var message = _options.IncludeExceptionDetail
                    ? ex.GetType().Name + ": " + ex.Message
                    : "The operation failed.";

                return Audited(
                    ToolInvocationResult.Failure(tool.Name, ToolErrorCodes.InvocationFailed,
                        message, stopwatch.ElapsedMilliseconds),
                    tool.IsMutating, context, argumentNames);
            }
        }

        private ToolInvocationResult Audited(
            ToolInvocationResult result, bool isMutating, ToolCallContext context, IReadOnlyList<string> argumentNames)
        {
            var audit = _options.Audit;
            if (audit != null)
            {
                var entry = new ToolAuditEntry(
                    result.ToolName, isMutating, context.Caller, context.CorrelationId,
                    argumentNames, result.IsSuccess, result.ErrorCode, result.DurationMilliseconds);

                // An audit sink must never be able to fail a call it is only observing.
                try
                {
                    audit(entry);
                }
                catch
                {
                    // Intentionally swallowed.
                }
            }

            return result;
        }
    }
}
