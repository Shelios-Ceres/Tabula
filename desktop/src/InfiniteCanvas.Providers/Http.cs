using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfiniteCanvas.Providers;

public sealed record SseEvent(string Event, string Data);

/// <summary>Thin HTTP helpers shared by the adapters; each adapter owns its JSON shapes and event semantics.</summary>
public sealed class Http(HttpClient client)
{
    public static Action<HttpRequestHeaders> Bearer(string key) => headers => headers.Authorization = new("Bearer", key);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Action<HttpRequestHeaders>? headers, HttpContent? content, CancellationToken ct, bool stream = false)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        headers?.Invoke(request.Headers);
        if (stream) request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await client.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);
        return response;
    }

    public async Task<JsonNode> JsonAsync(HttpMethod method, string url, Action<HttpRequestHeaders>? headers, JsonNode? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, url, headers, body is null ? null : Content(body), ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Parse a 2xx body; an HTML page or a null body means the base URL points at a website, not the API.</summary>
    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? json = null;
        try { json = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); } catch (JsonException) { }
        if (json is null) throw new ApiException("接口返回的不是 JSON，请检查接口地址", (int)response.StatusCode, RequestId(response, null));
        // Some gateways answer 200 with an error envelope.
        if (json is JsonObject obj && obj["error"] is JsonNode error && Message(error) is { } message) throw new ApiException(message, (int)response.StatusCode, RequestId(response, obj));
        return json;
    }

    public Task<JsonNode> PostJsonAsync(string url, Action<HttpRequestHeaders>? headers, JsonNode body, CancellationToken ct) => JsonAsync(HttpMethod.Post, url, headers, body, ct);
    public Task<JsonNode> GetJsonAsync(string url, Action<HttpRequestHeaders>? headers, CancellationToken ct) => JsonAsync(HttpMethod.Get, url, headers, null, ct);

    /// <summary>Delay between status polls of background image and video tasks.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>A dropped connection, a timed-out query or a gateway error says nothing about the vendor task, so polling goes on and its ID is kept.</summary>
    public static bool Transient(Exception ex) => ex is HttpRequestException or IOException or TimeoutException or ApiException { Status: 408 or 429 or >= 500 };

    /// <summary>One status poll, model listing or balance query may take at most this long, so a half-open connection after sleep or a network switch fails instead of hanging. Never used for generation requests or result downloads.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>GET, or POST when a body is given (signed billing APIs).</summary>
    public async Task<JsonNode> QueryJsonAsync(string url, Action<HttpRequestHeaders>? headers, CancellationToken ct, HttpContent? post = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            using var response = await SendAsync(post is null ? HttpMethod.Get : HttpMethod.Post, url, headers, post, timeout.Token).ConfigureAwait(false);
            return await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("请求超时（60 秒），请检查网络或接口地址"); }
    }

    public Task<HttpResponseMessage> PostStreamAsync(string url, Action<HttpRequestHeaders>? headers, HttpContent content, CancellationToken ct) => SendAsync(HttpMethod.Post, url, headers, content, ct, stream: true);

    public async Task<(byte[] Bytes, string MimeType)> DownloadAsync(string url, Action<HttpRequestHeaders>? headers, CancellationToken ct)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return DecodeDataUrl(url);
        using var response = await SendAsync(HttpMethod.Get, url, headers, null, ct).ConfigureAwait(false);
        var mime = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (mime.Contains("json")) throw new ApiException(Message(JsonNode.Parse(bytes)) ?? "下载结果失败");
        return (bytes, mime);
    }

    public static StringContent Content(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    public static async IAsyncEnumerable<SseEvent> ReadSse(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var name = "message";
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0) yield return new SseEvent(name, data.ToString());
                name = "message";
                data.Clear();
                if (line is null) yield break;
            }
            else if (line.StartsWith("data:")) data.Append(data.Length > 0 ? "\n" : "").Append(line[5..].TrimStart(' '));
            else if (line.StartsWith("event:")) name = line[6..].Trim();
        }
    }

    public static (byte[] Bytes, string MimeType) DecodeDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        var header = url[5..comma];
        return (Convert.FromBase64String(url[(comma + 1)..]), header.Split(';')[0]);
    }

    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A money figure sent as a number or a string (billing APIs often use strings, sometimes with thousands separators).</summary>
    public static decimal Decimal(JsonNode? node) =>
        node is JsonValue value && (value.TryGetValue<decimal>(out var number) || decimal.TryParse(Str(value), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) ? number : 0;

    /// <summary>Error text across vendors: error.message, message, msg, code, or a plain error string; Volcengine and Alibaba Cloud OpenAPIs capitalize (ResponseMetadata.Error.Message, Message).</summary>
    public static string? Message(JsonNode? node) => node switch
    {
        JsonValue value => Str(value),
        JsonObject obj => Message(obj["error"] ?? obj["Error"] ?? obj["ResponseMetadata"]) ?? Str(obj["message"] ?? obj["Message"]) ?? Str(obj["msg"]) ?? Message(obj["errors"]) ?? Str(obj["code"]),
        JsonArray array => array.Select(Message).FirstOrDefault(text => text is not null),
        _ => null,
    };

    private static string? RequestId(HttpResponseMessage response, JsonObject? body)
    {
        foreach (var header in new[] { "x-request-id", "request-id", "x-tt-logid" })
            if (response.Headers.TryGetValues(header, out var values)) return values.FirstOrDefault();
        return Str(body?["request_id"]);
    }

    private static async Task<ApiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? body = null;
        try { body = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        var message = Message(body) ?? (text.TrimStart().StartsWith('<') ? null : text.Trim()) ?? "";
        if (string.IsNullOrWhiteSpace(message)) message = status switch
        {
            401 or 403 => "鉴权失败，请检查 API Key",
            404 => "接口或模型不存在，请检查接口地址与模型名称",
            429 => "请求过于频繁或额度不足",
            >= 500 => $"服务暂时不可用（HTTP {status}）",
            _ => $"请求失败（HTTP {status}）",
        };
        return new ApiException(message.Length > 600 ? message[..600] : message, status, RequestId(response, body));
    }
}

public static class JsonExtensions
{
    /// <summary>Add a property only when it has a value, keeping request bodies minimal.</summary>
    public static JsonObject Set(this JsonObject obj, string key, JsonNode? value)
    {
        if (value is not null) obj[key] = value;
        return obj;
    }

    public static IEnumerable<JsonNode> Items(this JsonNode? node) => node is JsonArray array ? array.OfType<JsonNode>() : [];
}
