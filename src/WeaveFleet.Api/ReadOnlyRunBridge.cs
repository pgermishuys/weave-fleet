using System.Text.Json;
using WeaveFleet.Application.Memory.Dreaming;

namespace WeaveFleet.Api;

/// <summary>
/// A dreaming session may call <c>fleet_session_read</c> and no other of Fleet's tools. The OpenCode plugins call them at
/// <c>/api/bridge/*</c> with the process's bridge token and the harness's session id in the body; this refuses any route but
/// <see cref="ReadOnlyRunGuard.AllowedRoute"/> for a dreaming session (or its subagent), whichever harness it is on.
/// (Tools that reach Fleet over MCP are checked in <c>FleetToolCalls</c>.)
/// </summary>
public static class ReadOnlyRunBridge
{
    private const string Prefix = "/api/bridge/";
    private const int MaxBodyBytes = 1 << 20;

    public static IApplicationBuilder UseReadOnlyRunBridge(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api/bridge") || !HttpMethods.IsPost(context.Request.Method))
        {
            await next();
            return;
        }

        var route = context.Request.Path.Value![Prefix.Length..].Trim('/');
        if (string.Equals(route, ReadOnlyRunGuard.AllowedRoute, StringComparison.Ordinal))
        {
            await next();
            return;
        }

        var token = BearerToken(context.Request);
        string? harnessSessionId = null;
        if (token is not null && context.Request.ContentLength is null or <= MaxBodyBytes)
        {
            context.Request.EnableBuffering();
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "harnessSessionId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                            harnessSessionId = property.Value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON: the endpoint answers it as it always did.
            }

            context.Request.Body.Position = 0;
        }

        var guard = context.RequestServices.GetRequiredService<ReadOnlyRunGuard>();
        if (await guard.RefusesAsync(token, harnessSessionId, route, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorResponse(ReadOnlyRunGuard.Refusal), ApiJsonContext.Default.ErrorResponse, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next();
    });

    private static string? BearerToken(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
    }
}
