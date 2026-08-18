// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Web;
using McpToolAdapter.Dispatch;

namespace McpToolAdapter.Web
{
    /// <summary>
    /// Establishes <c>HttpContext.Current.User</c> and <c>Thread.CurrentPrincipal</c> for the duration
    /// of a tool call, then restores what was there before.
    /// </summary>
    /// <remarks>
    /// <para>This is what makes an application's own authentication keep working. Legacy business logic
    /// asks <c>HttpContext.Current.User.Identity.Name</c> and <c>User.IsInRole("Approver")</c>; those
    /// calls are the application's authorization model, and they return nothing useful when a tool is
    /// invoked by a gateway rather than a browser. Given verified claims from an authorizer such as
    /// <c>McpToolAdapter.Jwt.JwtBearerAuthorizer</c>, this puts a real principal back in place so
    /// those checks behave as they always have.</para>
    /// <para>Restoration in <see cref="Dispose"/> is not optional: an <c>HttpContext</c> is reused
    /// across the rest of the request, and leaving a caller's identity attached to it would be an
    /// identity-leak bug.</para>
    /// <para>What this cannot do is fake a <c>Session</c>. Code that reads
    /// <c>Session["CurrentUser"]</c> is bound to a browser session that an agent does not have, and
    /// there is no honest way to synthesise one. Such code has to be changed to take its inputs as
    /// parameters.</para>
    /// </remarks>
    internal sealed class PrincipalScope : IDisposable
    {
        private readonly HttpContext _context;
        private readonly IPrincipal _previousContextUser;
        private readonly IPrincipal _previousThreadPrincipal;
        private bool _disposed;

        private PrincipalScope(HttpContext context, IPrincipal principal)
        {
            _context = context;
            _previousThreadPrincipal = Thread.CurrentPrincipal;
            _previousContextUser = context?.User;

            Thread.CurrentPrincipal = principal;
            if (context != null) context.User = principal;
        }

        /// <summary>
        /// Returns a scope for the call, or null when there is no identity to establish — in which case
        /// the tool runs as whatever the application already had, exactly as before.
        /// </summary>
        public static IDisposable TryCreate(ToolCallContext callContext, Func<ToolCallContext, IPrincipal> mapper)
        {
            if (mapper == null) return null;

            var principal = mapper(callContext);
            if (principal == null) return null;

            return new PrincipalScope(HttpContext.Current, principal);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Thread.CurrentPrincipal = _previousThreadPrincipal;
            if (_context != null) _context.User = _previousContextUser;
        }
    }

    /// <summary>
    /// Builds a principal from verified token claims.
    /// </summary>
    /// <remarks>
    /// The default maps the identity claim to a name and reads roles from the token. That is rarely
    /// the whole answer: most legacy applications hold their own role tables, and the authoritative
    /// answer to "what may this user do" lives there rather than in the token. Supply your own mapper
    /// via <see cref="McpEndpoint.PrincipalMapper"/> to look roles up where they actually live.
    /// </remarks>
    public static class ClaimsPrincipalMapper
    {
        /// <summary>Claims searched for role membership, in order.</summary>
        public static readonly string[] RoleClaims = { "roles", "role", "groups", "cognito:groups" };

        /// <summary>
        /// Maps the caller and any role claims onto a <see cref="GenericPrincipal"/>. Returns null when
        /// the call carries no identity, so a service-account call does not silently acquire one.
        /// </summary>
        public static IPrincipal FromClaims(ToolCallContext callContext)
        {
            if (callContext == null || string.IsNullOrWhiteSpace(callContext.Caller)) return null;
            if (callContext.Claims == null || callContext.Claims.Count == 0) return null;

            var roles = RoleClaims
                .Where(claim => callContext.Claims.ContainsKey(claim))
                .SelectMany(claim => (callContext.Claims[claim] ?? string.Empty)
                    .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new GenericPrincipal(new GenericIdentity(callContext.Caller, "Bearer"), roles);
        }
    }
}
