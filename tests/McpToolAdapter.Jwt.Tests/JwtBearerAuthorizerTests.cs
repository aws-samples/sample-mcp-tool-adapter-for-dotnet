// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using McpToolAdapter.Hosting;
using McpToolAdapter.Jwt;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace McpToolAdapter.Jwt.Tests
{
    /// <summary>
    /// Exercises validation with real RSA keys and genuinely signed tokens rather than mocks, so the
    /// signature, audience, issuer and lifetime checks are actually proven rather than assumed.
    /// </summary>
    public class JwtBearerAuthorizerTests : IDisposable
    {
        private const string Issuer = "https://idp.example.com";
        private const string Audience = "orderapp";
        private const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";

        private readonly RSA _signingKey = RSA.Create(2048);
        private readonly RSA _attackerKey = RSA.Create(2048);

        public void Dispose()
        {
            _signingKey.Dispose();
            _attackerKey.Dispose();
        }

        private static JwtBearerOptions Options(Action<JwtBearerOptions> customize = null)
        {
            var options = new JwtBearerOptions { DiscoveryUrl = DiscoveryUrl, Audience = Audience };
            customize?.Invoke(options);
            return options;
        }

        private JwtBearerAuthorizer Authorizer(JwtBearerOptions options = null)
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(new RsaSecurityKey(_signingKey) { KeyId = "test-key" });

            return new JwtBearerAuthorizer(options ?? Options(), () => metadata);
        }

        private string Token(
            RSA key = null,
            string audience = Audience,
            string issuer = Issuer,
            DateTime? expires = null,
            DateTime? notBefore = null,
            IEnumerable<System.Security.Claims.Claim> claims = null)
        {
            var credentials = new SigningCredentials(
                new RsaSecurityKey(key ?? _signingKey) { KeyId = "test-key" },
                SecurityAlgorithms.RsaSha256);

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims ?? new[] { new System.Security.Claims.Claim("sub", "alice@example.com") },
                notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-1),
                expires: expires ?? DateTime.UtcNow.AddMinutes(10),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static McpRequest Request(string authorization, bool secure = true)
        {
            var headers = new Dictionary<string, string>();
            if (authorization != null) headers["Authorization"] = authorization;
            return new McpRequest("POST", "/_mcp/tools/x", headers, "{}", "10.0.0.1", secure);
        }

        [Fact]
        public void AcceptsAValidTokenAndSurfacesTheIdentity()
        {
            var result = Authorizer().Authorize(Request("Bearer " + Token()), new McpEndpointOptions());

            Assert.True(result.IsAllowed);
            Assert.Equal("alice@example.com", result.Caller);
            Assert.Equal("alice@example.com", result.Claims["sub"]);
        }

        [Fact]
        public void RejectsATokenSignedByAKeyItDoesNotTrust()
        {
            // The check that matters most: anyone can mint a token, only the real issuer can sign one.
            var result = Authorizer().Authorize(Request("Bearer " + Token(key: _attackerKey)), new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal(401, result.StatusCode);
            Assert.Contains(result.ErrorCode, new[] { "invalid_signature", "invalid_token" });
        }

        [Fact]
        public void RejectsATokenMintedForADifferentAudience()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(audience: "some-other-service")), new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal("invalid_audience", result.ErrorCode);
        }

        [Fact]
        public void RejectsATokenFromADifferentIssuer()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(issuer: "https://evil.example.com")), new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal(401, result.StatusCode);
        }

        [Fact]
        public void RejectsAnExpiredToken()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(
                    notBefore: DateTime.UtcNow.AddHours(-2),
                    expires: DateTime.UtcNow.AddHours(-1))),
                new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal("token_expired", result.ErrorCode);
        }

        [Fact]
        public void RejectsAMissingOrMalformedAuthorizationHeader()
        {
            var authorizer = Authorizer();
            var options = new McpEndpointOptions();

            Assert.Equal("missing_credentials", authorizer.Authorize(Request(null), options).ErrorCode);
            Assert.Equal("missing_credentials", authorizer.Authorize(Request("Basic abc"), options).ErrorCode);
            Assert.Equal("missing_credentials", authorizer.Authorize(Request("Bearer "), options).ErrorCode);
        }

        [Fact]
        public void RejectsGarbageInPlaceOfAToken()
        {
            var result = Authorizer().Authorize(Request("Bearer not-a-jwt"), new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal(401, result.StatusCode);
        }

        [Fact]
        public void RejectsPlainHttpByDefault()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(), secure: false), new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal("insecure_transport", result.ErrorCode);
        }

        [Fact]
        public void EnforcesRequiredScopesWhenConfigured()
        {
            var authorizer = Authorizer(Options(o => o.RequiredScopes.Add("tools.invoke")));

            var without = authorizer.Authorize(Request("Bearer " + Token()), new McpEndpointOptions());
            Assert.Equal("insufficient_scope", without.ErrorCode);

            var with = authorizer.Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("sub", "alice@example.com"),
                    new System.Security.Claims.Claim("scope", "openid tools.invoke profile")
                })),
                new McpEndpointOptions());

            Assert.True(with.IsAllowed);
        }

        [Fact]
        public void RequiresEveryConfiguredScopeNotJustOne()
        {
            var authorizer = Authorizer(Options(o =>
            {
                o.RequiredScopes.Add("tools.invoke");
                o.RequiredScopes.Add("tools.admin");
            }));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("sub", "alice@example.com"),
                    new System.Security.Claims.Claim("scope", "tools.invoke")
                })),
                new McpEndpointOptions());

            Assert.Equal("insufficient_scope", result.ErrorCode);
            Assert.Contains("tools.admin", result.Message);
        }

        [Fact]
        public void AcceptsProviderQualifiedScopeNames()
        {
            // Cognito emits scopes as resourceServer/scope.
            var authorizer = Authorizer(Options(o => o.RequiredScopes.Add("orderapp/tools.invoke")));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("sub", "m2m"),
                    new System.Security.Claims.Claim("scope", "orderapp/tools.invoke")
                })),
                new McpEndpointOptions());

            Assert.True(result.IsAllowed);
        }

        [Fact]
        public void AcceptsAMachineTokenCarryingAClientIdAndNoAudience()
        {
            // The machine-to-machine shape: no aud, a client_id, and a scope.
            var authorizer = Authorizer(new JwtBearerOptions { DiscoveryUrl = DiscoveryUrl }
                .Tap(o => o.AllowedClientIds.Add("1example23456789")));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(audience: null, claims: new[]
                {
                    new System.Security.Claims.Claim("client_id", "1example23456789"),
                    new System.Security.Claims.Claim("sub", "1example23456789")
                })),
                new McpEndpointOptions());

            Assert.True(result.IsAllowed);
            Assert.Equal("1example23456789", result.Caller);
        }

        [Fact]
        public void RejectsAClientIdThatIsNotAllowed()
        {
            var authorizer = Authorizer(new JwtBearerOptions { DiscoveryUrl = DiscoveryUrl }
                .Tap(o => o.AllowedClientIds.Add("expected-client")));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(audience: null, claims: new[]
                {
                    new System.Security.Claims.Claim("client_id", "some-other-client"),
                    new System.Security.Claims.Claim("sub", "some-other-client")
                })),
                new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal("client_not_allowed", result.ErrorCode);
        }

        [Theory]
        [InlineData("cid")]
        [InlineData("azp")]
        public void AcceptsTheClientIdentifierFromAlternativeClaims(string claimName)
        {
            // Okta uses cid; some providers use azp.
            var authorizer = Authorizer(new JwtBearerOptions { DiscoveryUrl = DiscoveryUrl }
                .Tap(o => o.AllowedClientIds.Add("okta-client")));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(audience: null, claims: new[]
                {
                    new System.Security.Claims.Claim(claimName, "okta-client"),
                    new System.Security.Claims.Claim("sub", "okta-client")
                })),
                new McpEndpointOptions());

            Assert.True(result.IsAllowed);
        }

        [Fact]
        public void RejectsAnIdentityTokenWhereAnAccessTokenIsExpected()
        {
            // An identity token is not an authorization credential.
            var authorizer = Authorizer(Options(o => o.RequiredTokenUse = "access"));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("sub", "alice@example.com"),
                    new System.Security.Claims.Claim("token_use", "id")
                })),
                new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal("wrong_token_use", result.ErrorCode);
        }

        [Fact]
        public void AcceptsTheExpectedTokenUse()
        {
            var authorizer = Authorizer(Options(o => o.RequiredTokenUse = "access"));

            var result = authorizer.Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("sub", "alice@example.com"),
                    new System.Security.Claims.Claim("token_use", "access")
                })),
                new McpEndpointOptions());

            Assert.True(result.IsAllowed);
        }

        [Fact]
        public void IgnoresTokenUseWhenTheProviderDoesNotEmitIt()
        {
            var authorizer = Authorizer(Options(o => o.RequiredTokenUse = "access"));

            Assert.True(authorizer.Authorize(Request("Bearer " + Token()), new McpEndpointOptions()).IsAllowed);
        }

        [Fact]
        public void FallsBackThroughTheConfiguredIdentityClaims()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("email", "bob@example.com")
                })),
                new McpEndpointOptions());

            Assert.True(result.IsAllowed);
            Assert.Equal("bob@example.com", result.Caller);
        }

        [Fact]
        public void RejectsATokenCarryingNoUsableIdentityClaim()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("something_else", "x")
                })),
                new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal("no_identity_claim", result.ErrorCode);
        }

        [Fact]
        public void JoinsRepeatedClaimsRatherThanDroppingThem()
        {
            var result = Authorizer().Authorize(
                Request("Bearer " + Token(claims: new[]
                {
                    new System.Security.Claims.Claim("sub", "alice@example.com"),
                    new System.Security.Claims.Claim("roles", "reader"),
                    new System.Security.Claims.Claim("roles", "approver")
                })),
                new McpEndpointOptions());

            Assert.Contains("reader", result.Claims["roles"]);
            Assert.Contains("approver", result.Claims["roles"]);
        }

        [Fact]
        public void RefusesEveryRequestWhenNeitherAudienceNorClientIdIsConfigured()
        {
            // Without one of the two, any token the provider ever issued would be accepted.
            var noAudience = new JwtBearerAuthorizer(
                new JwtBearerOptions { DiscoveryUrl = DiscoveryUrl });

            Assert.NotEmpty(noAudience.ConfigurationProblems);

            var result = noAudience.Authorize(Request("Bearer " + Token()), new McpEndpointOptions());
            Assert.False(result.IsAllowed);
            Assert.Equal(503, result.StatusCode);
        }

        [Fact]
        public void ReportsAnUnusableDiscoveryUrlAsMisconfiguration()
        {
            var problems = new JwtBearerOptions
            {
                DiscoveryUrl = "https://idp.example.com/openid-config",
                Audience = Audience
            }.Validate();

            Assert.Contains(problems, p => p.Contains("openid-configuration"));
        }

        [Fact]
        public void ReturnsServiceUnavailableWhenSigningKeysCannotBeRetrieved()
        {
            // An availability problem must not read as an authorization failure: the gateway should
            // retry, not conclude the caller is unauthorized.
            var authorizer = new JwtBearerAuthorizer(
                Options(), () => throw new InvalidOperationException("network down"));

            var result = authorizer.Authorize(Request("Bearer " + Token()), new McpEndpointOptions());

            Assert.False(result.IsAllowed);
            Assert.Equal(503, result.StatusCode);
            Assert.Equal("metadata_unavailable", result.ErrorCode);
        }
    }

    internal static class OptionsExtensions
    {
        /// <summary>Configures an options instance inline, since collection properties are read-only.</summary>
        public static JwtBearerOptions Tap(this JwtBearerOptions options, Action<JwtBearerOptions> configure)
        {
            configure(options);
            return options;
        }
    }
}
