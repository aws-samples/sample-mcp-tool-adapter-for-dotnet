// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

// Host for the order portal sample. The adapter integration is the same forty lines as in the
// QuickStart sample — deliberately, because a host adapter is written once per framework and does not
// grow with the application.

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using McpToolAdapter;
using McpToolAdapter.Dispatch;
using McpToolAdapter.Hosting;
using McpToolAdapter.Jwt;

namespace OrderPortal;

public static class Program
{
    // No hardcoded fallback, deliberately.
    //
    // A literal here would be committed to version control, and a well-known "development" secret is
    // precisely the value that ends up quietly accepted somewhere that is not development. When
    // MCP_SHARED_SECRET is absent this generates a fresh one per run and prints it in the banner, so a
    // local run still needs no configuration. When deployed, CDK supplies the variable from Secrets
    // Manager and this is never called.
    private static readonly string SharedSecret =
        Environment.GetEnvironmentVariable("MCP_SHARED_SECRET") is { Length: > 0 } fromEnv
            ? fromEnv
            : GenerateLocalSecret();

    /// <summary>Generates a per-run secret for local use only. Never a fallback for a deployment.</summary>
    private static string GenerateLocalSecret() =>
        // Base64 of 32 random bytes, with the two characters that need escaping in a header or URL
        // swapped out. Comfortably over the 32-character minimum the endpoint enforces.
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', 'x').Replace('/', 'y').TrimEnd('=');

    private static readonly bool AllowMutating =
        string.Equals(Environment.GetEnvironmentVariable("MCP_ALLOW_MUTATING"), "true",
            StringComparison.OrdinalIgnoreCase);

    // Running in Lambda is the reliable signal. This used to test for MCP_SHARED_SECRET, which broke as
    // soon as bearer-token mode arrived: an application authenticating with OAuth has no shared secret,
    // so a deployed process looked local, kept developer-friendly error detail on, and tried to bind a
    // port.
    private static readonly bool IsDeployed =
        Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("MCP_SHARED_SECRET") is { Length: > 0 };

    // Explicit public URL, when the host header cannot be trusted to be it.
    //
    // Behind a private API Gateway the Host header is the VPC endpoint's name, not the API's, so a
    // derived value would put the wrong server URL in the OpenAPI document and the gateway would call
    // the wrong place. When this is set it wins; otherwise the URL is derived per request, which is
    // what makes the same build work locally and behind a Function URL.
    private static readonly string? PublicServerUrl =
        Environment.GetEnvironmentVariable("MCP_SERVER_URL") is { Length: > 0 } url ? url.TrimEnd('/') : null;

    // Verbose request logging. On by default in these samples on purpose: the whole point is to let
    // you see exactly what the gateway sends and what goes back. Set MCP_LOG_REQUESTS=false to quieten
    // it. A real application should leave this off — it writes request bodies to CloudWatch.
    private static readonly bool LogRequests =
        !string.Equals(Environment.GetEnvironmentVariable("MCP_LOG_REQUESTS"), "false",
                       StringComparison.OrdinalIgnoreCase);

    private static readonly string TargetName =
        Environment.GetEnvironmentVariable("MCP_TARGET_NAME") ?? "orderapp";

    // apikey: the gateway proves it is the gateway, and the call carries no user.
    // jwt:    the gateway presents an OAuth bearer token, and the call runs as whoever it names.
    private static readonly bool UseBearerTokens =
        string.Equals(Environment.GetEnvironmentVariable("MCP_AUTH_MODE"), "jwt",
            StringComparison.OrdinalIgnoreCase);

    private static string[] Csv(string name) =>
        (Environment.GetEnvironmentVariable(name) ?? "")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

    // One processor per public base URL.
    //
    // The OpenAPI 'servers' entry must be the URL a gateway will actually call, and a container does
    // not know its own hostname at build or deploy time — a Lambda Function URL is not assigned until
    // the function exists. Deriving it from the request, and caching per host, means the same image
    // works locally and deployed with no configuration.
    private static readonly ConcurrentDictionary<string, McpRequestProcessor> Processors = new();

    private static ToolCatalog _catalog = null!;
    private static ToolDispatcher _dispatcher = null!;

    public static void Main(string[] args)
    {
        var catalog = ToolCatalog.BuildFromAssemblies(
            new ToolCatalogOptions
            {
                // Left empty on purpose: AgentCore namespaces tools by target name already.
                NamePrefix = null,
                DefaultMaxResultItems = 200
            },
            typeof(PortalTools).Assembly);

        var dispatcher = new ToolDispatcher(catalog, new ToolDispatcherOptions
        {
            AllowMutatingTools = AllowMutating,
            IncludeExceptionDetail = !IsDeployed,
            Audit = entry => Console.WriteLine(
                $"  audit: {entry.ToolName} caller={entry.Caller ?? "-"} ok={entry.Succeeded} " +
                $"{entry.DurationMilliseconds}ms args=[{string.Join(",", entry.ArgumentNames)}]"),

            // Establishes the caller for the duration of the invocation, so existing authorization
            // checks in the business logic still decide. The System.Web host does the same thing via
            // PrincipalScope, additionally setting HttpContext.Current.User.
            InvocationScope = ClaimsScope.TryCreate
        });

        _catalog = catalog;
        _dispatcher = dispatcher;

        // `--dump-openapi <path>` writes the document and exits, without binding a port.
        //
        // This is what lets the CDK generate the schema during synth, alongside `dotnet publish`, rather
        // than asking you to start the application and curl it. The document is produced by the same
        // code path that serves /_mcp/openapi.json, so the two cannot disagree.
        if (TryDumpOpenApi(args)) return;

        var builder = WebApplication.CreateBuilder(args);

        // No-op unless the process is running in Lambda.
        //
        // The event source must match the payload format the caller sends, and they differ: a Lambda
        // Function URL and an HTTP API send format 2.0, a REST API sends 1.0. Getting it wrong throws
        // NullReferenceException inside the marshaller and surfaces only as a 502, so it is passed in
        // explicitly by whichever stack deploys this.
        builder.Services.AddAWSLambdaHosting(
            string.Equals(Environment.GetEnvironmentVariable("MCP_LAMBDA_EVENT_SOURCE"), "restapi",
                          StringComparison.OrdinalIgnoreCase)
                ? LambdaEventSource.RestApi
                : LambdaEventSource.HttpApi);
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // The whole integration: translate the framework's request, hand it to the processor, write
        // back what it returns. A null result means the path is not ours.
        app.Map("/_mcp/{**rest}", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            // Behind a Function URL or load balancer the connection to this process is plain HTTP;
            // X-Forwarded-Proto carries what the caller actually used.
            var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                         ?? context.Request.Scheme;
            var baseUrl = $"{scheme}://{context.Request.Host}";

            var request = new McpRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                body,
                context.Connection.RemoteIpAddress?.ToString(),
                isSecureConnection: string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase));

            var started = DateTime.UtcNow;
            if (LogRequests) LogInbound(context, request, body);

            var response = ProcessorFor(PublicServerUrl ?? baseUrl).TryHandle(request);
            if (response is null)
            {
                context.Response.StatusCode = 404;
                if (LogRequests) Console.WriteLine("  <- 404 (path not handled by the adapter)");
                return;
            }

            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            await context.Response.WriteAsync(response.Body ?? string.Empty);

            if (LogRequests)
            {
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                Console.WriteLine($"  <- {response.StatusCode} in {elapsed:F0}ms, " +
                                  $"{(response.Body ?? string.Empty).Length} bytes");
                Console.WriteLine($"     body: {Truncate(response.Body, 900)}");
            }
        });

        if (IsDeployed)
        {
            Console.WriteLine($"Deployed mode: {catalog.Tools.Count} tool(s), " +
                              $"mutating={AllowMutating}, target={TargetName}, " +
                              $"auth={(UseBearerTokens ? "jwt bearer" : "shared secret")}");
            app.Run();
        }
        else
        {
            PrintBanner(catalog, ProcessorFor("http://localhost:5200"));
            app.Run("http://localhost:5200");
        }
    }

    /// <summary>
    /// Handles <c>--dump-openapi &lt;path&gt;</c>, returning true when the process should now exit.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="McpRequestProcessor"/> with a synthetic authorized request rather than
    /// calling the document builder directly, so what lands on disk is byte-for-byte what the endpoint
    /// serves — including the compatibility rules the endpoint applies to itself.
    /// <para>The server URL is a placeholder unless <c>MCP_SERVER_URL</c> is set. A deployed URL is not
    /// known until the API exists, so the CDK overrides <c>servers[0].url</c> with the real one at synth
    /// time; nothing here needs to know it.</para>
    /// </remarks>
    private static bool TryDumpOpenApi(string[] args)
    {
        var index = Array.IndexOf(args, "--dump-openapi");
        if (index < 0) return false;

        if (index + 1 >= args.Length)
            throw new ArgumentException("--dump-openapi requires a file path argument.");

        var destination = args[index + 1];
        var serverUrl = PublicServerUrl ?? "https://server-url-supplied-at-deploy-time.invalid";

        // Forces shared-secret authorization for this one synthetic request. In bearer-token mode the
        // configured authorizer would want a real signed JWT, which a build step has no way to obtain,
        // and the document does not depend on how callers authenticate.
        var response = ProcessorFor(serverUrl, new SharedSecretAuthorizer()).TryHandle(new McpRequest(
            "GET",
            "/_mcp/openapi.json",
            new Dictionary<string, string> { [McpEndpointOptions.ApiKeyHeader] = SharedSecret },
            isSecureConnection: true));

        if (response is null || response.StatusCode != 200)
            throw new InvalidOperationException(
                $"The endpoint refused to produce a document (status {response?.StatusCode.ToString() ?? "none"}): " +
                $"{response?.Body}");

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(destination, response.Body);

        Console.WriteLine($"Wrote {_catalog.Tools.Count} operation(s) to {destination}");
        return true;
    }

    /// <summary>
    /// Writes what arrived, so the values the gateway sends are visible rather than inferred.
    /// </summary>
    /// <remarks>
    /// Headers are logged because the interesting questions are about them: which credential header the
    /// gateway attached, what Host it used, whether X-Forwarded-Proto says https. The credential itself
    /// is redacted to its length — enough to tell "absent" from "present but wrong", without writing a
    /// secret to CloudWatch.
    /// </remarks>
    private static void LogInbound(HttpContext context, McpRequest request, string body)
    {
        Console.WriteLine($"-> {request.Method} {request.AppRelativePath}  from {request.RemoteIpAddress ?? "-"}");

        foreach (var header in context.Request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = IsSensitive(header.Key)
                ? $"<redacted, {header.Value.ToString().Length} chars>"
                : Truncate(header.Value.ToString(), 200);
            Console.WriteLine($"     {header.Key}: {value}");
        }

        if (!string.IsNullOrWhiteSpace(body))
            Console.WriteLine($"     body: {Truncate(body, 900)}");
    }

    private static bool IsSensitive(string headerName) =>
        headerName.Equals(McpEndpointOptions.ApiKeyHeader, StringComparison.OrdinalIgnoreCase) ||
        headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        headerName.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" :
        value.Length <= max ? value : value.Substring(0, max) + $"… (+{value.Length - max} more)";

    private static McpRequestProcessor ProcessorFor(string baseUrl, IMcpAuthorizer? authorizer = null)
    {
        // The authorizer is part of the cache key, so the dump path's shared-secret processor never
        // becomes the one that serves real traffic.
        var key = baseUrl + (authorizer == null ? "" : "|" + authorizer.GetType().Name);

        return Processors.GetOrAdd(key, _ => new McpRequestProcessor(
            _dispatcher,
            new McpEndpointOptions
            {
                Enabled = true,
                SharedSecret = SharedSecret,
                ServerUrl = baseUrl,
                Title = "Order portal operations",
                AgentCoreTargetName = TargetName,
                AllowMutating = AllowMutating,

                // Only relevant for a plain-HTTP request; permitted locally, refused once deployed.
                AllowInsecureTransport = !IsDeployed,
                IncludeExceptionDetail = !IsDeployed
            },
            new SystemTextJsonParser(),
            authorizer ?? BuildAuthorizer()));
    }

    /// <summary>
    /// Shared-secret authorization by default; OAuth bearer validation when MCP_AUTH_MODE=jwt.
    /// </summary>
    /// <remarks>
    /// Bearer mode is what makes a call carry an end user. Configure the AgentCore target with
    /// <c>credentialProviderType: OAUTH</c> and the gateway presents a token here instead of an API
    /// key; with <c>grantType: TOKEN_EXCHANGE</c> that token names the original user.
    /// </remarks>
    private static IMcpAuthorizer? BuildAuthorizer()
    {
        if (!UseBearerTokens) return null; // SharedSecretAuthorizer is the processor's default

        var options = new JwtBearerOptions
        {
            DiscoveryUrl = Environment.GetEnvironmentVariable("MCP_JWT_DISCOVERY_URL"),
            Audience = Environment.GetEnvironmentVariable("MCP_JWT_AUDIENCE"),

            // Cognito access tokens from a client-credentials grant carry no audience, so the client
            // identifier is what identifies the caller. Set whichever your provider emits.
            RequiredTokenUse = Environment.GetEnvironmentVariable("MCP_JWT_TOKEN_USE")
        };

        foreach (var clientId in Csv("MCP_JWT_CLIENT_IDS")) options.AllowedClientIds.Add(clientId);
        foreach (var scope in Csv("MCP_JWT_SCOPES")) options.RequiredScopes.Add(scope);

        var authorizer = new JwtBearerAuthorizer(options);
        foreach (var problem in authorizer.ConfigurationProblems)
            Console.WriteLine($"  JWT configuration problem: {problem}");

        return authorizer;
    }

    private static void PrintBanner(ToolCatalog catalog, McpRequestProcessor processor)
    {
        Console.WriteLine();
        Console.WriteLine($"Order portal — {catalog.Tools.Count} tool(s), " +
                          $"auth={(UseBearerTokens ? "jwt bearer" : "shared secret")}");
        foreach (var tool in catalog.Tools)
            Console.WriteLine($"  {tool.Name}{(tool.IsMutating ? "  [mutating]" : "")}");

        var issues = processor.GatewayIssues;
        if (issues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("AgentCore compatibility:");
            foreach (var issue in issues) Console.WriteLine($"  {issue}");
        }

        Console.WriteLine($@"
Try it — the key is required on every call:

  export KEY={SharedSecret}

  curl -s -H ""X-Mcp-Key: $KEY"" http://localhost:5200/_mcp/health
  curl -s -H ""X-Mcp-Key: $KEY"" http://localhost:5200/_mcp/openapi.json
  curl -s -H ""X-Mcp-Key: $KEY"" http://localhost:5200/_mcp/tools

  curl -s -H ""X-Mcp-Key: $KEY"" -H 'Content-Type: application/json' \
    -d '{{""id"":7}}' http://localhost:5200/_mcp/tools/get_order

  curl -s -H ""X-Mcp-Key: $KEY"" -H 'Content-Type: application/json' \
    -d '{{""query"":{{""Status"":""Shipped"",""Take"":3}}}}' http://localhost:5200/_mcp/tools/search

Things worth trying:
  omit the key                    -> 401
  wrong argument type             -> 400 naming the argument
  cancel_order                    -> 403, mutation disabled (MCP_ALLOW_MUTATING unset)
");
    }
}

/// <summary>
/// Establishes the verified caller as the current principal while a tool runs, then restores.
/// </summary>
/// <remarks>
/// The ASP.NET Core counterpart of the System.Web host's <c>PrincipalScope</c>. Restoring in
/// <see cref="Dispose"/> matters: the thread is pooled, and leaving a caller's identity attached to it
/// would leak that identity into an unrelated request.
/// <para>Returns null when the call carries no claims — an API-key call has no user, and inventing one
/// would be worse than having none.</para>
/// </remarks>
public sealed class ClaimsScope : IDisposable
{
    private static readonly string[] RoleClaims = { "roles", "role", "groups", "cognito:groups" };

    private readonly IPrincipal? _previous;

    private ClaimsScope(IPrincipal principal)
    {
        _previous = Thread.CurrentPrincipal;
        Thread.CurrentPrincipal = principal;
    }

    public static IDisposable? TryCreate(ToolCallContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Caller) || context.Claims.Count == 0) return null;

        var claims = new List<Claim> { new(ClaimTypes.Name, context.Caller) };

        foreach (var name in RoleClaims)
        {
            if (!context.Claims.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            foreach (var role in value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsScope(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));
    }

    public void Dispose() => Thread.CurrentPrincipal = _previous;
}

/// <summary>
/// Adapts <c>System.Text.Json</c> to the parser the core expects.
/// </summary>
/// <remarks>
/// The core does not depend on a JSON library, so each host supplies its own parser. The argument
/// binder wants plain CLR values — dictionaries, arrays and scalars — which is what this produces.
/// </remarks>
public sealed class SystemTextJsonParser : IJsonObjectParser
{
    public IDictionary<string, object?> ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("The request body must be a JSON object.");

        return (IDictionary<string, object?>)Convert(document.RootElement)!;
    }

    private static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => Convert(p.Value), StringComparer.OrdinalIgnoreCase),
        JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => element.TryGetInt64(out var whole)
            ? whole
            : element.GetDecimal(),
        _ => element.ToString()
    };
}
