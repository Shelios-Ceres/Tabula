using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Providers;

namespace InfiniteCanvas.Tests;

/// <summary>Request shapes and response parsing checked against the samples in each vendor's API reference.</summary>
public class ProviderTests
{
    private static readonly string Png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
    private static readonly MediaInput Reference = new([1, 2, 3], "image/png");

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(e => e + "\n\n")), Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Bytes(string mime) => new(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9]) { Headers = { ContentType = new(mime) } } };

    private static (AiProvider Provider, FakeHandler Handler) Create(Vendor vendor, Func<HttpRequestMessage, string, HttpResponseMessage> respond, string baseUrl = "", Action<ModelChannel>? configure = null)
    {
        var handler = new FakeHandler(respond);
        var channel = new ModelChannel { Vendor = vendor, BaseUrl = baseUrl, ApiKey = "test-key" };
        configure?.Invoke(channel);
        return (AiProvider.For(channel, new HttpClient(handler)), handler);
    }

    private static ImageRequest Image(string model, int count = 1, IReadOnlyList<MediaInput>? refs = null, ImageOptions? options = null) =>
        new(model, "一只猫", refs ?? [], null, count, options ?? new ImageOptions());

    [Fact]
    public async Task OpenAI_generations_sends_json_and_reads_b64()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        var result = await provider.GenerateImageAsync(Image("gpt-image-2.5-flare", options: new() { Tier = "2K", Ratio = "16:9", Quality = "xhigh" }), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.Equal("https://api.openai.com/v1/images/generations", request.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("2048x1152", (string?)json["size"]);
        Assert.Equal("xhigh", (string?)json["quality"]);
        Assert.Equal(1, (int?)json["n"]);
        Assert.Null(json["response_format"]);
        Assert.Single(result.Images);
    }

    [Theory]
    [InlineData("gpt-image-2.6-sunburst", "1K", "672x2016")]
    [InlineData("gpt-image-2.6-sunburst", "1K", "2016x672", "3:1")]
    [InlineData("gpt-image-2.6-sunburst", "2K", "2688x896", "3:1")]
    [InlineData("gpt-image-2.6-sunburst", "4K", "3840x1280", "3:1")]
    [InlineData("gpt-image-2.6-flare", "1K", "2016x672", "3:1")]
    [InlineData("gpt-image-2.6-flare", "2K", "2688x896", "3:1")]
    [InlineData("gpt-image-2.6-flare", "4K", "3840x1280", "3:1")]
    [InlineData("gpt-image-2.6-sunburst", "2K", "896x2688")]
    [InlineData("gpt-image-2.6-sunburst", "4K", "1280x3840")]
    [InlineData("gpt-image-2.6-flare", "1K", "672x2016")]
    [InlineData("gpt-image-2.6-flare", "2K", "896x2688")]
    [InlineData("gpt-image-2.6-flare", "4K", "1280x3840")]
    public async Task OpenAI_gpt_image_2_6_sends_a_true_1_to_3_or_3_to_1_size(string model, string tier, string size, string ratio = "1:3")
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        await provider.GenerateImageAsync(Image(model, options: new() { Tier = tier, Ratio = ratio }), null, default);
        Assert.Equal(size, (string?)JsonNode.Parse(handler.Calls.Single().Body)!["size"]);

        // The documented custom-size rules for GPT Image 2.6: multiples of 16, ratio from 1:3 to 3:1, an edge of at most 3840, 655,360 to 8,294,400 pixels.
        var (width, height) = (int.Parse(size.Split('x')[0]), int.Parse(size.Split('x')[1]));
        Assert.Equal((0, 0), (width % 16, height % 16));
        if (ratio == "1:3") Assert.Equal(3 * width, height); else Assert.Equal(3 * height, width);
        Assert.True(Math.Max(width, height) <= 3840 && width * height is >= 655_360 and <= 8_294_400);
    }

    [Fact]
    public void Only_gpt_image_2_6_offers_1_to_3_and_a_left_over_1_to_3_falls_back_to_a_square_not_another_tall_shape()
    {
        string[] models = ["gpt-image-2.6-sunburst", "gpt-image-2.6-flare", "gpt-image-2.5-sunburst", "gpt-image-2.5-flare", "gpt-image-2", "gpt-image-1.5", "gpt-image-1-mini", "qwen-image-3.0-pro", "qwen-image-max"];
        var offered = new List<string>();
        foreach (var model in models)
            foreach (var vendor in new[] { Vendor.OpenAI, Vendor.Qwen })
                if (ModelFamilies.FindImage(vendor, model) is { } family && family.Ratios.Contains("1:3") && family.Ratios.Contains("3:1")) offered.Add(model);
        Assert.Equal(["gpt-image-2.6-sunburst", "gpt-image-2.6-flare"], offered);
        Assert.Equal(["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "16:9", "9:16", "21:9", "9:21", "1:3", "3:1"], ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2.6-flare")!.Ratios);

        // A node that kept 1:3 from a 2.6 model and then moved to another GPT Image model gets the 1:1 size, not 1:3 or some other tall one.
        // (For Qwen the generator drops a ratio the model does not list before it reaches the provider.)
        foreach (var model in new[] { "gpt-image-2.5-flare", "gpt-image-1.5" })
            Assert.Equal(ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.OpenAI, model)!, "2K", "1:1"), ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.OpenAI, model)!, "2K", "1:3"));

        // The auto tier sends "auto" whatever the ratio: the model picks the shape, as it already does for every other ratio.
        var sixth = ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2.6-flare")!;
        Assert.Equal("auto", ModelFamilies.ImageSize(sixth, "auto", "1:3"));
        Assert.Equal("auto", ModelFamilies.ImageSize(sixth, "auto", "9:21"));
        Assert.Equal("1280x3840", ModelFamilies.ImageSize(sixth, "4K", "1:3"));
        Assert.Equal("auto", ModelFamilies.ImageSize(sixth, "auto", "3:1"));
        Assert.Equal("3840x1280", ModelFamilies.ImageSize(sixth, "4K", "3:1"));
        // The other GPT Image models do not send 3:1 either: a left-over one becomes 1:1.
        foreach (var model in new[] { "gpt-image-2.5-flare", "gpt-image-1.5" })
            Assert.Equal(ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.OpenAI, model)!, "2K", "1:1"), ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.OpenAI, model)!, "2K", "3:1"));
    }

    [Fact]
    public async Task OpenAI_edits_use_multipart_image_array()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        await provider.GenerateImageAsync(Image("gpt-image-2", refs: [Reference, Reference]), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.EndsWith("/v1/images/edits", request.RequestUri!.ToString());
        Assert.Contains("name=\"image[]\"", body);
        Assert.Contains("name=model", body);
    }

    [Fact]
    public async Task OpenAI_image_2_6_sends_complexity_and_gif_frames_without_streaming()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        var result = await provider.GenerateImageAsync(Image("gpt-image-2.6-flare", count: 2, options: new() { OutputFormat = "gif", Complexity = "low", FrameCount = 12, FrameDurationMs = 120, Stream = true, PartialImages = 2 }), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.EndsWith("/v1/images/generations", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal(("gif", "low", 12, 120, 2), ((string?)json["output_format"], (string?)json["complexity"], (int?)json["frame_count"], (int?)json["frame_duration_ms"], (int?)json["n"]));
        // GIF has no streaming or partial images.
        Assert.Null(json["stream"]);
        Assert.Null(json["partial_images"]);
        Assert.Equal("image/gif", result.Images[0].MimeType);
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("gpt-image-2.6-flare", options: new() { OutputFormat = "gif", FrameCount = 65 }), null, default));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("gpt-image-2.6-flare", options: new() { OutputFormat = "gif", Background = "transparent" }), null, default));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task OpenAI_image_2_6_svg_is_one_text_to_image_picture()
    {
        var svg = Convert.ToBase64String(Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1536 1024"/>"""));
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{svg}}"}]}"""));
        var result = await provider.GenerateImageAsync(Image("gpt-image-2.6-sunburst", options: new() { OutputFormat = "svg", Complexity = "high" }), null, default);
        Assert.Equal("image/svg+xml", result.Images.Single().MimeType);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal(("svg", "high", 1), ((string?)json["output_format"], (string?)json["complexity"], (int?)json["n"]));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("gpt-image-2.6-sunburst", count: 2, options: new() { OutputFormat = "svg" }), null, default));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("gpt-image-2.6-sunburst", refs: [Reference], options: new() { OutputFormat = "svg" }), null, default));
        // Only GPT Image 2.6 has vector and animated output.
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("gpt-image-2.5-flare", options: new() { OutputFormat = "gif" }), null, default));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task OpenAI_markup_goes_with_the_edit_and_never_with_a_mask()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        var markup = new MediaInput([4, 5, 6], "image/png");
        await provider.GenerateImageAsync(new ImageRequest("gpt-image-2.6-flare", "把圈出的杯子换成蓝色", [Reference], null, 1, new() { Complexity = "medium" }, Markup: markup), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.EndsWith("/v1/images/edits", request.RequestUri!.ToString());
        Assert.Contains("name=markup", body);
        Assert.Contains("filename=markup.png", body);
        Assert.Contains("name=complexity", body);
        Assert.DoesNotContain("name=mask", body);
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(new ImageRequest("gpt-image-2.6-flare", "改", [Reference], Reference, 1, new(), Markup: markup), null, default));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(new ImageRequest("gpt-image-2.5-flare", "改", [Reference], null, 1, new() { OpenAIApi = "responses", MainModel = "gpt-6-luna" }, Markup: markup), null, default));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task OpenAI_stream_reports_partial_and_completed_images()
    {
        var (provider, _) = Create(Vendor.OpenAI, (_, _) => Sse(
            $$"""data: {"type":"image_generation.partial_image","b64_json":"{{Png}}","partial_image_index":0}""",
            $$"""data: {"type":"image_generation.completed","b64_json":"{{Png}}"}"""));
        var events = new List<ImageEvent>();
        var result = await provider.GenerateImageAsync(Image("gpt-image-2", options: new() { Stream = true, PartialImages = 1 }), new SyncProgress<ImageEvent>(events.Add), default);
        Assert.Single(result.Images);
        Assert.Contains(events, e => e.Preview is not null);
    }

    [Fact]
    public async Task Responses_text_streams_deltas()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Sse(
            """data: {"type":"response.output_text.delta","delta":"你"}""",
            """data: {"type":"response.output_text.delta","delta":"好"}""",
            """data: {"type":"response.completed","response":{"status":"completed"}}"""));
        var deltas = new List<string>();
        var text = await provider.GenerateTextAsync(new TextRequest("gpt-5.5", "打个招呼", [], "简短", "high"), deltas.Add, default);
        Assert.Equal("你好", text);
        Assert.Equal(["你", "你好"], deltas);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal("high", (string?)json["reasoning"]!["effort"]);
        Assert.Equal("简短", (string?)json["instructions"]);
    }

    [Theory]
    [InlineData(Vendor.OpenAI, false)]
    [InlineData(Vendor.OpenAI, true)]
    [InlineData(Vendor.XAI, false)]
    [InlineData(Vendor.XAI, true)]
    public async Task Responses_text_requires_completed_even_after_done_or_eof(Vendor vendor, bool done)
    {
        var events = new List<string> { """data: {"type":"response.output_text.delta","delta":"尚未完成"}""" };
        if (done) events.Add("data: [DONE]");
        var (provider, _) = Create(vendor, (_, _) => Sse(events.ToArray()));
        var deltas = new List<string>();
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), deltas.Add, default));
        Assert.Contains("完成前中断", error.Message);
        Assert.Equal(["尚未完成"], deltas);
        Assert.Equal(default(TokenUsage), provider.Tokens);
    }

    [Theory]
    [InlineData(Vendor.OpenAI)]
    [InlineData(Vendor.XAI)]
    public async Task Responses_empty_stream_is_not_a_success(Vendor vendor)
    {
        var (provider, _) = Create(vendor, (_, _) => Sse());
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), null, default));
    }

    [Theory]
    [InlineData(Vendor.OpenAI)]
    [InlineData(Vendor.XAI)]
    public async Task Responses_text_done_is_preserved_if_the_response_never_completes(Vendor vendor)
    {
        var (provider, _) = Create(vendor, (_, _) => Sse("""data: {"type":"response.output_text.done","text":"已收到的正文"}"""));
        var deltas = new List<string>();
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), deltas.Add, default));
        Assert.Equal(["已收到的正文"], deltas);
    }

    [Theory]
    [InlineData(Vendor.OpenAI)]
    [InlineData(Vendor.XAI)]
    public async Task Responses_completed_finishes_without_waiting_for_stream_eof(Vendor vendor)
    {
        var (provider, _) = Create(vendor, (_, _) => Sse(
            """data: {"type":"response.output_text.delta","delta":"完成"}""",
            """data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":2,"output_tokens":3}}}""",
            """data: {"type":"error","message":"完成后的无关数据"}"""));
        Assert.Equal("完成", await provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), null, default));
        Assert.Equal(new TokenUsage(2, 3), provider.Tokens);
    }

    [Fact]
    public async Task Google_interactions_wraps_user_input_and_reads_steps()
    {
        var (provider, handler) = Create(Vendor.Google, (_, _) => Json($$"""{"id":"i1","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"好的"},{"type":"image","mime_type":"image/jpeg","data":"{{Png}}"}]}]}"""));
        var result = await provider.GenerateImageAsync(Image("gemini-nano-banana-2.1", refs: [Reference], options: new() { Tier = "2K", Ratio = "16:9", Thinking = "high", WebSearch = true }), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.RequestUri!.ToString());
        Assert.Equal("test-key", request.Headers.GetValues("x-goog-api-key").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("user_input", (string?)json["input"]![0]!["type"]);
        Assert.Equal("image", (string?)json["input"]![0]!["content"]![1]!["type"]);
        Assert.Equal("2K", (string?)json["response_format"]!["image_size"]);
        Assert.Equal("high", (string?)json["generation_config"]!["thinking_level"]);
        Assert.Equal("web_search", (string?)json["tools"]![0]!["search_types"]![0]);
        Assert.Single(result.Images);
        Assert.Equal(["好的"], result.Texts);
    }

    [Fact]
    public async Task Ark_seedream_maps_size_and_sequential_generation()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json($$$"""{"data":[{"b64_json":"{{{Png}}}","size":"2848x1600"},{"error":{"code":"OutputImageSensitiveContentDetected","message":"敏感"}}]}"""));
        var result = await provider.GenerateImageAsync(Image("doubao-seedream-4-5-251128", count: 2, options: new() { Tier = "2K", Ratio = "16:9", OutputFormat = "png" }), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/images/generations", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("2848x1600", (string?)json["size"]);
        Assert.Equal("auto", (string?)json["sequential_image_generation"]);
        Assert.Equal(2, (int?)json["sequential_image_generation_options"]!["max_images"]);
        Assert.Null(json["output_format"]);
        Assert.Single(result.Images);
        Assert.Equal(["敏感"], result.Errors);
    }

    [Fact]
    public async Task Ark_stream_collects_partial_success_events()
    {
        var (provider, _) = Create(Vendor.Ark, (_, _) => Sse(
            $$"""event: image_generation.partial_succeeded{{"\n"}}data: {"type":"image_generation.partial_succeeded","image_index":0,"b64_json":"{{Png}}"}""",
            """event: image_generation.partial_failed""" + "\n" + """data: {"type":"image_generation.partial_failed","image_index":1,"error":{"message":"失败"}}""",
            """event: image_generation.completed""" + "\n" + """data: {"type":"image_generation.completed","usage":{"generated_images":1}}"""));
        var result = await provider.GenerateImageAsync(Image("doubao-seedream-5-0-260128", count: 2, options: new() { Stream = true }), null, default);
        Assert.Single(result.Images);
        Assert.Equal(["失败"], result.Errors);
    }

    [Fact]
    public async Task Ark_stream_delivers_finished_images_before_a_later_failure()
    {
        var (provider, _) = Create(Vendor.Ark, (_, _) => Sse(
            $$"""data: {"type":"image_generation.partial_succeeded","image_index":0,"b64_json":"{{Png}}"}""",
            """data: {"type":"error","error":{"message":"后续图片失败"}}"""));
        var events = new List<ImageEvent>();
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("doubao-seedream-5-0-260128", count: 2, options: new() { Stream = true }), new SyncProgress<ImageEvent>(events.Add), default));
        var output = Assert.Single(events);
        Assert.Equal(0, output.Index);
        Assert.Null(output.Preview);
        Assert.Equal(Convert.FromBase64String(Png), output.Output!.Bytes);
    }

    [Fact]
    public async Task Ark_stream_keeps_the_finished_image_event_when_the_batch_is_cancelled()
    {
        var (provider, _) = Create(Vendor.Ark, (_, _) => Sse(
            $$"""data: {"type":"image_generation.partial_succeeded","image_index":0,"b64_json":"{{Png}}"}""",
            """data: {"type":"image_generation.completed"}"""));
        using var stop = new CancellationTokenSource();
        var events = new List<ImageEvent>();
        var progress = new SyncProgress<ImageEvent>(value => { events.Add(value); if (value.Output is not null) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateImageAsync(Image("doubao-seedream-5-0-260128", count: 2, options: new() { Stream = true }), progress, stop.Token));
        Assert.Equal(Convert.FromBase64String(Png), Assert.Single(events).Output!.Bytes);
    }

    [Fact]
    public async Task Xai_edits_send_images_array_with_type()
    {
        var (provider, handler) = Create(Vendor.XAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}","mime_type":"image/png"}]}"""));
        var result = await provider.GenerateImageAsync(Image("grok-imagine-image-2.0", refs: [Reference, Reference], options: new() { Tier = "2k", Ratio = "16:9", Quality = "medium" }), null, default);
        var (request, body) = handler.Calls.Single();
        Assert.Equal("https://api.x.ai/v1/images/edits", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("image_url", (string?)json["images"]![1]!["type"]);
        Assert.Null(json["image"]);
        Assert.Equal("medium", (string?)json["quality"]);
        Assert.Equal("image/png", result.Images.Single().MimeType);
    }

    [Fact]
    public async Task Xai_generic_image_family_sends_no_quality_or_unknown_resolution()
    {
        var (provider, handler) = Create(Vendor.XAI, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        await provider.GenerateImageAsync(Image("grok-imagine-image", options: new() { Quality = "low", Tier = "1.5k" }), null, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Null(json["quality"]);
        Assert.Null(json["resolution"]);
    }

    [Fact]
    public async Task Google_tts_sends_voice_and_style_and_reads_wav()
    {
        var wav = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]);
        var (provider, handler) = Create(Vendor.Google, (_, _) => Json($$"""{"id":"i2","status":"completed","steps":[{"type":"model_output","content":[{"type":"audio","mime_type":"audio/wav","data":"{{wav}}"}]}]}"""));
        var audio = await provider.SpeechAsync(new SpeechRequest("gemini-3.8-flash-tts", "祝你今天愉快", "alloy", "mp3", 1, "开心地"), default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal("audio", (string?)json["response_format"]!["type"]);
        // A voice from another vendor falls back to the first Gemini voice.
        Assert.Equal("Kore", (string?)json["generation_config"]!["speech_config"]![0]!["voice"]);
        Assert.Equal("开心地", (string?)json["input"]![0]!["content"]![0]!["annotations"]![0]!["style"]);
        Assert.Equal(("audio/wav", 4), (audio.MimeType, audio.Bytes.Length));
    }

    [Fact]
    public async Task Qwen_image_puts_images_before_text_and_downloads_urls()
    {
        var (provider, handler) = Create(Vendor.Qwen, (request, _) => request.Method == HttpMethod.Post
            ? Json("""{"output":{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":[{"image":"https://oss.example/a.png"}]}}]},"request_id":"r1"}""")
            : Bytes("image/png"));
        var result = await provider.GenerateImageAsync(Image("qwen-image-3.0-pro", refs: [Reference], options: new() { Tier = "1K", Ratio = "16:9", NegativePrompt = "模糊" }), null, default);
        var (request, body) = handler.Calls[0];
        Assert.Equal("https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        var content = json["input"]!["messages"]![0]!["content"]!.AsArray();
        Assert.NotNull(content[0]!["image"]);
        Assert.Equal("一只猫", (string?)content[1]!["text"]);
        Assert.Equal("1984*1120", (string?)json["parameters"]!["size"]);
        Assert.Equal("模糊", (string?)json["parameters"]!["negative_prompt"]);
        Assert.Single(result.Images);
    }

    [Fact]
    public async Task Qwen_text_uses_sse_header_and_incremental_output()
    {
        var (provider, handler) = Create(Vendor.Qwen, (_, _) => Sse(
            """data: {"output":{"choices":[{"message":{"role":"assistant","content":[{"text":"你"}]}}]}}""",
            """data: {"output":{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":[{"text":"好"}]}}]}}"""));
        var text = await provider.GenerateTextAsync(new TextRequest("qwen3.8-max", "打招呼", []), null, default);
        Assert.Equal("你好", text);
        var (request, body) = handler.Calls.Single();
        Assert.EndsWith("/services/aigc/multimodal-generation/generation", request.RequestUri!.ToString());
        Assert.Equal("enable", request.Headers.GetValues("X-DashScope-SSE").Single());
        Assert.Equal(true, (bool?)JsonNode.Parse(body)!["parameters"]!["incremental_output"]);
    }

    [Fact]
    public async Task Ark_pro_appends_point_and_box_tags_for_interactive_edits()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        Core.Canvas.ImageSelection[] marks = [new(Core.Canvas.ImageSelection.Point, 0.25, 0.5), new(Core.Canvas.ImageSelection.Box, 0.1, 0.2, 0.9995, 0.8)];
        await provider.GenerateImageAsync(Image("doubao-seedream-5-0-pro-260628", refs: [Reference]) with { Selections = marks }, null, default);
        var prompt = (string?)JsonNode.Parse(handler.Calls.Single().Body)!["prompt"];
        Assert.Equal("一只猫\n<point>250 500</point> <bbox>100 200 999 800</bbox>", prompt);
    }

    [Fact]
    public async Task Ark_layer_decomposition_returns_base_and_positioned_layers()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json($$$"""
            {"data":[{"b64_json":"{{{Png}}}","output_format":"jpeg"},
                     {"b64_json":"{{{Png}}}","z_index":1,"name":"人物","description":"穿灰色上衣","bounding_box":{"absolute":[10,20,30,40],"normalized":[220,432,777,1000]}}]}
            """));
        var result = await provider.GenerateImageAsync(Image("doubao-seedream-5-0-flash-260901", refs: [Reference], options: new() { LayerDecomposition = true, Tier = "1.5K", Ratio = "16:9" }), null, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal(true, (bool?)json["layer_decomposition"]);
        Assert.Equal("1.5K", (string?)json["size"]);
        Assert.Null(result.Images[0].Layer);
        var layer = result.Images[1].Layer!;
        Assert.Equal((1, "人物"), (layer.ZIndex, layer.Name));
        Assert.Equal([220d, 432, 777, 1000], layer.Box!);
        Assert.Equal("image/png", result.Images[1].MimeType);
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("doubao-seedream-4-5-251128", refs: [Reference], options: new() { LayerDecomposition = true }), null, default));
    }

    [Fact]
    public async Task Ark_transparent_background_forces_png()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json($$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        await provider.GenerateImageAsync(Image("doubao-seedream-5-0-pro-260628", refs: [Reference], options: new() { Transparent = true, OutputFormat = "jpeg" }), null, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal(("transparent", "png"), ((string?)json["background"], (string?)json["output_format"]));
    }

    [Fact]
    public async Task Google_continues_a_stored_interaction()
    {
        var (provider, handler) = Create(Vendor.Google, (_, _) => Json($$"""{"id":"int-2","status":"completed","steps":[{"type":"model_output","content":[{"type":"image","mime_type":"image/jpeg","data":"{{Png}}"}]}]}"""));
        var result = await provider.GenerateImageAsync(Image("gemini-3-pro-image-preview", options: new() { Store = true }) with { PreviousId = "int-1" }, null, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal((true, "int-1"), ((bool?)json["store"], (string?)json["previous_interaction_id"]));
        Assert.Equal("int-2", result.ConversationId);
    }

    [Fact]
    public async Task OpenAI_responses_continue_a_stored_response()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"id":"resp-2","status":"completed","output":[{"type":"image_generation_call","result":"{{Png}}"},{"type":"message","content":[{"type":"output_text","text":"已调亮"}]}]}"""));
        var mask = new MediaInput([7], "image/png");
        var progress = new Capture<ImageEvent>();
        var result = await provider.GenerateImageAsync(new ImageRequest("gpt-image-2.5-sunburst", "调亮背景", [Reference], mask, 1, new() { OpenAIApi = "responses", MainModel = "gpt-6-luna", MainReasoning = "none", Store = true }, PreviousId: "resp-1"), progress, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal((true, "resp-1", true), ((bool?)json["store"], (string?)json["previous_response_id"], (bool?)json["background"]));
        Assert.Equal(("gpt-6-luna", "none"), ((string?)json["model"], (string?)json["reasoning"]!["effort"]));
        Assert.StartsWith("data:image/png;base64,", (string?)json["tools"]![0]!["input_image_mask"]!["image_url"]);
        Assert.Equal(("resp-2", "已调亮"), (result.ConversationId, result.Texts.Single()));
        Assert.Equal("resp-2", progress.Items.Single().TaskId);
    }

    [Fact]
    public async Task OpenAI_resumes_a_background_response_by_polling()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json($$"""{"id":"resp-3","status":"completed","output":[{"type":"image_generation_call","output_format":"jpeg","result":"{{Png}}"}]}"""));
        var result = await provider.ResumeImageTaskAsync("resp-3", store: false, default);
        var (request, _) = handler.Calls.Single();
        Assert.Equal((HttpMethod.Get, "https://api.openai.com/v1/responses/resp-3"), (request.Method, request.RequestUri!.ToString()));
        Assert.Equal("image/jpeg", result.Images.Single().MimeType);
        Assert.Null(result.ConversationId);
    }

    [Fact]
    public async Task OpenAI_background_failures_and_cancel()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (request, _) => request.Method == HttpMethod.Get
            ? Json("""{"id":"resp-4","status":"failed","error":{"code":"server_error","message":"生成失败了"}}""")
            : Json("""{"id":"resp-4","status":"cancelled"}"""));
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.ResumeImageTaskAsync("resp-4", false, default));
        Assert.Equal("生成失败了", error.Message);
        await provider.CancelTaskAsync("resp-4");
        Assert.Equal((HttpMethod.Post, "https://api.openai.com/v1/responses/resp-4/cancel"), (handler.Calls[1].Request.Method, handler.Calls[1].Request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task OpenAI_explains_a_completed_response_without_an_image()
    {
        var (provider, _) = Create(Vendor.OpenAI, (_, _) => Json("""{"id":"resp-5","status":"completed","output":[{"type":"image_generation_call","status":"failed","result":null},{"type":"message","content":[{"type":"output_text","text":"无法生成"}]}]}"""));
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.ResumeImageTaskAsync("resp-5", false, default));
        Assert.Equal("Responses 未返回图片（resp-5）\n图像工具状态：failed\n主模型回复：无法生成", error.Message);
    }

    private sealed class Capture<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }

    [Fact]
    public async Task Qwen_sends_rewrite_mode_thinking_and_seed()
    {
        var (provider, handler) = Create(Vendor.Qwen, (request, _) => request.Method == HttpMethod.Post
            ? Json("""{"output":{"choices":[{"message":{"content":[{"image":"https://oss.example/a.png"}]}}]}}""")
            : Bytes("image/png"));
        await provider.GenerateImageAsync(Image("qwen-image-3.0-pro", options: new() { PromptExtendMode = "agent", EnableThinking = false, Seed = 42 }), null, default);
        var parameters = JsonNode.Parse(handler.Calls[0].Body)!["parameters"]!;
        Assert.Equal(("agent", false, 42L), ((string?)parameters["prompt_extend_mode"], (bool?)parameters["enable_thinking"], (long?)parameters["seed"]));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("qwen-image-3.0-pro", refs: [Reference], options: new() { PromptExtendMode = "agent" }), null, default));
    }

    [Fact]
    public async Task Errors_surface_vendor_messages()
    {
        var (provider, _) = Create(Vendor.Qwen, (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":"InvalidParameter","message":"size 不合法","request_id":"r9"}""", Encoding.UTF8, "application/json"),
        });
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("qwen-image-3.0"), null, default));
        Assert.Equal("size 不合法", error.Message);
        Assert.Equal("r9", error.RequestId);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task Html_page_with_200_points_at_the_base_url()
    {
        var (provider, _) = Create(Vendor.OpenAI, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") });
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.ListModelsAsync(default));
        Assert.Contains("接口地址", error.Message);
    }

    [Fact]
    public async Task OpenAI_balance_sums_this_month_costs_with_the_admin_key()
    {
        var (provider, handler) = Create(Vendor.OpenAI, (_, _) => Json("""{"object":"page","data":[{"results":[{"amount":{"value":1.25,"currency":"usd"}}]},{"results":[{"amount":{"value":0.5,"currency":"usd"}}]}],"has_more":false}"""),
            configure: c => c.BillingKey = "admin-key");
        var balance = await provider.BalanceAsync(default);
        var request = handler.Calls.Single().Request;
        Assert.StartsWith("https://api.openai.com/v1/organization/costs?start_time=", request.RequestUri!.ToString());
        Assert.Contains("bucket_width=1d", request.RequestUri.Query);
        Assert.Equal("Bearer admin-key", request.Headers.Authorization!.ToString());
        Assert.Equal(("本月花费", 1.75m, "USD"), (balance.Label, balance.Amount, balance.Currency));
    }

    [Fact]
    public async Task Xai_balance_finds_the_team_from_the_management_key()
    {
        var (provider, handler) = Create(Vendor.XAI, (request, _) => request.RequestUri!.AbsolutePath.StartsWith("/auth")
            ? Json("""{"teamId":"old","scope":"SCOPE_TEAM","scopeId":"team-1"}""")
            : Json("""{"changes":[],"total":{"val":"-1250"}}"""), configure: c => c.BillingKey = "mgmt-key");
        var balance = await provider.BalanceAsync(default);
        Assert.Equal("https://management-api.x.ai/auth/management-keys/validation", handler.Calls[0].Request.RequestUri!.ToString());
        Assert.Equal("https://management-api.x.ai/v1/billing/teams/team-1/prepaid/balance", handler.Calls[1].Request.RequestUri!.ToString());
        Assert.Equal("Bearer mgmt-key", handler.Calls[1].Request.Headers.Authorization!.ToString());
        Assert.Equal(("预付余额", 12.5m, "USD"), (balance.Label, balance.Amount, balance.Currency));
    }

    [Fact]
    public async Task Ark_balance_signs_a_volcengine_billing_request()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json("""{"ResponseMetadata":{"Action":"QueryBalanceAcct"},"Result":{"AvailableBalance":"86.68","CashBalance":"86.68","Currency":"CNY"}}"""),
            configure: c => (c.BillingId, c.BillingKey) = ("AKLTtest", "secret"));
        var balance = await provider.BalanceAsync(default);
        var (request, body) = handler.Calls.Single();
        Assert.Equal((HttpMethod.Post, "https://open.volcengineapi.com/?Action=QueryBalanceAcct&Version=2022-01-01", "{}"), (request.Method, request.RequestUri!.ToString(), body));
        var date = request.Headers.GetValues("X-Date").Single();
        Assert.Matches(@"^\d{8}T\d{6}Z$", date);
        Assert.Matches($@"^HMAC-SHA256 Credential=AKLTtest/{date[..8]}/cn-beijing/billing/request, SignedHeaders=content-type;host;x-content-sha256;x-date, Signature=[0-9a-f]{{64}}$", request.Headers.GetValues("Authorization").Single());
        Assert.Equal(("可用余额", 86.68m, "CNY"), (balance.Label, balance.Amount, balance.Currency));
    }

    [Fact]
    public async Task Volcengine_errors_read_the_response_metadata()
    {
        var (provider, _) = Create(Vendor.Ark, (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"ResponseMetadata":{"RequestId":"r1","Error":{"Code":"SignatureDoesNotMatch","Message":"签名不匹配"}}}""", Encoding.UTF8, "application/json"),
        }, configure: c => (c.BillingId, c.BillingKey) = ("AK", "SK"));
        Assert.Equal("签名不匹配", (await Assert.ThrowsAsync<ApiException>(() => provider.BalanceAsync(default))).Message);
    }

    [Fact]
    public async Task Qwen_balance_signs_an_acs3_bss_request()
    {
        var (provider, handler) = Create(Vendor.Qwen, (_, _) => Json("""{"Code":"200","Success":true,"Data":{"AvailableAmount":"1,234.50","AvailableCashAmount":"1,234.50","Currency":"CNY"}}"""),
            configure: c => (c.BillingId, c.BillingKey) = ("LTAItest", "secret"));
        var balance = await provider.BalanceAsync(default);
        var request = handler.Calls.Single().Request;
        Assert.Equal((HttpMethod.Get, "https://business.aliyuncs.com/"), (request.Method, request.RequestUri!.ToString()));
        Assert.Equal(("QueryAccountBalance", "2017-12-14"), (request.Headers.GetValues("x-acs-action").Single(), request.Headers.GetValues("x-acs-version").Single()));
        Assert.Matches(@"^ACS3-HMAC-SHA256 Credential=LTAItest,SignedHeaders=host;x-acs-action;x-acs-content-sha256;x-acs-date;x-acs-signature-nonce;x-acs-version,Signature=[0-9a-f]{64}$", request.Headers.GetValues("Authorization").Single());
        Assert.Equal(("可用额度", 1234.50m, "CNY"), (balance.Label, balance.Amount, balance.Currency));

        var (intl, intlHandler) = Create(Vendor.Qwen, (_, _) => Json("""{"Success":false,"Code":"NotAuthorized","Message":"没有权限"}"""), "https://dashscope-intl.aliyuncs.com", c => (c.BillingId, c.BillingKey) = ("LTAI", "s"));
        Assert.Equal("没有权限", (await Assert.ThrowsAsync<ApiException>(() => intl.BalanceAsync(default))).Message);
        Assert.Equal("business.ap-southeast-1.aliyuncs.com", intlHandler.Calls.Single().Request.RequestUri!.Host);
    }

    [Fact]
    public async Task Google_has_no_balance_api()
    {
        var (provider, handler) = Create(Vendor.Google, (_, _) => Json("{}"), configure: c => c.BillingKey = "x");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.BalanceAsync(default));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task OpenAI_images_and_responses_report_tokens()
    {
        var (images, _) = Create(Vendor.OpenAI, (_, _) => Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}],"usage":{"input_tokens":50,"output_tokens":1470,"total_tokens":1520}}"""));
        await images.GenerateImageAsync(Image("gpt-image-2"), null, default);
        Assert.Equal(new TokenUsage(50, 1470), images.Tokens);

        var (stream, _) = Create(Vendor.OpenAI, (_, _) => Sse(
            $$$"""data: {"type":"image_generation.completed","b64_json":"{{{Png}}}","usage":{"input_tokens":10,"output_tokens":200}}"""));
        await stream.GenerateImageAsync(Image("gpt-image-2", options: new() { Stream = true }), null, default);
        Assert.Equal(new TokenUsage(10, 200), stream.Tokens);

        var (text, _) = Create(Vendor.OpenAI, (_, _) => Sse(
            """data: {"type":"response.output_text.delta","delta":"好"}""",
            """data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":12,"output_tokens":30,"output_tokens_details":{"reasoning_tokens":20}}}}"""));
        await text.GenerateTextAsync(new TextRequest("gpt-5.5", "你好", []), null, default);
        Assert.Equal(new TokenUsage(12, 30), text.Tokens);
    }

    [Fact]
    public async Task Google_splits_image_and_thought_tokens()
    {
        var (provider, _) = Create(Vendor.Google, (_, _) => Json($$$"""{"id":"i1","status":"completed","usage":{"total_input_tokens":300,"total_output_tokens":1140,"total_thought_tokens":80,"output_tokens_by_modality":[{"modality":"image","tokens":1120},{"modality":"text","tokens":20}]},"steps":[{"type":"model_output","content":[{"type":"image","mime_type":"image/jpeg","data":"{{{Png}}}"}]}]}"""));
        await provider.GenerateImageAsync(Image("gemini-3-pro-image-preview"), null, default);
        Assert.Equal(new TokenUsage(300, 100, 1120), provider.Tokens);

        var (text, _) = Create(Vendor.Google, (_, _) => Sse(
            """data: {"event_type":"step.delta","delta":{"type":"text","text":"好"}}""",
            """data: {"event_type":"interaction.completed","interaction":{"status":"completed","usage":{"total_input_tokens":7,"total_output_tokens":20,"total_thought_tokens":22}}}"""));
        await text.GenerateTextAsync(new TextRequest("gemini-3.8-flash", "你好", []), null, default);
        Assert.Equal(new TokenUsage(7, 42), text.Tokens);
    }

    [Fact]
    public async Task Ark_chat_asks_for_usage_and_reads_the_last_chunk()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Sse(
            """data: {"choices":[{"delta":{"content":"你好"}}],"usage":null}""",
            """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":null}""",
            """data: {"choices":[],"usage":{"prompt_tokens":15,"completion_tokens":9,"total_tokens":24}}""",
            "data: [DONE]"));
        var text = await provider.GenerateTextAsync(new TextRequest("doubao-seed-2-0-pro", "打招呼", []), null, default);
        Assert.Equal("你好", text);
        Assert.True((bool?)JsonNode.Parse(handler.Calls.Single().Body)!["stream_options"]!["include_usage"]);
        Assert.Equal(new TokenUsage(15, 9), provider.Tokens);

        var (images, _) = Create(Vendor.Ark, (_, _) => Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}],"usage":{"generated_images":1,"output_tokens":16384,"total_tokens":16384}}"""));
        await images.GenerateImageAsync(Image("doubao-seedream-5-0-260128"), null, default);
        Assert.Equal(new TokenUsage(0, 16384), images.Tokens);
    }

    [Fact]
    public async Task Qwen_text_keeps_the_last_cumulative_usage()
    {
        var (provider, _) = Create(Vendor.Qwen, (_, _) => Sse(
            """data: {"output":{"choices":[{"message":{"content":"我爱"}}]},"usage":{"input_tokens":26,"output_tokens":1}}""",
            """data: {"output":{"choices":[{"finish_reason":"stop","message":{"content":"苹果"}}]},"usage":{"input_tokens":26,"output_tokens":4}}"""));
        await provider.GenerateTextAsync(new TextRequest("qwen3.5-plus", "你好", []), null, default);
        Assert.Equal(new TokenUsage(26, 4), provider.Tokens);
    }

    private static string NativeTextDelta(Vendor vendor) => vendor switch
    {
        Vendor.Google => """data: {"event_type":"step.delta","delta":{"type":"text","text":"未完成"}}""",
        Vendor.Ark => """data: {"choices":[{"delta":{"content":"未完成"}}]}""",
        _ => """data: {"output":{"choices":[{"message":{"content":"未完成"},"finish_reason":null}]}}""",
    };

    [Theory]
    [InlineData(Vendor.Google, false)]
    [InlineData(Vendor.Google, true)]
    [InlineData(Vendor.Ark, false)]
    [InlineData(Vendor.Ark, true)]
    [InlineData(Vendor.Qwen, false)]
    [InlineData(Vendor.Qwen, true)]
    public async Task Native_text_needs_a_success_terminal_event_before_done_or_eof(Vendor vendor, bool done)
    {
        var events = new List<string> { NativeTextDelta(vendor) };
        if (done) events.Add("data: [DONE]");
        var (provider, _) = Create(vendor, (_, _) => Sse(events.ToArray()));
        var deltas = new List<string>();
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), deltas.Add, default));
        Assert.Equal(["未完成"], deltas);
    }

    [Theory]
    [InlineData(Vendor.Google)]
    [InlineData(Vendor.Ark)]
    [InlineData(Vendor.Qwen)]
    public async Task Native_empty_text_stream_is_not_success(Vendor vendor)
    {
        var (provider, _) = Create(vendor, (_, _) => Sse());
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), null, default));
    }

    [Theory]
    [InlineData(Vendor.Google, "incomplete")]
    [InlineData(Vendor.Ark, "length")]
    [InlineData(Vendor.Ark, "content_filter")]
    [InlineData(Vendor.Qwen, "length")]
    [InlineData(Vendor.Qwen, "tool_calls")]
    public async Task Native_text_truncation_keeps_partial_text_and_reports_failure(Vendor vendor, string reason)
    {
        var end = vendor switch
        {
            Vendor.Google => $$$"""data: {"event_type":"interaction.completed","interaction":{"status":"{{{reason}}}"}}""",
            Vendor.Ark => $$"""data: {"choices":[{"delta":{},"finish_reason":"{{reason}}"}]}""",
            _ => $$$"""data: {"output":{"choices":[{"message":{"content":""},"finish_reason":"{{{reason}}}"}]}}""",
        };
        var (provider, _) = Create(vendor, (_, _) => Sse(NativeTextDelta(vendor), end, "data: [DONE]"));
        var deltas = new List<string>();
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), deltas.Add, default));
        Assert.Equal(["未完成"], deltas);
    }

    [Fact]
    public async Task Google_completed_returns_without_requiring_eof()
    {
        var (provider, _) = Create(Vendor.Google, (_, _) => Sse(
            NativeTextDelta(Vendor.Google),
            """data: {"event_type":"interaction.completed","interaction":{"status":"completed"}}""",
            """data: {"event_type":"error","message":"完成后的数据"}"""));
        Assert.Equal("未完成", await provider.GenerateTextAsync(new TextRequest("text-model", "继续", []), null, default));
    }

    private static HttpResponseMessage MiniMaxOk(string data, string usage = "", string format = "png") =>
        Json($$"""{"created":1,"data":[{{data}}],"output_format":"{{format}}",{{usage}}"request_id":"req-1"}""");

    private static string MiniMaxItem(string name) => $$"""{"url":"https://cdn.example/{{name}}.png","size":"2720x1536","revised_prompt":"一只猫"}""";

    [Fact]
    public async Task MiniMax_text_to_image_sends_area_ratio_and_format_then_downloads_each_url()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post ? MiniMaxOk($"{MiniMaxItem("a")},{MiniMaxItem("b")}") : Bytes("application/octet-stream"));
        var events = new List<ImageEvent>();
        var result = await provider.GenerateImageAsync(Image("MiniMax-H3-Image", count: 2, options: new() { Tier = "2K", Ratio = "16:9" }), new SyncProgress<ImageEvent>(events.Add), default);
        var (request, body) = handler.Calls[0];
        Assert.Equal("https://api.minimax.cn/v1/images/generations", request.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal(("MiniMax-H3-Image", "一只猫", 2, "2K", "16:9", "png", "url"), ((string?)json["model"]!, (string?)json["prompt"]!, (int)json["n"]!, (string?)json["area"]!, (string?)json["aspect_ratio"]!, (string?)json["output_format"]!, (string?)json["response_format"]!));
        // Enhancement is MiniMax's default, so nothing is sent for it; text-to-image carries no pictures and no size.
        Assert.Null(json["disable_prompt_enhanced"]);
        Assert.Null(json["images"]);
        Assert.Null(json["size"]);
        Assert.Equal(["https://cdn.example/a.png", "https://cdn.example/b.png"], handler.Calls.Skip(1).Select(c => c.Request.RequestUri!.ToString()));
        Assert.Equal(["image/png", "image/png"], result.Images.Select(i => i.MimeType));
        Assert.Equal([0, 1], events.Select(e => e.Index));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MiniMax_uses_the_international_host_and_the_chosen_output_format()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post ? MiniMaxOk(MiniMaxItem("a"), format: "webp") : Bytes("image/webp"), "https://api.minimax.io",
            channel => Assert.True(channel.IsInternationalSite));
        var result = await provider.GenerateImageAsync(Image("MiniMax-H3-Image-Highspeed", options: new() { Tier = "4K", Ratio = "3:1", OutputFormat = "webp" }), null, default);
        Assert.Equal("https://api.minimax.io/v1/images/generations", handler.Calls[0].Request.RequestUri!.ToString());
        var json = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.Equal(("MiniMax-H3-Image-Highspeed", "4K", "3:1", "webp"), ((string?)json["model"]!, (string?)json["area"]!, (string?)json["aspect_ratio"]!, (string?)json["output_format"]!));
        Assert.Equal("image/webp", result.Images.Single().MimeType);
    }

    [Fact]
    public async Task MiniMax_auto_tier_sends_neither_area_nor_ratio_and_an_auto_ratio_sends_only_the_area()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post ? MiniMaxOk(MiniMaxItem("a")) : Bytes("image/png"));
        await provider.GenerateImageAsync(Image("MiniMax-H3-Image", options: new() { Tier = "auto", Ratio = "16:9" }), null, default);
        await provider.GenerateImageAsync(Image("MiniMax-H3-Image", options: new() { Tier = "1K", Ratio = "auto" }), null, default);
        var posts = handler.Calls.Where(c => c.Request.Method == HttpMethod.Post).Select(c => JsonNode.Parse(c.Body)!).ToList();
        Assert.Equal((null, null), ((string?)posts[0]["area"], (string?)posts[0]["aspect_ratio"]));
        Assert.Equal(("1K", null), ((string?)posts[1]["area"], (string?)posts[1]["aspect_ratio"]));
    }

    [Fact]
    public async Task MiniMax_turning_enhancement_off_needs_a_fixed_ratio_and_is_refused_before_any_request()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post ? MiniMaxOk(MiniMaxItem("a")) : Bytes("image/png"));
        foreach (var options in new ImageOptions[] { new() { Tier = "2K", Ratio = "auto", PromptExtend = false }, new() { Tier = "auto", Ratio = "1:1", PromptExtend = false } })
            Assert.Contains("提示词增强", (await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("MiniMax-H3-Image", options: options), null, default))).Message);
        Assert.Empty(handler.Calls);

        await provider.GenerateImageAsync(Image("MiniMax-H3-Image", options: new() { Tier = "2K", Ratio = "1:1", PromptExtend = false }), null, default);
        var json = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.Equal((true, "1:1"), ((bool)json["disable_prompt_enhanced"]!, (string?)json["aspect_ratio"]));
    }

    [Fact]
    public async Task MiniMax_edit_sends_references_as_data_uris_in_order_and_never_the_enhancement_switch()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post ? MiniMaxOk(MiniMaxItem("a")) : Bytes("image/png"));
        // The edits endpoint always enhances the prompt and has no switch, so an off setting with an auto ratio is neither sent nor refused.
        await provider.GenerateImageAsync(Image("MiniMax-H3-Image", refs: [Reference, new([4, 5], "image/jpeg")], options: new() { Tier = "2K", Ratio = "auto", PromptExtend = false }), null, default);
        var (request, body) = handler.Calls[0];
        Assert.Equal("https://api.minimax.cn/v1/images/edits", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal(["data:image/png;base64,AQID", "data:image/jpeg;base64,BAU="], json["images"]!.AsArray().Select(i => (string?)i!["image_url"]));
        Assert.Null(json["disable_prompt_enhanced"]);
        Assert.Equal("2K", (string?)json["area"]);
        Assert.Null(json["aspect_ratio"]);
    }

    [Fact]
    public async Task MiniMax_partial_failure_fills_the_pictures_that_succeeded_and_keeps_the_failure_messages()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post
            ? MiniMaxOk(MiniMaxItem("a") + """,{"error":{"code":1027,"message":"生成的图片未通过内容安全检查 (1027)"}},""" + MiniMaxItem("c"),
                """
                "usage":{"generated_images":2,"total_tokens":8120,"input_tokens":120,"output_tokens":8000,"input_tokens_details":{"text_tokens":120,"image_tokens":0},"output_tokens_details":{"image_tokens":8000}},
                """)
            : Bytes("image/png"));
        var events = new List<ImageEvent>();
        var result = await provider.GenerateImageAsync(Image("MiniMax-H3-Image", count: 3, options: new() { Tier = "1K", Ratio = "1:1" }), new SyncProgress<ImageEvent>(events.Add), default);
        Assert.Equal(2, result.Images.Count);
        Assert.Equal(["生成的图片未通过内容安全检查 (1027)"], result.Errors);
        // The generator fills the first nodes with the pictures and fails the rest with these messages.
        Assert.Equal([0, 1], events.Select(e => e.Index));
        Assert.Equal(["https://cdn.example/a.png", "https://cdn.example/c.png"], handler.Calls.Skip(1).Select(c => c.Request.RequestUri!.ToString()));
        // Only the pictures that were delivered are billed, and the image output tokens are kept apart.
        Assert.Equal(new TokenUsage(120, 0, 8000), provider.Tokens);
    }

    [Fact]
    public async Task MiniMax_a_picture_that_cannot_be_downloaded_fails_only_its_own_node()
    {
        var (provider, _) = Create(Vendor.MiniMax, (request, _) => request.Method == HttpMethod.Post ? MiniMaxOk($"{MiniMaxItem("a")},{MiniMaxItem("b")}")
            : request.RequestUri!.AbsolutePath.EndsWith("a.png") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Bytes("image/png"));
        var result = await provider.GenerateImageAsync(Image("MiniMax-H3-Image", count: 2, options: new() { Tier = "1K", Ratio = "1:1" }), null, default);
        Assert.Single(result.Images);
        Assert.Contains("下载失败", result.Errors.Single());
    }

    [Fact]
    public async Task MiniMax_error_body_gives_its_message_status_and_request_id()
    {
        var (provider, _) = Create(Vendor.MiniMax, (_, _) => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"insufficient_balance_error","message":"insufficient balance (1008)","http_code":"402"},"request_id":"req-9"}""", Encoding.UTF8, "application/json"),
        });
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("MiniMax-H3-Image", options: new() { Tier = "1K", Ratio = "1:1" }), null, default));
        Assert.Equal(("insufficient balance (1008)", 402, "req-9"), (error.Message, error.Status, error.RequestId));
        Assert.Equal(default(TokenUsage), provider.Tokens);
    }

    [Fact]
    public async Task MiniMax_refuses_what_its_endpoints_cannot_do()
    {
        var (provider, handler) = Create(Vendor.MiniMax, (_, _) => MiniMaxOk(MiniMaxItem("a")));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("MiniMax-H3-Image", count: 11), null, default));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(Image("MiniMax-H3-Image-Context-IR"), null, default));
        await Assert.ThrowsAsync<ApiException>(() => provider.GenerateImageAsync(new("MiniMax-H3-Image", "改", [Reference], Reference, 1, new()), null, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.BalanceAsync(default));
        Assert.Empty(handler.Calls);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
