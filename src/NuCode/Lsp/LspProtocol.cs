using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuCode.Lsp;

/// <summary>
/// JSON-RPC protocol types and helpers for LSP communication.
/// </summary>
internal static class LspProtocol
{
    private static int s_requestId;

    private static readonly JsonSerializerOptions s_options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int NextId() => Interlocked.Increment(ref s_requestId);

    public static byte[] EncodeMessage(string json)
    {
        var content = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {content.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        var message = new byte[headerBytes.Length + content.Length];
        headerBytes.CopyTo(message, 0);
        content.CopyTo(message, headerBytes.Length);
        return message;
    }

    public static string CreateRequest(int id, string method, object? @params = null)
    {
        var request = new JsonRpcRequest { Id = id, Method = method, Params = @params };
        return JsonSerializer.Serialize(request, s_options);
    }

    public static string CreateNotification(string method, object? @params = null)
    {
        var notification = new JsonRpcNotification { Method = method, Params = @params };
        return JsonSerializer.Serialize(notification, s_options);
    }

    public static string CreateResponse(int id, object? result)
    {
        var response = new JsonRpcOutgoingResponse { Id = id, Result = result };
        return JsonSerializer.Serialize(response, s_options);
    }

    public static string CreateErrorResponse(int id, int code, string message)
    {
        var response = new JsonRpcOutgoingResponse
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message },
        };
        return JsonSerializer.Serialize(response, s_options);
    }

    /// <summary>
    /// Parses an incoming JSON-RPC message and classifies it as a response, notification, or server request.
    /// </summary>
    public static JsonRpcIncoming? ParseIncoming(string json) =>
        JsonSerializer.Deserialize<JsonRpcIncoming>(json, s_options);

    public sealed class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string Jsonrpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public object? Params { get; set; }
    }

    public sealed class JsonRpcNotification
    {
        [JsonPropertyName("jsonrpc")]
        public string Jsonrpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public object? Params { get; set; }
    }

    /// <summary>
    /// Unified incoming message type. Classify by:
    /// - Response: <see cref="Id"/> not null, <see cref="Method"/> is null.
    /// - Notification: <see cref="Method"/> not null, <see cref="Id"/> is null.
    /// - Server request: both <see cref="Id"/> and <see cref="Method"/> are not null.
    /// </summary>
    public sealed class JsonRpcIncoming
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }

        public bool IsResponse => Id is not null && Method is null;
        public bool IsNotification => Method is not null && Id is null;
        public bool IsServerRequest => Method is not null && Id is not null;
    }

    public sealed class JsonRpcOutgoingResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string Jsonrpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("result")]
        public object? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }
    }

    public sealed class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

