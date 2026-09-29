using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace WeaveFleet.Api;

/// <summary>
/// A request an endpoint can't bind (a field it doesn't know, a wrong type, JSON that isn't JSON, the wrong content
/// type) used to get a 400 or 415 with nothing in it, so an agent calling the API could only guess what was wrong.
/// Minimal APIs throw instead of answering when <see cref="RouteHandlerOptions.ThrowOnBadRequest"/> is on; this
/// catches that and answers with an <see cref="ErrorResponse"/> that says what's wrong and where. Field names come
/// from the request type's JSON metadata; the body itself is never echoed or logged.
/// </summary>
internal static partial class ReadableBadRequests
{
    private const int MaxEchoedLength = 80;

    // The answer is JSON, never HTML: an agent reading it raw sees "prompt" and isn't, not \u0022prompt\u0022 and isn\u0027t.
    private static readonly JsonTypeInfo<ErrorResponse> ErrorJson = new ApiJsonContext(
        new JsonSerializerOptions(ApiJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ErrorResponse;

    public static IServiceCollection AddReadableBadRequests(this IServiceCollection services) =>
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

    /// <summary>Must run after routing, so the endpoint's body type is known, and inside CORS.</summary>
    public static IApplicationBuilder UseReadableBadRequests(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // A charset .NET doesn't know makes reading the body throw something that isn't a bad-request exception.
        if (JsonBodyType(context) is not null && UnknownCharset(context.Request) is { } charset)
        {
            var error = $"The body says it's in \"{Echo(charset)}\", which Fleet can't read. Send the JSON encoded as UTF-8.";
            await AnswerAsync(context, StatusCodes.Status415UnsupportedMediaType, error, logged: error);
            return;
        }

        try
        {
            await next();

            // Routing turns a request away for its content type before any endpoint runs, with an empty 415. Every
            // endpoint that takes a body takes JSON.
            if (context.Response.StatusCode == StatusCodes.Status415UnsupportedMediaType
                && !context.Response.HasStarted && context.Response.ContentType is null)
            {
                var error = WrongContentType(context.Request);
                await AnswerAsync(context, StatusCodes.Status415UnsupportedMediaType, error, logged: error);
            }
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            var error = Describe(context, ex);
            // The framework's own messages can quote a query value; only the ones written here go to the log.
            await AnswerAsync(context, ex.StatusCode, error, logged: error == ex.Message ? "(framework message, not logged)" : error);
        }
    });

    internal static string Describe(HttpContext context, BadHttpRequestException ex)
    {
        var bodyType = JsonBodyType(context);
        if (bodyType is null)
            return ex.Message;

        if (ex.StatusCode == StatusCodes.Status415UnsupportedMediaType)
            return WrongContentType(context.Request);

        var options = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        try
        {
            var root = options.GetTypeInfo(bodyType);
            if (ex.InnerException is JsonException json)
                return DescribeJson(json, root);
            if (HasNoBody(context))
                return $"The body is empty. This endpoint takes {Shape(root, isBody: true)}.";
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // Metadata the describer can't walk; the plain message below still beats an empty body.
        }

        return ex.InnerException is JsonException
            ? "The body couldn't be read as JSON for this endpoint."
            : ex.Message;
    }

    private static string WrongContentType(HttpRequest request) =>
        string.IsNullOrEmpty(request.ContentType)
            ? "Send the body as JSON with Content-Type: application/json (this request had no Content-Type)."
            : $"Send the body as JSON with Content-Type: application/json (this request said \"{Echo(request.ContentType)}\").";

    private static string DescribeJson(JsonException json, JsonTypeInfo root)
    {
        // The reader's own exception inside means the bytes aren't JSON at all.
        if (json.InnerException is JsonException reader)
        {
            return $"The body isn't valid JSON (line {json.LineNumber + 1}, byte {json.BytePositionInLine + 1}): "
                   + WithoutPosition(reader.Message);
        }

        var path = ParsePath(json.Path);
        if (json.InnerException?.InnerException is DecoderFallbackException)
        {
            var at = Where(path);
            return at.Length == 0
                ? "The body isn't valid UTF-8. Send the JSON encoded as UTF-8."
                : $"The body isn't valid UTF-8 (in \"{Echo(at)}\"). Send the JSON encoded as UTF-8.";
        }

        var type = root;
        var where = new StringBuilder();
        foreach (var segment in path)
        {
            if (segment.Index is { } index)
            {
                if (type.Kind != JsonTypeInfoKind.Enumerable || type.ElementType is null)
                    return Fallback(json, where);
                type = type.Options.GetTypeInfo(type.ElementType);
                where.Append('[').Append(index).Append(']');
                continue;
            }

            var name = segment.Name!;
            if (type.Kind == JsonTypeInfoKind.Dictionary && type.ElementType is not null)
            {
                type = type.Options.GetTypeInfo(type.ElementType);
            }
            else if (type.Kind == JsonTypeInfoKind.Object)
            {
                var property = FindProperty(type, name);
                if (property is null)
                {
                    if ((type.UnmappedMemberHandling ?? type.Options.UnmappedMemberHandling) != JsonUnmappedMemberHandling.Disallow)
                        return Fallback(json, where);

                    var fields = string.Join(", ", type.Properties.Select(p => p.Name));
                    return where.Length == 0
                        ? $"Unknown field \"{Echo(name)}\". Fields this endpoint takes: {fields}."
                        : $"Unknown field \"{Echo(name)}\" in \"{where}\". Fields it takes: {fields}.";
                }

                type = type.Options.GetTypeInfo(property.PropertyType);
                name = property.Name;
            }
            else
            {
                return Fallback(json, where);
            }

            if (where.Length > 0)
                where.Append('.');
            where.Append(name);
        }

        var shape = Shape(type, isBody: where.Length == 0);
        if (shape is null)
            return Fallback(json, where);
        return where.Length == 0 ? $"The body should be {shape}." : $"\"{where}\" should be {shape}.";
    }

    /// <summary>What the value should look like, in plain words; null when the type reads its own way.</summary>
    private static string? Shape(JsonTypeInfo type, bool isBody = false)
    {
        switch (type.Kind)
        {
            case JsonTypeInfoKind.Object:
                var fields = string.Join(", ", type.Properties.Select(p => p.IsRequired ? $"{p.Name} (required)" : p.Name));
                var noun = isBody ? "a JSON object" : "an object";
                return fields.Length == 0 ? noun : $"{noun} with these fields: {fields}";
            case JsonTypeInfoKind.Enumerable when type.ElementType is not null:
                return $"a list of {Plural(type.Options.GetTypeInfo(type.ElementType))}";
            case JsonTypeInfoKind.Dictionary:
                return isBody ? "a JSON object" : "an object";
        }

        var t = Nullable.GetUnderlyingType(type.Type) ?? type.Type;
        if (t == typeof(string) || t == typeof(char) || t == typeof(Guid) || t == typeof(Uri))
            return "a string";
        if (t == typeof(bool))
            return "true or false";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
            return "a date and time string (ISO 8601)";
        if (IsNumber(t))
            return "a number";
        return null;
    }

    private static string Plural(JsonTypeInfo element) => element.Kind switch
    {
        JsonTypeInfoKind.Object or JsonTypeInfoKind.Dictionary => "objects",
        JsonTypeInfoKind.Enumerable => "lists",
        _ => Shape(element) switch
        {
            "a string" => "strings",
            "a number" => "numbers",
            "true or false" => "true or false values",
            _ => "values",
        },
    };

    private static bool IsNumber(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte)
        || t == typeof(double) || t == typeof(float) || t == typeof(decimal);

    /// <summary>A failure the describer can't place: say where, and keep a converter's own message.</summary>
    private static string Fallback(JsonException json, StringBuilder where)
    {
        var at = where.Length > 0 ? $"\"{where}\"" : "the body";
        // Messages System.Text.Json writes itself end with the path and name .NET types; a converter's don't.
        return json.Message.Contains(" Path: ", StringComparison.Ordinal)
            ? $"Fleet couldn't read {at} as this endpoint expects."
            : $"Fleet couldn't read {at}: {json.Message}";
    }

    private static JsonPropertyInfo? FindProperty(JsonTypeInfo type, string name)
    {
        var comparison = type.Options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var property = type.Properties.FirstOrDefault(p => string.Equals(p.Name, name, comparison));
        if (property is not null || type.PolymorphismOptions is null)
            return property;

        return type.PolymorphismOptions.DerivedTypes
            .SelectMany(d => type.Options.GetTypeInfo(d.DerivedType).Properties)
            .FirstOrDefault(p => string.Equals(p.Name, name, comparison));
    }

    /// <summary>The type an endpoint reads its JSON body into, or null when it doesn't take one.</summary>
    private static Type? JsonBodyType(HttpContext context)
    {
        var accepts = context.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>();
        if (accepts?.RequestType is not { } type || type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(object))
            return null;
        return accepts.ContentTypes.Any(c => c.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) ? type : null;
    }

    private static string? UnknownCharset(HttpRequest request)
    {
        if (!request.HasJsonContentType()
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || mediaType.Charset.Length == 0)
        {
            return null;
        }

        var charset = mediaType.Charset.Value!.Trim('"');
        try
        {
            Encoding.GetEncoding(charset);
            return null;
        }
        catch (ArgumentException)
        {
            return charset;
        }
    }

    private static bool HasNoBody(HttpContext context) =>
        context.Request.ContentLength == 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false;

    private static async Task AnswerAsync(HttpContext context, int statusCode, string error, string logged)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ReadableBadRequests).FullName!);
        // Path without its base: an agent's base carries its bridge token.
        LogUnreadableRequest(logger, context.Request.Method, context.Request.Path.Value ?? "/", statusCode, logged);

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(error), ErrorJson);
    }

    private static string WithoutPosition(string message)
    {
        var end = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (end < 0 ? message : message[..end]).Replace(" Change the reader options.", "", StringComparison.Ordinal);
    }

    private static string Echo(string value) =>
        value.Length <= MaxEchoedLength ? value : string.Concat(value.AsSpan(0, MaxEchoedLength), "…");

    private static string Where(List<PathSegment> path)
    {
        var where = new StringBuilder();
        foreach (var segment in path)
        {
            if (segment.Index is { } index)
                where.Append('[').Append(index).Append(']');
            else
                where.Append(where.Length > 0 ? "." : "").Append(segment.Name);
        }
        return where.ToString();
    }

    /// <summary>Splits System.Text.Json's path (<c>$.a['b.c'][0]</c>) into names and indexes.</summary>
    internal static List<PathSegment> ParsePath(string? path)
    {
        var segments = new List<PathSegment>();
        if (string.IsNullOrEmpty(path))
            return segments;

        var i = path.StartsWith('$') ? 1 : 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = path.IndexOfAny(['.', '['], i + 1);
                if (end < 0)
                    end = path.Length;
                segments.Add(new PathSegment(path[(i + 1)..end], null));
                i = end;
            }
            else if (path.AsSpan(i).StartsWith("['"))
            {
                var end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    break;
                segments.Add(new PathSegment(path[(i + 2)..end], null));
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), out var index))
                    break;
                segments.Add(new PathSegment(null, index));
                i = end + 1;
            }
            else
            {
                break;
            }
        }
        return segments;
    }

    internal readonly record struct PathSegment(string? Name, int? Index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} {Path} couldn't read the request ({StatusCode}): {Error}")]
    private static partial void LogUnreadableRequest(ILogger logger, string method, string path, int statusCode, string error);
}
