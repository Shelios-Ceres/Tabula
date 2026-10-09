using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>OpenAI: Image API (generations / multipart edits, optional SSE), Responses API (text and the image_generation tool) and Audio speech.</summary>
public sealed class OpenAIProvider(ModelChannel channel, HttpClient http) : AiProvider(channel, http)
{
    private Action<HttpRequestHeaders> Auth => Http.Bearer(Key);
    private static readonly string[] Formats = ["png", "jpeg", "webp"];

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var family = ModelFamilies.FindImage(Vendor.OpenAI, request.Model);
        if (request.Count is < 1 or > 10) throw new ApiException("图片数量必须是 1–10 的整数");
        if (request.References.Count > (family?.MaxReferences ?? 16)) throw new ApiException($"{request.Model} 最多支持 {family?.MaxReferences ?? 16} 张参考图");
        if (!Formats.Contains(options.OutputFormat)) throw new ApiException("输出格式无效");
        if (options.OutputFormat == "jpeg" && options.Background == "transparent") throw new ApiException("JPEG 不支持透明背景");
        var size = family is null ? "auto" : ModelFamilies.ImageSize(family, options.Tier, options.Ratio) ?? "auto";
        return options.OpenAIApi == "responses"
            ? await ResponsesImageAsync(request, size, progress, ct).ConfigureAwait(false)
            : await ImagesApiAsync(request, size, progress, ct).ConfigureAwait(false);
    }

    private async Task<ImageResult> ImagesApiAsync(ImageRequest request, string size, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var editing = request.References.Count > 0 || request.Mask is not null;
        var fields = new List<(string Key, string Value)>
        {
            ("model", request.Model), ("prompt", request.Prompt), ("n", request.Count.ToString()), ("size", size),
            ("quality", options.Quality ?? "auto"), ("background", options.Background), ("output_format", options.OutputFormat), ("moderation", options.Moderation),
        };
        if (options.OutputFormat is "jpeg" or "webp") fields.Add(("output_compression", options.Compression.ToString()));
        if (options.Stream) fields.AddRange([("stream", "true"), ("partial_images", Math.Clamp(options.PartialImages, 0, 3).ToString())]);

        HttpContent content;
        if (editing)
        {
            var form = new MultipartFormDataContent();
            foreach (var (key, value) in fields) form.Add(new StringContent(value), key);
            var field = request.References.Count > 1 ? "image[]" : "image";
            for (var i = 0; i < request.References.Count; i++) form.Add(File(request.References[i]), field, $"reference-{i + 1}{Ext(request.References[i])}");
            if (request.Mask is { } mask) form.Add(File(mask), "mask", "mask.png");
            content = form;
        }
        else
        {
            var body = new JsonObject();
            foreach (var (key, value) in fields)
                body[key] = key switch { "n" or "output_compression" or "partial_images" => JsonValue.Create(int.Parse(value)), "stream" => JsonValue.Create(true), _ => JsonValue.Create(value) };
            content = Http.Content(body);
        }

        var url = $"{Base}/images/{(editing ? "edits" : "generations")}";
        var mime = "image/" + options.OutputFormat;
        if (!options.Stream)
        {
            using var response = await Http.SendAsync(HttpMethod.Post, url, Auth, content, ct).ConfigureAwait(false);
            var payload = await Http.ReadJsonAsync(response, ct).ConfigureAwait(false);
            Count(payload["usage"]);
            var images = await ParseImagesAsync(payload["data"], mime, ct).ConfigureAwait(false);
            return images.Count > 0 ? new(images, [], []) : throw new ApiException("接口未返回图片");
        }

        var prefix = editing ? "image_edit" : "image_generation";
        var outputs = new List<GeneratedImage>();
        using (var response = await Http.PostStreamAsync(url, Auth, content, ct).ConfigureAwait(false))
        {
            await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
            {
                if (item.Data == "[DONE]") break;
                var data = JsonNode.Parse(item.Data)!;
                var type = Http.Str(data["type"]) ?? item.Event;
                if (type == "error" || type.EndsWith(".failed")) throw new ApiException(Http.Message(data) ?? "图片生成失败");
                if (Http.Str(data["b64_json"]) is not { } b64) continue;
                var image = new GeneratedImage(Convert.FromBase64String(b64), mime);
                if (type == $"{prefix}.partial_image") progress?.Report(new ImageEvent(Preview: image));
                if (type == $"{prefix}.completed")
                {
                    Count(data["usage"]);
                    outputs.Add(image);
                    progress?.Report(new ImageEvent(Index: outputs.Count - 1, Output: image));
                }
            }
        }
        return outputs.Count > 0 ? new(outputs, [], []) : throw new ApiException("流式响应未返回最终图片");
    }

    /// <summary>
    /// The image_generation tool runs inside a mainline model, so it needs a text model from the same channel.
    /// Always in background mode: submit, report the response ID, then poll, so a long 4K / max job never holds one connection open.
    /// </summary>
    private async Task<ImageResult> ResponsesImageAsync(ImageRequest request, string size, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        if (string.IsNullOrWhiteSpace(options.MainModel)) throw new ApiException("Responses 模式需要在 OpenAI 下选择一个文本主模型");
        if (request.Count != 1) throw new ApiException("Responses 图像工具仅支持图片数量 1");
        var tool = new JsonObject
        {
            ["type"] = "image_generation", ["model"] = request.Model, ["size"] = size, ["quality"] = options.Quality ?? "auto",
            ["background"] = options.Background, ["output_format"] = options.OutputFormat, ["moderation"] = options.Moderation,
        }
        .Set("output_compression", options.OutputFormat is "jpeg" or "webp" ? options.Compression : null)
        .Set("input_image_mask", request.Mask is { } mask ? new JsonObject { ["image_url"] = mask.DataUrl } : null);
        var body = new JsonObject
        {
            ["model"] = options.MainModel, ["input"] = ResponsesApi.Message(request.Prompt, request.References),
            ["tools"] = new JsonArray { tool }, ["tool_choice"] = new JsonObject { ["type"] = "image_generation" }, ["store"] = options.Store, ["background"] = true,
        }
        .Set("previous_response_id", request.PreviousId)
        .Set("reasoning", options.MainReasoning is { } effort ? new JsonObject { ["effort"] = effort } : null);
        var payload = await Http.PostJsonAsync($"{Base}/responses", Auth, body, ct).ConfigureAwait(false);
        if (Http.Str(payload["id"]) is not { } id) throw new ApiException("Responses 未返回任务 ID");
        progress?.Report(new ImageEvent(TaskId: id));
        return await AwaitResponseAsync(id, payload, options.Store, ct).ConfigureAwait(false);
    }

    public override Task<ImageResult> ResumeImageTaskAsync(string taskId, bool store, CancellationToken ct) => AwaitResponseAsync(taskId, null, store, ct);

    /// <summary>Background responses can be cancelled while queued or in progress; cancelling a finished one just returns it.</summary>
    public override async Task CancelTaskAsync(string taskId)
    {
        using var timeout = new CancellationTokenSource(Http.QueryTimeout);
        using var _ = await Http.SendAsync(HttpMethod.Post, $"{Base}/responses/{Uri.EscapeDataString(taskId)}/cancel", Auth, null, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Poll GET /responses/{id} while it is queued or in progress (no overall limit: the user stops it); a dropped poll only skips one round.</summary>
    private async Task<ImageResult> AwaitResponseAsync(string id, JsonNode? payload, bool store, CancellationToken ct)
    {
        var url = $"{Base}/responses/{Uri.EscapeDataString(id)}";
        // Resuming polls at once; after submitting or a pending / failed poll, wait one interval first.
        for (var wait = payload is not null; payload is null || Http.Str(payload["status"]) is "queued" or "in_progress"; wait = true)
        {
            if (wait) await Task.Delay(Http.PollInterval, ct).ConfigureAwait(false);
            try { payload = await Http.QueryJsonAsync(url, Auth, ct).ConfigureAwait(false); }
            catch (Exception ex) when (Http.Transient(ex) && !ct.IsCancellationRequested) { }
        }
        var status = Http.Str(payload["status"]);
        if (status != "completed")
            throw new ApiException(status switch
            {
                "cancelled" => "任务已在服务端取消",
                "incomplete" => $"Responses 未完成：{Http.Str(payload["incomplete_details"]?["reason"]) ?? "未知原因"}",
                _ => Http.Message(payload) ?? $"Responses 状态为 {status}",
            });
        // Only the mainline model's tokens: the hosted image tool reports none of its own.
        Count(payload["usage"]);
        var images = new List<GeneratedImage>();
        var texts = new List<string>();
        foreach (var output in payload["output"].Items())
        {
            if (Http.Str(output["type"]) == "image_generation_call" && Http.Str(output["result"]) is { } result)
                images.Add(new(Convert.FromBase64String(result), "image/" + (Http.Str(output["output_format"]) ?? "png")));
            if (Http.Str(output["type"]) == "message") texts.AddRange(output["content"].Items().Select(item => Http.Str(item["text"])).OfType<string>());
        }
        return images.Count > 0 ? new(images, texts, [], store ? id : null) : throw new ApiException(NoImage(id, payload, texts));
    }

    /// <summary>A completed response without an image: say whether the tool was called, how it ended and what the model wrote instead.</summary>
    private static string NoImage(string id, JsonNode payload, List<string> texts)
    {
        var outputs = payload["output"].Items().ToList();
        var calls = outputs.Where(o => Http.Str(o["type"]) == "image_generation_call").Select(o => Http.Str(o["status"]) ?? "未知").ToList();
        var types = string.Join("、", outputs.Select(o => Http.Str(o["type"])).Distinct());
        var lines = new List<string>
        {
            $"Responses 未返回图片（{id}）",
            calls.Count > 0 ? $"图像工具状态：{string.Join("、", calls)}" : $"主模型没有调用图像工具，输出类型：{(types.Length > 0 ? types : "无")}",
        };
        if (texts.Count > 0) lines.Add($"主模型回复：{string.Join(" ", texts)}");
        return string.Join("\n", lines);
    }

    public override Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct) =>
        ResponsesApi.StreamTextAsync(Http, $"{Base}/responses", Auth, request, onDelta, ct, Count);

    public override async Task<GeneratedImage> SpeechAsync(SpeechRequest request, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = request.Model, ["input"] = request.Input, ["voice"] = request.Voice, ["response_format"] = request.Format, ["speed"] = request.Speed }
            .Set("instructions", string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions);
        using var response = await Http.SendAsync(HttpMethod.Post, $"{Base}/audio/speech", Auth, Http.Content(body), ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var mime = request.Format switch { "wav" => "audio/wav", "opus" => "audio/opus", "aac" => "audio/aac", "flac" => "audio/flac", "pcm" => "audio/pcm", _ => "audio/mpeg" };
        return new(bytes, mime);
    }

    public override async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        var payload = await Http.QueryJsonAsync($"{Base}/models", Auth, ct).ConfigureAwait(false);
        return payload["data"].Items().Select(item => Http.Str(item["id"])).OfType<string>().Order().ToList();
    }

    /// <summary>OpenAI has no balance API: the Costs API (Admin Key only) gives this month's spend, summed over its daily UTC buckets.</summary>
    public override async Task<Balance> BalanceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var payload = await Http.QueryJsonAsync($"{Base}/organization/costs?start_time={start}&bucket_width=1d&limit=31", Http.Bearer(Channel.BillingKey), ct).ConfigureAwait(false);
        var amounts = payload["data"].Items().SelectMany(bucket => bucket["results"].Items()).Select(result => result["amount"]).ToList();
        return new("本月花费", amounts.Sum(amount => Http.Decimal(amount?["value"])), (Http.Str(amounts.FirstOrDefault()?["currency"]) ?? "usd").ToUpperInvariant());
    }

    private async Task<List<GeneratedImage>> ParseImagesAsync(JsonNode? data, string mime, CancellationToken ct)
    {
        var images = new List<GeneratedImage>();
        foreach (var item in data.Items())
        {
            if (Http.Str(item["b64_json"]) is { } b64) images.Add(new(Convert.FromBase64String(b64), mime));
            else if (Http.Str(item["url"]) is { } url)
            {
                var (bytes, type) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
                images.Add(new(bytes, type));
            }
        }
        return images;
    }

    private static ByteArrayContent File(MediaInput input)
    {
        var content = new ByteArrayContent(input.Bytes);
        content.Headers.ContentType = new(input.MimeType);
        return content;
    }

    private static string Ext(MediaInput input) => input.MimeType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
}
