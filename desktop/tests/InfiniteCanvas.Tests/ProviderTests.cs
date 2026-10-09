using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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

    private static (AiProvider Provider, FakeHandler Handler) Create(Vendor vendor, Func<HttpRequestMessage, string, HttpResponseMessage> respond, string baseUrl = "")
    {
        var handler = new FakeHandler(respond);
        var channel = new ModelChannel { Vendor = vendor, BaseUrl = baseUrl, ApiKey = "test-key" };
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
    public async Task Google_veo_polls_operation_and_downloads_with_key()
    {
        var (provider, handler) = Create(Vendor.Google, (request, _) => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith(":predictLongRunning") => Json("""{"name":"models/veo-3.1-generate-preview/operations/op1"}"""),
            var path when path.Contains("/operations/") => Json("""{"done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://files.example/v.mp4"}}]}}}"""),
            _ => Bytes("video/mp4"),
        });
        var id = await provider.CreateVideoTaskAsync(new VideoRequest("veo-3.1-generate-preview", "海浪", [Reference], VideoMode.Frames, 6, "1080p", "16:9", true, false), default);
        var body = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.Equal(8, (int?)body["parameters"]!["durationSeconds"]);
        Assert.NotNull(body["instances"]![0]!["image"]!["bytesBase64Encoded"]);
        var state = await provider.PollVideoTaskAsync(id, default);
        Assert.Equal(VideoTaskStatus.Completed, state.Status);
        Assert.Equal("test-key", handler.Calls.Last().Request.Headers.GetValues("x-goog-api-key").Single());
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
    public async Task Ark_seedance_frames_on_2_5_force_adaptive_ratio()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json("""{"id":"cgt-1"}"""));
        await provider.CreateVideoTaskAsync(new VideoRequest("doubao-seedance-2-5-260628", "飞鸟", [Reference], VideoMode.Frames, 10, "1080p", "16:9", true, false, "ep-123"), default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal("ep-123", (string?)json["model"]);
        Assert.Equal("adaptive", (string?)json["ratio"]);
        Assert.Equal("first_frame", (string?)json["content"]![1]!["role"]);
        Assert.Equal(true, (bool?)json["generate_audio"]);
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
    public async Task Xai_video_frames_send_first_and_last_frame()
    {
        var (provider, handler) = Create(Vendor.XAI, (_, _) => Json("""{"request_id":"r-1"}"""));
        await provider.CreateVideoTaskAsync(new VideoRequest("grok-imagine-video-1.5", "开门", [Reference, Reference], VideoMode.Frames, 8, "720p", "16:9", false, false), default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.NotNull(json["image"]!["url"]);
        Assert.NotNull(json["last_frame"]!["url"]);
        Assert.Null(json["reference_images"]);
    }

    [Fact]
    public async Task Xai_reference_video_is_capped_at_720p()
    {
        var (provider, handler) = Create(Vendor.XAI, (_, _) => Json("""{"request_id":"r-2"}"""));
        await provider.CreateVideoTaskAsync(new VideoRequest("grok-imagine-video-1.5", "跳舞", [Reference], VideoMode.Reference, 8, "1080p", "16:9", false, false), default);
        Assert.Equal("720p", (string?)JsonNode.Parse(handler.Calls.Single().Body)!["resolution"]);
    }

    private static readonly MediaInput Clip = new([7, 7], "video/mp4");
    private static readonly MediaInput Voice = new([5, 5], "audio/wav");

    [Fact]
    public async Task Xai_video_keyframes_and_voices_on_1_5()
    {
        var (provider, handler) = Create(Vendor.XAI, (_, _) => Json("""{"request_id":"r-3"}"""));
        var request = new VideoRequest("grok-imagine-video-1.5", "<AUDIO_0> 说你好", [Reference, Reference], VideoMode.Frames, 6, "1080p", "16:9", false, false)
        {
            Voices = ["eve", "leo", "ara", "rex"],
            Keyframes = [(Reference, 2.1), (Reference, 5.9)],
        };
        await provider.CreateVideoTaskAsync(request, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal(3, json["reference_audios"]!.AsArray().Count);
        Assert.Equal("eve", (string?)json["reference_audios"]![0]!["voice_id"]);
        // Snapped to the 1/3 s grid and kept strictly inside the 6 s clip.
        Assert.Equal(2.0, (double)json["keyframes"]![0]!["timestamp_s"]!, 3);
        Assert.Equal(17 / 3.0, (double)json["keyframes"]![1]!["timestamp_s"]!, 3);
        Assert.NotNull(json["last_frame"]);
        Assert.Equal("720p", (string?)json["resolution"]);
    }

    [Fact]
    public async Task Xai_video_edit_and_extend_use_their_endpoints()
    {
        var (provider, handler) = Create(Vendor.XAI, (_, _) => Json("""{"request_id":"r-4"}"""));
        await provider.CreateVideoTaskAsync(new VideoRequest("grok-imagine-video-1.5", "换成雪天", [], VideoMode.Frames, 8, "720p", "16:9", false, false) { Videos = [Clip], Task = "edit" }, default);
        await provider.CreateVideoTaskAsync(new VideoRequest("grok-imagine-video-1.5", "继续走", [], VideoMode.Frames, 15, "720p", "16:9", false, false) { Videos = [Clip], Task = "extend" }, default);
        Assert.Equal("https://api.x.ai/v1/videos/edits", handler.Calls[0].Request.RequestUri!.ToString());
        var edit = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.StartsWith("data:video/mp4;base64,", (string?)edit["video"]!["url"]);
        Assert.Null(edit["duration"]);
        Assert.Equal("https://api.x.ai/v1/videos/extensions", handler.Calls[1].Request.RequestUri!.ToString());
        Assert.Equal(10, (int?)JsonNode.Parse(handler.Calls[1].Body)!["duration"]);
    }

    [Fact]
    public async Task Ark_seedance_reference_media_draft_and_last_frame()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json("""{"id":"cgt-2"}"""));
        var request = new VideoRequest("doubao-seedance-2-5-260628", "跟随视频1的运镜", [Reference], VideoMode.Frames, -1, "1080p", "16:9", true, false)
        {
            Videos = [Clip with { Url = "https://ark.example/v.mp4" }],
            Audios = [Voice],
            Draft = true,
            ReturnLastFrame = true,
        };
        await provider.CreateVideoTaskAsync(request, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        var content = json["content"]!.AsArray();
        Assert.Equal("reference_image", (string?)content[1]!["role"]);
        Assert.Equal("https://ark.example/v.mp4", (string?)content[2]!["video_url"]!["url"]);
        Assert.Equal("reference_video", (string?)content[2]!["role"]);
        Assert.StartsWith("data:audio/wav;base64,", (string?)content[3]!["audio_url"]!["url"]);
        Assert.Equal("reference_audio", (string?)content[3]!["role"]);
        Assert.Equal(-1, (int?)json["duration"]);
        Assert.Equal("480p", (string?)json["resolution"]);
        Assert.Equal(true, (bool?)json["draft"]);
        Assert.Equal(true, (bool?)json["return_last_frame"]);
        Assert.Null(json["omni_reference_task_type"]);
    }

    [Fact]
    public async Task Ark_seedance_rejects_local_reference_video_and_edits_by_url()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json("""{"id":"cgt-3"}"""));
        await Assert.ThrowsAsync<ApiException>(() => provider.CreateVideoTaskAsync(new VideoRequest("doubao-seedance-2-5-260628", "改成夜景", [], VideoMode.Frames, 8, "720p", "16:9", false, false) { Videos = [Clip], Task = "edit" }, default));
        Assert.Empty(handler.Calls);
        await provider.CreateVideoTaskAsync(new VideoRequest("doubao-seedance-2-5-260628", "改成夜景", [], VideoMode.Frames, 8, "720p", "16:9", false, false) { Videos = [Clip with { Url = "https://ark.example/v.mp4" }], Task = "edit" }, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal("edit", (string?)json["omni_reference_task_type"]);
        Assert.Equal("adaptive", (string?)json["ratio"]);
        Assert.Equal(-1, (int?)json["duration"]);
    }

    [Fact]
    public async Task Ark_seedance_draft_final_sends_only_allowed_fields()
    {
        var (provider, handler) = Create(Vendor.Ark, (_, _) => Json("""{"id":"cgt-4"}"""));
        await provider.CreateVideoTaskAsync(new VideoRequest("doubao-seedance-2-5-260628", "", [], VideoMode.Frames, 0, "1080p", "adaptive", false, true) { DraftTaskId = "cgt-draft", ReturnLastFrame = true }, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!.AsObject();
        Assert.Equal(["model", "content", "resolution", "watermark", "return_last_frame"], json.Select(p => p.Key));
        Assert.Equal("draft_task", (string?)json["content"]![0]!["type"]);
        Assert.Equal("cgt-draft", (string?)json["content"]![0]!["draft_task"]!["id"]);
    }

    [Fact]
    public async Task Ark_seedance_poll_returns_url_and_last_frame()
    {
        var (provider, handler) = Create(Vendor.Ark, (request, _) => request.RequestUri!.Host == "ark.cn-beijing.volces.com"
            ? Json("""{"id":"cgt-5","status":"succeeded","content":{"video_url":"https://tos.example/v.mp4","last_frame_url":"https://tos.example/f.jpeg"}}""")
            : Bytes(request.RequestUri.AbsolutePath.EndsWith(".mp4") ? "video/mp4" : "image/jpeg"));
        var state = await provider.PollVideoTaskAsync("cgt-5", default);
        Assert.Equal(VideoTaskStatus.Completed, state.Status);
        Assert.Equal("https://tos.example/v.mp4", state.RemoteUrl);
        Assert.True(state.RemoteUntil > DateTime.UtcNow.AddHours(20));
        Assert.NotNull(state.LastFrame);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task Wan_t2v_uploads_local_audio_and_sends_async_headers()
    {
        var (provider, handler) = Create(Vendor.Qwen, (request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/uploads" => Json("""{"data":{"policy":"p","signature":"s","upload_dir":"dashscope-instant/abc","upload_host":"https://oss.example","oss_access_key_id":"ak","x_oss_object_acl":"private","x_oss_forbid_overwrite":"true"}}"""),
            "/" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => Json("""{"output":{"task_id":"t-1","task_status":"PENDING"},"request_id":"r"}"""),
        });
        var id = await provider.CreateVideoTaskAsync(new VideoRequest("wan2.7-t2v-2026-06-12", "月光下奔跑", [], VideoMode.Frames, 8, "1080p", "9:16", false, true) { Audios = [Voice] }, default);
        Assert.Equal("t-1", id);
        Assert.Contains("model=wan2.7-t2v-2026-06-12", handler.Calls[0].Request.RequestUri!.Query);
        Assert.Contains("name=key", handler.Calls[1].Body);
        Assert.True(handler.Calls[1].Body.IndexOf("name=file", StringComparison.Ordinal) > handler.Calls[1].Body.IndexOf("name=key", StringComparison.Ordinal));
        var (request, body) = handler.Calls[2];
        Assert.Equal("https://dashscope.aliyuncs.com/api/v1/services/aigc/video-generation/video-synthesis", request.RequestUri!.ToString());
        Assert.Equal("enable", request.Headers.GetValues("X-DashScope-Async").Single());
        Assert.Equal("enable", request.Headers.GetValues("X-DashScope-OssResourceResolve").Single());
        var json = JsonNode.Parse(body)!;
        Assert.StartsWith("oss://dashscope-instant/abc/", (string?)json["input"]!["audio_url"]);
        Assert.EndsWith(".wav", (string?)json["input"]!["audio_url"]);
        Assert.Equal("1080P", (string?)json["parameters"]!["resolution"]);
        Assert.Equal("9:16", (string?)json["parameters"]!["ratio"]);
        Assert.Equal(8, (int?)json["parameters"]!["duration"]);
        Assert.Equal(true, (bool?)json["parameters"]!["watermark"]);
    }

    [Fact]
    public async Task Wan_i2v_frames_and_continuation_by_result_url()
    {
        var (provider, handler) = Create(Vendor.Qwen, (_, _) => Json("""{"output":{"task_id":"t-2"}}"""));
        await provider.CreateVideoTaskAsync(new VideoRequest("wan2.7-i2v", "转身", [Reference, Reference], VideoMode.Frames, 5, "720p", "adaptive", false, false), default);
        var frames = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.Equal(["first_frame", "last_frame"], frames["input"]!["media"]!.AsArray().Select(m => (string?)m!["type"]));
        Assert.Null(frames["parameters"]!["ratio"]);
        await provider.CreateVideoTaskAsync(new VideoRequest("wan2.7-i2v", "继续", [], VideoMode.Frames, 15, "720p", "adaptive", false, false) { Videos = [Clip with { Url = "https://dashscope-result.example/v.mp4" }], Task = "extend" }, default);
        var (request, body) = handler.Calls[1];
        var media = JsonNode.Parse(body)!["input"]!["media"]![0]!;
        Assert.Equal(("first_clip", "https://dashscope-result.example/v.mp4"), ((string?)media["type"], (string?)media["url"]));
        Assert.False(request.Headers.Contains("X-DashScope-OssResourceResolve"));
    }

    [Fact]
    public async Task Wan_videoedit_keeps_source_length_and_polls_result()
    {
        var (provider, handler) = Create(Vendor.Qwen, (request, _) => request.Method == HttpMethod.Post
            ? Json("""{"output":{"task_id":"t-3"}}""")
            : request.RequestUri!.AbsolutePath.StartsWith("/api/v1/tasks/")
                ? Json("""{"output":{"task_id":"t-3","task_status":"SUCCEEDED","video_url":"https://dashscope-result.example/out.mp4"}}""")
                : Bytes("video/mp4"));
        await provider.CreateVideoTaskAsync(new VideoRequest("wan2.7-videoedit", "黏土风格", [Reference], VideoMode.Frames, 5, "720p", "adaptive", false, false) { Videos = [Clip with { Url = "https://x.example/in.mp4" }], Task = "edit" }, default);
        var json = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.Equal(["video", "reference_image"], json["input"]!["media"]!.AsArray().Select(m => (string?)m!["type"]));
        Assert.Null(json["parameters"]!["duration"]);
        Assert.Null(json["parameters"]!["ratio"]);
        var state = await provider.PollVideoTaskAsync("t-3", default);
        Assert.Equal(VideoTaskStatus.Completed, state.Status);
        Assert.Equal("https://dashscope-result.example/out.mp4", state.RemoteUrl);
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
    public async Task Google_veo_extend_sends_the_video_uri_at_720p()
    {
        var (provider, handler) = Create(Vendor.Google, (_, _) => Json("""{"name":"models/veo-3.1-generate-preview/operations/op2"}"""));
        var uri = "https://generativelanguage.googleapis.com/v1beta/files/abc:download?alt=media";
        await provider.CreateVideoTaskAsync(new VideoRequest("veo-3.1-generate-preview", "继续向前", [], VideoMode.Frames, 4, "1080p", "9:16", false, false) { Videos = [Clip with { Url = uri }], Task = "extend" }, default);
        var json = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal(uri, (string?)json["instances"]![0]!["video"]!["uri"]);
        Assert.Equal("720p", (string?)json["parameters"]!["resolution"]);
        Assert.Equal(8, (int?)json["parameters"]!["durationSeconds"]);
        Assert.Equal("9:16", (string?)json["parameters"]!["aspectRatio"]);
    }

    [Fact]
    public async Task Google_omni_runs_in_background_and_downloads_the_file()
    {
        var (provider, handler) = Create(Vendor.Google, (request, _) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("POST", "/v1beta/interactions") => Json("""{"id":"v1_abc","status":"in_progress"}"""),
            ("GET", "/v1beta/interactions/v1_abc") => Json("""{"id":"v1_abc","status":"completed","steps":[{"type":"model_output","content":[{"type":"video","mime_type":"video/mp4","uri":"https://generativelanguage.googleapis.com/v1beta/files/f1:download?alt=media"}]}]}"""),
            ("GET", "/v1beta/files/f1") => Json("""{"name":"files/f1","state":"ACTIVE"}"""),
            _ => Bytes("video/mp4"),
        });
        var id = await provider.CreateVideoTaskAsync(new VideoRequest("gemini-omni-1.1-flash", "让小提琴隐形", [], VideoMode.Frames, 0, "1080p", "9:16", false, false) { Videos = [Clip], Task = "edit", PreviousId = "v1_prev" }, default);
        Assert.Equal("v1_abc", id);
        var json = JsonNode.Parse(handler.Calls[0].Body)!;
        Assert.Equal(true, (bool?)json["background"]);
        Assert.Equal(true, (bool?)json["store"]);
        Assert.Equal("v1_prev", (string?)json["previous_interaction_id"]);
        Assert.Equal("edit", (string?)json["generation_config"]!["video_config"]!["task"]);
        Assert.Equal("1080p", (string?)json["response_format"]!["resolution"]);
        Assert.Equal("9:16", (string?)json["response_format"]!["aspect_ratio"]);
        // Continuing the stored interaction never uploads the video again.
        Assert.Single(json["input"]![0]!["content"]!.AsArray());
        var state = await provider.PollVideoTaskAsync(id, default);
        Assert.Equal(VideoTaskStatus.Completed, state.Status);
        Assert.Equal("v1_abc", state.ConversationId);
        Assert.EndsWith("/v1beta/files/f1:download", handler.Calls.Last().Request.RequestUri!.AbsolutePath);
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
            """data: {"output":{"choices":[{"message":{"role":"assistant","content":[{"text":"好"}]}}]}}"""));
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

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
