// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;

namespace McpToolAdapter.Hosting
{
    /// <summary>Outcome of an authorization decision.</summary>
    public sealed class McpAuthorizationResult
    {
        private static readonly IReadOnlyDictionary<string, string> NoClaims =
            new Dictionary<string, string>(0);

        private McpAuthorizationResult(
            bool allowed, string caller, int statusCode, string code, string message,
            IReadOnlyDictionary<string, string> claims = null)
        {
            IsAllowed = allowed;
            Caller = caller;
            StatusCode = statusCode;
            ErrorCode = code;
            Message = message;
            Claims = claims ?? NoClaims;
        }

        public bool IsAllowed { get; }

        /// <summary>Caller identity to record in the audit trail. Established here, never taken from the request body.</summary>
        public string Caller { get; }

        public int StatusCode { get; }
        public string ErrorCode { get; }
        public string Message { get; }

        /// <summary>
        /// Claims established by the authorizer, empty when it carries no identity.
        /// </summary>
        /// <remarks>
        /// This is how an end user's identity reaches the invocation. A host maps these onto whatever
        /// principal its framework expects — on <c>System.Web</c>, onto
        /// <c>HttpContext.Current.User</c>, which is what legacy authorization checks read.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Claims { get; }

        public static McpAuthorizationResult Allow(
            string caller, IReadOnlyDictionary<string, string> claims = null)
        {
            return new McpAuthorizationResult(true, caller, 200, null, null, claims);
        }

        public static McpAuthorizationResult Deny(int statusCode, string code, string message)
        {
            return new McpAuthorizationResult(false, null, statusCode, code, message);
        }
    }

    /// <summary>Decides whether a request may invoke tools.</summary>
    public interface IMcpAuthorizer
    {
        McpAuthorizationResult Authorize(McpRequest request, McpEndpointOptions options);

        /// <summary>
        /// Reasons this authorizer cannot safely authorize anything, or empty when it is ready.
        /// </summary>
        /// <remarks>
        /// Each authorizer declares its own configuration requirements, because only it knows them. A
        /// shared secret is mandatory for <see cref="SharedSecretAuthorizer"/> and meaningless to a
        /// bearer-token authorizer, so the endpoint cannot sensibly decide this centrally: doing so
        /// forced applications using OAuth to configure a shared secret they never use.
        /// <para>A non-empty result makes the endpoint refuse every request. Reporting rather than
        /// throwing is deliberate, so a misconfigured MCP endpoint never stops an application serving
        /// its own pages.</para>
        /// </remarks>
        IReadOnlyList<string> ConfigurationProblems { get; }
    }

    /// <summary>
    /// Shared-secret authorization over HTTPS, with an optional caller address allowlist.
    /// </summary>
    /// <remarks>
    /// <para>This is a deliberate floor, not a finished authorization model. It establishes that the
    /// caller is the gateway; it says nothing about which end user the call is on behalf of. That
    /// second question — service identity versus impersonated end user — is the decision this
    /// endpoint's real security posture rests on, and it belongs to whoever deploys it.</para>
    /// <para>Replace this by assigning a different <see cref="IMcpAuthorizer"/> — for OAuth bearer
    /// validation, mutual TLS client-certificate checks, or an internal SSO token — without
    /// touching anything else.</para>
    /// </remarks>
    public sealed class SharedSecretAuthorizer : IMcpAuthorizer
    {
        private readonly IReadOnlyList<string> _problems;

        public SharedSecretAuthorizer(McpEndpointOptions options = null)
        {
            _problems = Check(options);
        }

        /// <inheritdoc />
        public IReadOnlyList<string> ConfigurationProblems
        {
            get { return _problems; }
        }

        /// <summary>
        /// The shared secret requirement, which lives here rather than on the options because it is a
        /// requirement of this authorizer alone.
        /// </summary>
        private static IReadOnlyList<string> Check(McpEndpointOptions options)
        {
            var problems = new List<string>();
            if (options == null || !options.Enabled) return problems;

            if (string.IsNullOrWhiteSpace(options.SharedSecret))
            {
                problems.Add("A shared secret is required when the endpoint is enabled with shared-secret " +
                             "authorization; without one the endpoint is unauthenticated remote invocation " +
                             "of business logic.");
            }
            else if (options.SharedSecret.Length < 32)
            {
                problems.Add("The shared secret must be at least 32 characters.");
            }

            return problems;
        }

        public McpAuthorizationResult Authorize(McpRequest request, McpEndpointOptions options)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (!request.IsSecureConnection && !options.AllowInsecureTransport)
            {
                return McpAuthorizationResult.Deny(403, "insecure_transport",
                    "HTTPS is required. The shared secret would otherwise be sent in clear text.");
            }

            if (options.AllowedIpAddresses.Count > 0)
            {
                var remote = request.RemoteIpAddress ?? string.Empty;
                var permitted = options.AllowedIpAddresses.Any(
                    allowed => string.Equals(allowed, remote, StringComparison.OrdinalIgnoreCase));

                if (!permitted)
                {
                    return McpAuthorizationResult.Deny(403, "address_not_allowed",
                        "The caller address is not permitted.");
                }
            }

            var presented = request.Header(McpEndpointOptions.ApiKeyHeader);
            if (string.IsNullOrEmpty(presented))
            {
                return McpAuthorizationResult.Deny(401, "missing_credentials",
                    "The " + McpEndpointOptions.ApiKeyHeader + " header is required.");
            }

            if (!FixedTimeEquals(presented, options.SharedSecret))
            {
                return McpAuthorizationResult.Deny(401, "invalid_credentials",
                    "The supplied key is not valid.");
            }

            return McpAuthorizationResult.Allow("shared-secret");
        }

        /// <summary>
        /// Compares without an early return on the first differing character, so response timing
        /// does not reveal how much of a guessed secret was correct.
        /// </summary>
        internal static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;

            // Length is not secret, but comparing over a fixed span keeps the loop's cost
            // independent of where the first difference falls.
            var length = Math.Max(left.Length, right.Length);
            var difference = left.Length ^ right.Length;

            for (var i = 0; i < length; i++)
            {
                var l = i < left.Length ? left[i] : '\0';
                var r = i < right.Length ? right[i] : '\0';
                difference |= l ^ r;
            }

            return difference == 0;
        }
    }
}
