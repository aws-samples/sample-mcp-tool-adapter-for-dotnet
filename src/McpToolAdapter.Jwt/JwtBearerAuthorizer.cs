// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpToolAdapter.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace McpToolAdapter.Jwt
{
    public sealed class JwtBearerOptions
    {
        /// <summary>
        /// OIDC discovery document URL, matching <c>^.+/\.well-known/openid-configuration$</c>. Signing
        /// keys are fetched from it and refreshed automatically.
        /// </summary>
        public string DiscoveryUrl { get; set; }

        /// <summary>
        /// Audience this application is registered as, validated against the <c>aud</c> claim.
        /// </summary>
        /// <remarks>
        /// Either this or <see cref="AllowedClientIds"/> must be set. One of the two is mandatory
        /// because without either, any token the provider ever issued — for any service — would be
        /// accepted here.
        /// </remarks>
        public string Audience { get; set; }

        /// <summary>
        /// Client identifiers permitted to call this application, validated against <c>client_id</c>
        /// and then <c>cid</c>.
        /// </summary>
        /// <remarks>
        /// The alternative to <see cref="Audience"/>, because providers disagree about where the
        /// calling identity lives. AgentCore Gateway's own inbound authorizer draws the same
        /// distinction — <c>allowedAudience</c> versus <c>allowedClients</c> — and its documentation
        /// notes that Okta places the client identity in <c>cid</c> rather than <c>client_id</c>, so
        /// both are checked. Machine-to-machine access tokens in particular commonly carry a client
        /// identifier and no audience.
        /// </remarks>
        public IList<string> AllowedClientIds { get; } = new List<string>();

        /// <summary>Expected issuer. Defaults to the issuer advertised by the discovery document.</summary>
        public string Issuer { get; set; }

        /// <summary>Permitted clock skew. Kept small; the default of five minutes is generous.</summary>
        public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Scopes that must all be present. Checked against <c>scope</c> (space-delimited) and
        /// <c>scp</c>.
        /// </summary>
        /// <remarks>
        /// For machine-to-machine callers the scope is usually the only authorization signal in the
        /// token, so setting at least one is strongly advised. Provider-qualified names such as
        /// <c>orderapp/tools.invoke</c> work as-is.
        /// </remarks>
        public IList<string> RequiredScopes { get; } = new List<string>();

        /// <summary>
        /// Required value of the <c>token_use</c> claim, when the provider emits one — Amazon Cognito
        /// uses <c>access</c> for access tokens and <c>id</c> for identity tokens.
        /// </summary>
        /// <remarks>
        /// Set this to <c>access</c> when the caller should be presenting an access token. Identity
        /// tokens are not authorization credentials, and accepting one where an access token is
        /// expected is a real, and easily missed, privilege problem. Left unset, the claim is ignored,
        /// since many providers do not emit it.
        /// </remarks>
        public string RequiredTokenUse { get; set; }

        /// <summary>
        /// Claim carrying the identity to record and map onto a principal. Tried in order; first match
        /// wins. Defaults to `sub`, then common alternatives.
        /// </summary>
        public IList<string> IdentityClaims { get; } =
            new List<string> { "sub", "preferred_username", "email", "upn" };

        /// <summary>How long to wait for the discovery document. Blocking, so keep it short.</summary>
        public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();

            if (string.IsNullOrWhiteSpace(DiscoveryUrl))
                problems.Add("A discovery URL is required.");
            else if (!DiscoveryUrl.EndsWith("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
                problems.Add("The discovery URL must end with '/.well-known/openid-configuration'.");

            if (string.IsNullOrWhiteSpace(Audience) && AllowedClientIds.Count == 0)
            {
                problems.Add("Set an audience or at least one allowed client id. Without either, any " +
                             "token the provider issued for any service would be accepted here.");
            }

            return problems;
        }
    }

    /// <summary>
    /// Authorizes a request by validating an OAuth bearer token, and surfaces its claims so the call
    /// runs as the end user rather than as a service account.
    /// </summary>
    /// <remarks>
    /// <para>This is the receiving end of AgentCore's on-behalf-of token exchange. Configure the
    /// gateway target with <c>credentialProviderType: OAUTH</c> and
    /// <c>grantType: TOKEN_EXCHANGE</c>, and the gateway presents a token scoped to this
    /// application's audience with the original user's <c>sub</c> preserved. Pair it with an
    /// invocation scope in the host to turn those claims into a principal the legacy code reads.</para>
    /// <para>Validation is delegated to Microsoft's token handler: signature, issuer, audience and
    /// lifetime are all enforced, and signing-key rotation is handled by
    /// <see cref="ConfigurationManager{T}"/>. Nothing here is hand-rolled, because this is precisely
    /// the code that is dangerous to get subtly wrong.</para>
    /// </remarks>
    public sealed class JwtBearerAuthorizer : IMcpAuthorizer
    {
        private const string BearerPrefix = "Bearer ";

        private readonly JwtBearerOptions _options;
        private readonly IReadOnlyList<string> _configurationProblems;
        // MapInboundClaims defaults to true, which rewrites standard JWT claim names into long
        // Microsoft URIs — 'sub' becomes '…/identity/claims/nameidentifier'. Everything here works in
        // terms of the names actually present in the token, so the mapping is turned off.
        private readonly JwtSecurityTokenHandler _handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };
        private readonly Func<OpenIdConnectConfiguration> _metadata;

        public JwtBearerAuthorizer(JwtBearerOptions options)
            : this(options, null)
        {
        }

        /// <summary>Test seam: supply the discovery document directly instead of fetching it.</summary>
        internal JwtBearerAuthorizer(JwtBearerOptions options, Func<OpenIdConnectConfiguration> metadata)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _configurationProblems = options.Validate();

            if (metadata != null)
            {
                _metadata = metadata;
                return;
            }

            if (_configurationProblems.Count > 0)
            {
                _metadata = () => throw new InvalidOperationException("Misconfigured.");
                return;
            }

            var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                options.DiscoveryUrl, new OpenIdConnectConfigurationRetriever());

            _metadata = () => FetchWithoutDeadlocking(manager, options.MetadataTimeout);
        }

        /// <summary>Problems that make this authorizer refuse every request. Empty when usable.</summary>
        public IReadOnlyList<string> ConfigurationProblems
        {
            get { return _configurationProblems; }
        }

        public McpAuthorizationResult Authorize(McpRequest request, McpEndpointOptions endpointOptions)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (_configurationProblems.Count > 0)
            {
                return McpAuthorizationResult.Deny(503, "misconfigured",
                    "Bearer token validation is not correctly configured, so requests are refused.");
            }

            if (endpointOptions != null &&
                !request.IsSecureConnection &&
                !endpointOptions.AllowInsecureTransport)
            {
                return McpAuthorizationResult.Deny(403, "insecure_transport",
                    "HTTPS is required. A bearer token must not be sent in clear text.");
            }

            var header = request.Header("Authorization");
            if (string.IsNullOrWhiteSpace(header) ||
                !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return McpAuthorizationResult.Deny(401, "missing_credentials",
                    "An Authorization header carrying a Bearer token is required.");
            }

            var token = header.Substring(BearerPrefix.Length).Trim();
            if (token.Length == 0)
            {
                return McpAuthorizationResult.Deny(401, "missing_credentials", "The Bearer token is empty.");
            }

            OpenIdConnectConfiguration metadata;
            try
            {
                metadata = _metadata();
            }
            catch (Exception ex)
            {
                // Availability failure, not an authorization failure: 503 tells the gateway to retry.
                return McpAuthorizationResult.Deny(503, "metadata_unavailable",
                    "Could not retrieve the token signing keys: " + ex.GetType().Name + ".");
            }

            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = metadata.SigningKeys,
                ValidateIssuer = true,
                ValidIssuer = string.IsNullOrWhiteSpace(_options.Issuer) ? metadata.Issuer : _options.Issuer,
                // Only when an audience is configured. With client-id validation instead, there may be
                // no `aud` claim at all, and demanding one would reject every valid token.
                ValidateAudience = !string.IsNullOrWhiteSpace(_options.Audience),
                ValidAudience = _options.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = _options.ClockSkew
            };

            System.Security.Claims.ClaimsPrincipal principal;
            try
            {
                SecurityToken validated;
                principal = _handler.ValidateToken(token, parameters, out validated);
            }
            catch (SecurityTokenExpiredException)
            {
                return McpAuthorizationResult.Deny(401, "token_expired", "The token has expired.");
            }
            catch (SecurityTokenInvalidAudienceException)
            {
                return McpAuthorizationResult.Deny(401, "invalid_audience",
                    "The token was not issued for this application.");
            }
            catch (SecurityTokenInvalidSignatureException)
            {
                return McpAuthorizationResult.Deny(401, "invalid_signature", "The token signature is not valid.");
            }
            catch (SecurityTokenException ex)
            {
                return McpAuthorizationResult.Deny(401, "invalid_token", "The token is not valid: " + ex.GetType().Name + ".");
            }
            catch (Exception ex)
            {
                // Not every rejection arrives as a SecurityTokenException — a malformed token throws
                // SecurityTokenMalformedException, which does not derive from it. Anything unexpected
                // while validating a credential must fail closed rather than propagate.
                return McpAuthorizationResult.Deny(401, "invalid_token",
                    "The token could not be validated: " + ex.GetType().Name + ".");
            }

            var claims = Flatten(principal);

            if (!string.IsNullOrWhiteSpace(_options.RequiredTokenUse))
            {
                string tokenUse;
                if (claims.TryGetValue("token_use", out tokenUse) &&
                    !string.Equals(tokenUse, _options.RequiredTokenUse, StringComparison.Ordinal))
                {
                    return McpAuthorizationResult.Deny(401, "wrong_token_use",
                        "This endpoint expects a '" + _options.RequiredTokenUse + "' token but received '" +
                        tokenUse + "'.");
                }
            }

            if (_options.AllowedClientIds.Count > 0 && !HasAllowedClient(claims, _options.AllowedClientIds))
            {
                return McpAuthorizationResult.Deny(403, "client_not_allowed",
                    "The token's client identifier is not permitted to call this application.");
            }

            var missingScopes = _options.RequiredScopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope) && !HasScope(claims, scope))
                .ToList();
            if (missingScopes.Count > 0)
            {
                return McpAuthorizationResult.Deny(403, "insufficient_scope",
                    "The token is missing required scope(s): " + string.Join(", ", missingScopes.ToArray()) + ".");
            }

            var identity = _options.IdentityClaims
                .Select(name => claims.TryGetValue(name, out var value) ? value : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(identity))
            {
                return McpAuthorizationResult.Deny(401, "no_identity_claim",
                    "The token carries none of the configured identity claims: " +
                    string.Join(", ", _options.IdentityClaims.ToArray()) + ".");
            }

            return McpAuthorizationResult.Allow(identity, claims);
        }

        private static Dictionary<string, string> Flatten(System.Security.Claims.ClaimsPrincipal principal)
        {
            var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var claim in principal.Claims)
            {
                // Repeated claims (roles, scopes) are joined rather than dropped.
                claims[claim.Type] = claims.TryGetValue(claim.Type, out var existing)
                    ? existing + " " + claim.Value
                    : claim.Value;
            }

            return claims;
        }

        /// <summary>
        /// Checks <c>client_id</c> then <c>cid</c>, because providers place the calling client in
        /// different claims.
        /// </summary>
        private static bool HasAllowedClient(IReadOnlyDictionary<string, string> claims, IList<string> allowed)
        {
            foreach (var name in new[] { "client_id", "cid", "azp" })
            {
                string value;
                if (!claims.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value)) continue;
                if (allowed.Any(a => string.Equals(a, value, StringComparison.Ordinal))) return true;
            }

            return false;
        }

        private static bool HasScope(IReadOnlyDictionary<string, string> claims, string required)
        {
            foreach (var name in new[] { "scope", "scp" })
            {
                if (!claims.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) continue;

                if (value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                         .Any(scope => string.Equals(scope, required, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Blocks on the async retrieval from a synchronous pipeline.
        /// </summary>
        /// <remarks>
        /// <c>Task.Run</c> is not decoration: awaiting directly and blocking would capture ASP.NET's
        /// synchronization context and can deadlock the request. Running on a pool thread escapes it.
        /// The result is cached by <see cref="ConfigurationManager{T}"/>, so this cost is paid on
        /// refresh rather than per call.
        /// </remarks>
        private static OpenIdConnectConfiguration FetchWithoutDeadlocking(
            ConfigurationManager<OpenIdConnectConfiguration> manager, TimeSpan timeout)
        {
            using (var cancellation = new CancellationTokenSource(timeout))
            {
                return Task.Run(() => manager.GetConfigurationAsync(cancellation.Token), cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
    }
}
