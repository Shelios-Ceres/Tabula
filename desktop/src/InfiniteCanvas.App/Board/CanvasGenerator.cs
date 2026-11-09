using System.Globalization;
using InfiniteCanvas.App.Pages;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using InfiniteCanvas.Providers;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Turns a node plus its upstream connections into a provider request and writes results back into output nodes.
/// Runs on the UI thread; awaits resume there, so node updates need no marshalling except streamed text.
/// </summary>
public sealed class CanvasGenerator(CanvasDocument doc, DispatcherQueue ui, Action recordHistory, Action changed)
{
    /// <summary>Node.Error values for a user stop and an interrupted run; shown as paused, not failed.</summary>
    public const string Stopped = "已停止生成", Interrupted = "生成已中断";

    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly Dictionary<CancellationTokenSource, TaskCompletionSource> _pending = [];
    private bool _shuttingDown;

    public event Action? RunningChanged;
    /// <summary>Raised once new output nodes are placed and running, before the request is sent.</summary>
    public event Action<IReadOnlyList<CanvasNode>>? TargetsPrepared;
    /// <summary>Distinct generations in flight; a batch shares one token across its source and outputs.</summary>
    public int RunningCount => _pending.Count;
    public bool IsRunning(string nodeId) => _running.ContainsKey(nodeId);
    public void Cancel(string nodeId) { if (_running.TryGetValue(nodeId, out var cts)) cts.Cancel(); }

    /// <summary>Leaving the canvas: stop requests but keep vendor task IDs so polling resumes next time.</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        foreach (var cts in _pending.Keys.ToList()) cts.Cancel();
    }

    /// <summary>Wait for preprocessing, cancelled requests and received results to finish before the page's final save.</summary>
    public Task ShutdownAsync()
    {
        Shutdown();
        return Task.WhenAll(_pending.Values.Select(done => done.Task));
    }

    public void ResumeAfterCancelledLeave()
    {
        _shuttingDown = false;
        ResumeTasks();
    }

    private CancellationTokenSource Begin(params CanvasNode[] nodes)
    {
        var cts = new CancellationTokenSource();
        _pending.Add(cts, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        foreach (var node in nodes) _running[node.Id] = cts;
        RunningChanged?.Invoke();
        return cts;
    }

    private void Finish(CancellationTokenSource cts)
    {
        var done = _pending[cts];
        foreach (var id in _running.Where(pair => pair.Value == cts).Select(pair => pair.Key).ToList()) _running.Remove(id);
        _pending.Remove(cts);
        try
        {
            RunningChanged?.Invoke();
            changed();
        }
        finally
        {
            cts.Dispose();
            done.TrySetResult();
        }
    }

    public static Capability CapabilityOf(CanvasNode node) => NodeSpec.CapabilityOf(node);

    /// <summary>Video generation is not part of this version (it lives on the video-models branch): video nodes only hold imported clips.</summary>
    public static bool CanGenerate(CanvasNode node) => node.Kind != NodeKind.Group && CapabilityOf(node) != Capability.Video;

    /// <summary>Why a generation cannot start, for callers without dialogs such as agent tools; null when it can. Mirrors the checks in GenerateAsync.</summary>
    public static string? CannotGenerate(CanvasDocument doc, CanvasNode node)
    {
        if (RetrySource(doc, node) is { } origin) node = origin;
        if (!CanGenerate(node)) return node.Kind == NodeKind.Group ? "分组节点不能生成" : "这个版本不支持生成视频";
        var capability = CapabilityOf(node);
        var settings = SettingsStore.Current;
        if (settings.Resolve(settings.ModelFor(capability, node.Model)) is not { } resolved) return $"还没有可用的{Labels.Name(capability)}模型，请让用户先在设置里添加";
        if (string.IsNullOrWhiteSpace(resolved.Channel.ApiKey)) return $"服务商「{settings.ChannelName(resolved.Channel)}」没有可用的 API Key，请让用户先在设置里填写";
        if (string.IsNullOrWhiteSpace(node.Prompt) && InputsOf(doc, node).Count == 0) return "节点没有提示词，也没有连接任何输入";
        return null;
    }

    /// <summary>A node that already holds an image or text joins its own request as the first input; the panel shows the same list so 图片N numbers match.</summary>
    public static List<GenerationResource> InputsOf(CanvasDocument doc, CanvasNode node) => CanvasGraph.GenerationInputs(doc, node);

    /// <summary>
    /// An empty output node without a prompt of its own (a failed or stopped result) retries through the node that produced it:
    /// that node's current prompt, inputs and settings, writing into this node only. Null when the node runs as its own source.
    /// </summary>
    public static CanvasNode? RetrySource(CanvasDocument doc, CanvasNode node) =>
        !node.HasContent && string.IsNullOrWhiteSpace(node.Prompt) && node.Option(NodeKeys.SourceId) is { Length: > 0 } id && doc.Find(id) is { } origin && origin != node && CanGenerate(origin)
            ? origin : null;

    /// <summary>Generate from a node, or retry an empty output through its source (see <see cref="RetrySource"/>).</summary>
    public Task GenerateAsync(CanvasNode node, bool recordUndo = true) => RetrySource(doc, node) is { } origin ? RunAsync(origin, node, recordUndo) : RunAsync(node, null, recordUndo);

    private async Task RunAsync(CanvasNode source, CanvasNode? retry, bool recordUndo)
    {
        if (_shuttingDown || doc.Find(source.Id) != source || IsRunning(source.Id) || retry is not null && IsRunning(retry.Id) || !CanGenerate(source)) return;
        var capability = CapabilityOf(source);
        var settings = SettingsStore.Current;
        source.Model = settings.ModelFor(capability, source.Model);
        if (settings.Resolve(source.Model) is not { } resolved)
        {
            await Dialogs.MessageAsync("还没有可用的模型", $"请先在设置里为服务商添加{Labels.Name(capability)}模型。");
            return;
        }
        var (channel, model) = resolved;
        if (string.IsNullOrWhiteSpace(channel.ApiKey))
        {
            await Dialogs.MessageAsync("缺少 API Key", $"服务商「{settings.ChannelName(channel)}」还没有填写 API Key，或已保存的 Key 无法在本机解密，请到设置里重新填写。");
            return;
        }

        var resources = InputsOf(doc, source);
        if (string.IsNullOrWhiteSpace(source.Prompt) && resources.Count == 0)
        {
            await Dialogs.MessageAsync("请先输入提示词", "在提示词框里描述想要生成的内容，或连接文本、图片节点作为输入。");
            return;
        }

        // Speech reads its input aloud, so it gets plain text without 【文本N】 labels or image numbering, and never needs images.
        var audio = capability == Capability.Audio;
        var prompt = audio
            ? string.Join("\n\n", resources.Where(r => r.Type == NodeKind.Text).Select(r => r.Text).Prepend(source.Prompt).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()))
            : CanvasGraph.ComposePrompt(source.Prompt ?? "", resources);
        if (audio && prompt.Length == 0)
        {
            await Dialogs.MessageAsync("请先输入要朗读的文字", "在提示词框里输入要朗读的文字，或连接文本节点作为输入。");
            return;
        }
        // Keep this request's settings independent of edits made while references or the provider are still working.
        var snapshot = source.Clone();
        var systemPrompt = settings.SystemPrompt;
        var imageOptions = ImageOptionsOf(snapshot, ModelFamilies.FindImage(channel.Vendor, model.Name), channel);
        var references = audio ? [] : resources.Where(r => r.Type == NodeKind.Image).Select(r => r.Node.Clone()).ToList();
        var provider = AiProvider.For(channel, App.Http);
        var cts = retry is null ? Begin(source) : Begin(source, retry);
        var targets = new List<CanvasNode>();
        var images = new List<MediaInput>();
        var shrunk = new List<string>();
        DateTime? started = null;
        try
        {
            // A missing reference stops before history or output nodes are created; preprocessing is cancellable too.
            try
            {
                var limit = ReferenceLimits.For(channel.Vendor);
                for (var i = 0; i < references.Count; i++)
                {
                    var (input, note) = await LoadAsync(references[i], limit, references.Count, cts.Token);
                    images.Add(input);
                    if (note is not null) shrunk.Add($"图片{i + 1}：{note}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.Log(ex);
                if (!cts.IsCancellationRequested)
                    await Dialogs.MessageAsync("读取参考图片失败", $"上游图片文件丢失、无法读取或无法压缩到服务商上限内：{ex.Message}");
                return;
            }
            cts.Token.ThrowIfCancellationRequested();
            if (_shuttingDown || doc.Find(source.Id) != source || retry is not null && doc.Find(retry.Id) != retry) return;
            // Layer decomposition yields one base image plus its layers.
            var count = capability != Capability.Image || imageOptions.LayerDecomposition ? 1 : Math.Clamp(snapshot.Count, 1, 15);
            if (recordUndo) recordHistory();
            targets = retry is null ? PrepareTargets(source, snapshot, capability, count) : [Restart(retry)];
            if (retry is not null) CopySettings(snapshot, retry);
            var inputNote = shrunk.Count > 0 ? $"超过服务商上限的参考图已压缩后发送（原图未改动）：{string.Join("；", shrunk)}" : null;
            foreach (var target in targets)
            {
                target.SetOption(NodeKeys.InputNote, inputNote);
                _running[target.Id] = cts;
            }
            RunningChanged?.Invoke();
            if (targets.Any(t => t != source)) TargetsPrepared?.Invoke(targets);
            changed();
            started = DateTime.Now;
            switch (capability)
            {
                case Capability.Image: await RunImageAsync(provider, channel, model, snapshot, imageOptions, prompt, images, targets, cts.Token); break;
                case Capability.Audio: await RunAudioAsync(provider, model, snapshot, prompt, targets[0], cts.Token); break;
                default: await RunTextAsync(provider, channel, model, snapshot, systemPrompt, prompt, images, targets[0], cts.Token); break;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_shuttingDown) CancelTasks(provider, targets);
            Fail(targets, _shuttingDown ? Interrupted : Stopped, keepTask: _shuttingDown);
        }
        catch (Exception ex) when (targets.FirstOrDefault()?.TaskId is not null && Http.Transient(ex))
        {
            KeepTask(targets[0], ErrorText(ex, null));
        }
        catch (Exception ex)
        {
            Fail(targets, ErrorText(ex, started), keepTask: false);
        }
        finally
        {
            try
            {
                // Count finished pictures even when the rest of their batch was stopped.
                var success = targets.Count(t => t.Status == NodeStatus.Success);
                UsageStore.Record(channel.Id, model.Name, capability, success, provider.Tokens);
                if (capability == Capability.Image && success > 0 && provider.Tokens.Total > 0 && ModelFamilies.OpenAIApi(snapshot, ModelFamilies.FindImage(channel.Vendor, model.Name)) != "responses")
                    UsageStore.RememberImage(Prices.ImageKey(model.Name, snapshot), provider.Tokens.Per(success));
            }
            finally { Finish(cts); }
        }
    }

    /// <summary>
    /// An empty node of the right kind receives the result itself and the rest become its siblings;
    /// otherwise outputs go to new nodes on the right, connected from the source.
    /// </summary>
    private List<CanvasNode> PrepareTargets(CanvasNode source, CanvasNode settings, Capability capability, int count)
    {
        var kind = NodeSpec.KindOf(capability);
        var targets = new List<CanvasNode>();
        if (source.Kind == kind && !source.HasContent) targets.Add(source);
        // Siblings share the source's inputs and prompt instead of hanging off the first result.
        var sibling = targets.Count > 0;
        var inputs = sibling ? doc.Connections.Where(c => c.To == source.Id).Select(c => c.From).ToList() : [source.Id];
        double? x = null;
        var y = source.Y;
        while (targets.Count < count)
        {
            var node = CanvasNode.Create(kind, 0, 0);
            CopySettings(settings, node);
            if (sibling) node.Prompt = settings.Prompt;
            if (kind == NodeKind.Image)
            {
                var aspect = ModelFamilies.AspectOf(settings.Ratio is null or "auto" ? "1:1" : settings.Ratio);
                node.Height = Math.Clamp(node.Width / aspect, 120, 640);
            }
            node.Y = y;
            node.X = source.Right + 80;
            x ??= CanvasGraph.FreeX(node, doc.Nodes);
            node.X = x.Value;
            y += node.Height + 24;
            // Outputs placed inside a group join it, like nodes dropped there.
            node.GroupId = CanvasGraph.ContainingGroup(doc, node)?.Id;
            doc.Nodes.Add(node);
            foreach (var input in inputs) doc.Connections.Add(CanvasConnection.Create(input, node.Id));
            targets.Add(node);
        }
        foreach (var target in targets)
        {
            Restart(target);
            // Outputs remember their source so a failed one can be retried later without a prompt of its own.
            if (target != source && !sibling) target.SetOption(NodeKeys.SourceId, source.Id);
        }
        // A batch stays one card on the canvas until the user opens it, unless the run asked for a row or a grid.
        Stacks.FormBatch(targets, Stacks.ParseLayout(settings.Option(NodeKeys.Layout)) ?? StackLayout.Stack, Stacks.GapOf(settings));
        return targets;
    }

    private static CanvasNode Restart(CanvasNode target)
    {
        target.Status = NodeStatus.Loading;
        target.Error = null;
        target.Preview = null;
        // A new run never polls a task left over from an earlier one.
        target.TaskId = null;
        return target;
    }

    private static void CopySettings(CanvasNode from, CanvasNode to)
    {
        to.Model = from.Model;
        to.Size = from.Size;
        to.Ratio = from.Ratio;
        to.Quality = from.Quality;
        to.Seconds = from.Seconds;
        to.Resolution = from.Resolution;
        to.GenerateAudio = from.GenerateAudio;
        // Masks, selections and conversation IDs describe the source image only, and layer split is a one-off operation, so outputs never inherit them.
        to.Options = NodeKeys.OutputOptions(from, to);
    }

    private void Fail(IEnumerable<CanvasNode> targets, string message, bool keepTask)
    {
        foreach (var target in targets.Where(t => t.Status == NodeStatus.Loading))
        {
            if (keepTask && target.TaskId is not null) continue;
            target.Status = NodeStatus.Error;
            target.Error = message;
            target.Preview = null;
            target.TaskId = null;
        }
    }

    private static void KeepTask(CanvasNode node, string message)
    {
        node.Status = NodeStatus.Error;
        node.Error = $"查询结果失败，重新打开画布会继续查询：{message}";
        node.Preview = null;
    }

    /// <summary>A user stop also cancels background tasks on the vendor side (best effort, failures only logged); leaving the canvas keeps them to resume.</summary>
    private static void CancelTasks(AiProvider provider, IEnumerable<CanvasNode> targets)
    {
        foreach (var id in targets.Select(t => t.TaskId).OfType<string>().ToList())
            _ = provider.CancelTaskAsync(id).ContinueWith(t => App.Log(t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Vendor errors are shown as-is. Network failures only say "an error occurred while sending the request",
    /// so unwrap the inner causes and add how long after sending the connection dropped; details also go to the log.
    /// </summary>
    private static string ErrorText(Exception ex, DateTime? started)
    {
        if (ex is ApiException api)
            return string.Join("\n", new[] { api.Message, api.Status > 0 ? $"HTTP {api.Status}" : null, api.RequestId is { Length: > 0 } id ? $"Request ID：{id}" : null }.OfType<string>());
        App.Log(ex);
        if (ex is not (HttpRequestException or IOException)) return ex.Message;
        var causes = new List<string>();
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            if (!string.IsNullOrWhiteSpace(inner.Message) && !causes.Contains(inner.Message)) causes.Add(inner.Message.Trim());
        var elapsed = started is { } time ? $"，请求发出后约 {(int)(DateTime.Now - time).TotalSeconds} 秒" : "";
        var detail = causes.Count > 0 ? string.Join(" → ", causes) : ex.Message;
        return $"网络连接中断{elapsed}：{detail}\n常见原因是代理或网络线路在长时间等待中被断开，高画质、4K 等耗时较长的请求更容易遇到；可以稍后重试，或检查代理线路是否稳定。";
    }

    /// <summary>
    /// Read a reference for upload; the stored asset stays untouched. OpenAI, Gemini and xAI reject BMP / GIF, so those become PNG.
    /// A picture over the vendor's documented byte or pixel limit is re-encoded just under it: JPEG 95 at full size first,
    /// a smaller size only when that is still too big. Returns a note describing any shrinking.
    /// </summary>
    private async Task<(MediaInput Input, string? Note)> LoadAsync(CanvasNode node, ReferenceLimit limit, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(ProjectStore.AssetPath(doc.Id, node.Content!));
        var mime = node.MimeType ?? Media.MimeOf(node.Content!);
        // No vendor takes SVG as an input picture, so it goes as a PNG drawn at its own size.
        if (mime == "image/svg+xml") (bytes, mime) = (await ImageTools.RasterizeSvgAsync(bytes), "image/png");
        var (budget, pixelBudget) = (ReferenceLimits.Budget(limit, count), ReferenceLimits.PixelBudget(limit));
        var supported = mime is "image/png" or "image/jpeg" or "image/webp";
        if (supported && bytes.Length <= budget && pixelBudget == long.MaxValue) return (new(bytes, mime), null);
        using var input = new MemoryStream(bytes);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        ct.ThrowIfCancellationRequested();
        var (width, height) = ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        if (!supported)
        {
            using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            ct.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
            ct.ThrowIfCancellationRequested();
            encoder.SetSoftwareBitmap(pixels);
            await encoder.FlushAsync();
            ct.ThrowIfCancellationRequested();
            (bytes, mime) = (output.ToArray(), "image/png");
        }
        if (bytes.Length <= budget && (long)width * height <= pixelBudget) return (new(bytes, mime), null);
        var scale = Math.Min(1, Math.Sqrt((double)pixelBudget / ((long)width * height)));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var jpeg = await EncodeJpegAsync(decoder, scale, ct);
            if (jpeg.Length <= budget)
            {
                var resized = scale < 1 ? $"{width}×{height} → {(int)Math.Round(width * scale)}×{(int)Math.Round(height * scale)}" : "分辨率不变";
                return (new(jpeg, "image/jpeg"), $"{bytes.Length / 1e6:0.0}MB → {jpeg.Length / 1e6:0.0}MB，{resized}");
            }
            // Shrink just enough for the size to land under the budget, with a little headroom for JPEG variance.
            scale *= Math.Sqrt((double)budget / jpeg.Length) * 0.97;
        }
        throw new InvalidOperationException($"压缩后仍超过 {budget / 1e6:0.0}MB 上限");
    }

    private static async Task<byte[]> EncodeJpegAsync(BitmapDecoder decoder, double scale, CancellationToken ct)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        var quality = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.95f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output.AsRandomAccessStream(), quality);
        ct.ThrowIfCancellationRequested();
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();
        ct.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    /// <summary>A stored mask or markup layer is in the original image's coordinates; follow the prepared first reference's oriented dimensions.</summary>
    private static async Task<MediaInput> LoadMaskAsync(string path, MediaInput reference, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        using var referenceStream = new MemoryStream(reference.Bytes);
        var referenceDecoder = await BitmapDecoder.CreateAsync(referenceStream.AsRandomAccessStream());
        var (width, height) = (referenceDecoder.OrientedPixelWidth, referenceDecoder.OrientedPixelHeight);
        ct.ThrowIfCancellationRequested();
        using var input = new MemoryStream(bytes);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        ct.ThrowIfCancellationRequested();
        if (decoder.OrientedPixelWidth == width && decoder.OrientedPixelHeight == height) return new(bytes, "image/png");
        // The editor paints over Win2D's already oriented image, so scale the mask without rotating it a second time.
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.NearestNeighbor };
        using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();
        ct.ThrowIfCancellationRequested();
        return new(output.ToArray(), "image/png");
    }

    private async Task FillAsync(CanvasNode node, byte[] bytes, string mimeType)
    {
        if (doc.Find(node.Id) != node) return;
        var previous = node.Content;
        node.Content = ProjectStore.SaveAsset(doc.Id, bytes, mimeType);
        ProjectStore.TitleFromAsset(node, previous);
        node.MimeType = mimeType;
        // Match the node to the real frame, e.g. an adaptive-ratio video or a ratio the model did not support.
        var (width, height) = mimeType.StartsWith("image/") ? await ImageSizeAsync(bytes)
            : mimeType.StartsWith("video/") ? await VideoSizeAsync(ProjectStore.AssetPath(doc.Id, node.Content)) : (0, 0);
        if (doc.Find(node.Id) != node) return;
        if (width > 0 && height > 0)
        {
            node.NaturalWidth = width;
            node.NaturalHeight = height;
            node.Height = Math.Round(node.Width * height / width);
        }
        node.Preview = null;
        node.Error = null;
        node.Status = NodeStatus.Success;
        changed();
    }

    public static async Task<(int Width, int Height)> ImageSizeAsync(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch { return Media.SvgSize(bytes); }
    }

    /// <summary>Frame size from the container metadata; (0, 0) when it cannot be read.</summary>
    public static async Task<(int Width, int Height)> VideoSizeAsync(string path)
    {
        try
        {
            var properties = await (await StorageFile.GetFileFromPathAsync(path)).Properties.GetVideoPropertiesAsync();
            return ((int)properties.Width, (int)properties.Height);
        }
        catch { return (0, 0); }
    }

    private static string DefaultFormat(ImageFamily? family, Vendor vendor) => family?.OutputFormats.FirstOrDefault() ?? (vendor == Vendor.OpenAI ? "png" : "jpeg");

    /// <summary>The Responses image tool runs inside a text model of the same channel; the first one unless the node picked another.</summary>
    public static string MainModelOf(CanvasNode node, ModelChannel channel) =>
        node.Option("mainModel", channel.Models.FirstOrDefault(m => m.Capability == Capability.Text)?.Name ?? "");

    private static ImageOptions ImageOptionsOf(CanvasNode node, ImageFamily? family, ModelChannel channel) => new()
    {
        // Values left over from another family fall back to what the panel shows (adapters use their first tier / the vendor default).
        Tier = family is null || family.Tiers.Contains(node.Size) ? node.Size : null,
        Ratio = family is null || family.Ratios.Contains(node.Ratio) ? node.Ratio : null,
        Quality = family is null || family.Qualities.Contains(node.Quality) ? node.Quality : null,
        Complexity = family?.Complexities.Contains(node.Option("complexity")) == true ? node.Option("complexity") : null,
        Background = node.Option("background", "auto"),
        OutputFormat = node.Option("outputFormat", DefaultFormat(family, channel.Vendor)) is var format && (family is null || family.OutputFormats.Contains(format)) ? format : DefaultFormat(family, channel.Vendor),
        FrameCount = int.TryParse(node.Option("frameCount"), out var frames) ? frames : null,
        FrameDurationMs = int.TryParse(node.Option("frameDuration"), out var duration) ? duration : null,
        Compression = int.TryParse(node.Option("compression"), out var compression) ? compression : 100,
        Moderation = node.Option("moderation", "auto"),
        Stream = node.Option("stream") == "true",
        PartialImages = int.TryParse(node.Option("partialImages"), out var partial) ? partial : 0,
        Thinking = node.Option("thinking", "auto"),
        WebSearch = node.Option("webSearch") == "true",
        ImageSearch = node.Option("imageSearch") == "true",
        Watermark = node.Option("watermark") == "true",
        FastPrompt = node.Option("fastPrompt") == "true",
        NegativePrompt = node.Option("negativePrompt"),
        PromptExtend = node.Option("promptExtend", "true") == "true",
        OpenAIApi = ModelFamilies.OpenAIApi(node, family),
        MainModel = MainModelOf(node, channel),
        // The mainline model only rewrites the prompt and calls the tool, so it defaults to its lowest effort.
        MainReasoning = ModelFamilies.SnapReasoning(MainModelOf(node, channel), node.Option("mainReasoning")),
        Store = node.Option("store") == "true",
        // Switches stay on the node after a model change; only the families that show them may send them.
        LayerDecomposition = node.Option("layers") == "true" && family?.Layers == true,
        Transparent = node.Option("transparent") == "true" && family?.Interactive == true,
        PromptExtendMode = node.Option("promptExtendMode", "direct"),
        EnableThinking = node.Option("qwenThinking") is { Length: > 0 } thinking ? thinking == "true" : null,
        Seed = long.TryParse(node.Option("seed"), out var seed) ? seed : null,
    };

    /// <summary>Serialize provider events on the UI thread; draining also waits for final images to reach disk.</summary>
    private sealed class ImageProgress(DispatcherQueue ui, Func<ImageEvent, Task> apply) : IProgress<ImageEvent>
    {
        private readonly object _gate = new();
        private Task _pending = Task.CompletedTask;

        public Task DrainAsync() { lock (_gate) return _pending; }

        public void Report(ImageEvent value)
        {
            lock (_gate)
            {
                var previous = _pending;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = done.Task;
                if (!ui.TryEnqueue(async () =>
                {
                    try { await previous; await apply(value); done.TrySetResult(); }
                    catch (Exception ex) { done.TrySetException(ex); }
                })) done.TrySetException(new InvalidOperationException("画布已关闭，无法保存生成结果"));
            }
        }
    }

    private async Task RunImageAsync(AiProvider provider, ModelChannel channel, ChannelModel model, CanvasNode source, ImageOptions options, string prompt, List<MediaInput> images, List<CanvasNode> targets, CancellationToken ct)
    {
        var started = DateTime.Now;
        var family = ModelFamilies.FindImage(channel.Vendor, model.Name);
        // Editing an image node: its own marks travel with the request (mask for GPT Image, points / boxes for Seedream).
        var editing = source.Kind == NodeKind.Image && source.HasContent;
        var mask = editing && channel.Vendor == Vendor.OpenAI && source.Option(NodeKeys.Mask) is { Length: > 0 } maskAsset && File.Exists(ProjectStore.AssetPath(doc.Id, maskAsset))
            ? await LoadMaskAsync(ProjectStore.AssetPath(doc.Id, maskAsset), images[0], ct) : null;
        var markup = editing && channel.Vendor == Vendor.OpenAI && family?.Markup == true && source.Option(NodeKeys.Markup) is { Length: > 0 } markupAsset && File.Exists(ProjectStore.AssetPath(doc.Id, markupAsset))
            ? await LoadMaskAsync(ProjectStore.AssetPath(doc.Id, markupAsset), images[0], ct) : null;
        ct.ThrowIfCancellationRequested();
        var selections = editing && family?.Interactive == true ? NodeKeys.SelectionsOf(source) : [];
        // Continuing a stored conversation: the image is already in it, so only extra inputs are sent again.
        // OpenAI only continues through the Responses image tool; the Image API ignores previous IDs.
        var continues = options.Store && editing && (channel.Vendor != Vendor.OpenAI || options.OpenAIApi == "responses");
        var previousId = continues && source.Option(NodeKeys.ConversationModel) == source.Model ? source.Option(NodeKeys.ConversationId) : "";
        if (previousId.Length > 0) images = images.Skip(1).ToList();
        if (options.LayerDecomposition)
        {
            await RunLayersAsync(provider, model, prompt, images, options, targets[0], ct);
            return;
        }
        // Each request carries as many images as the family allows (the OpenAI Responses image tool and SVG output return one;
        // Seedream group generation counts references against the same limit); the rest run as parallel requests.
        var perRequest = channel.Vendor == Vendor.OpenAI && (options.OpenAIApi == "responses" || options.OutputFormat == "svg") ? 1 : Math.Max(1, (family?.MaxImages ?? 1) - (family?.Sequential == true ? images.Count : 0));
        var groups = targets.Chunk(perRequest).ToList();
        await Task.WhenAll(groups.Select(async group =>
        {
            var progress = new ImageProgress(ui, async e =>
            {
                // An OpenAI Responses background task: saved with the canvas so reopening it keeps polling.
                if (e.TaskId is { } taskId && group[0].Status == NodeStatus.Loading)
                {
                    group[0].TaskId = taskId;
                    group[0].TaskModel = source.Model;
                    changed();
                }
                if (e.Preview is { } preview && group[0].Status == NodeStatus.Loading) group[0].Preview = preview.Bytes;
                // Output is a finished picture, unlike a preview: retain it even if later pictures fail or are cancelled.
                if (e.Output is { } output && e.Index >= 0 && e.Index < group.Length && group[e.Index].Status == NodeStatus.Loading)
                    await FillAsync(group[e.Index], output.Bytes, output.MimeType);
            });
            try
            {
                var request = new ImageRequest(model.Name, prompt, images, mask, group.Length, options, model.EndpointId, selections, previousId.Length > 0 ? previousId : null, markup);
                ImageResult result;
                try { result = await provider.GenerateImageAsync(request, progress, ct); }
                finally { await progress.DrainAsync(); }
                for (var i = 0; i < group.Length; i++)
                {
                    if (i >= result.Images.Count)
                    {
                        Fail([group[i]], result.Errors.ElementAtOrDefault(i - result.Images.Count) ?? "接口返回的图片少于请求数量", false);
                        continue;
                    }
                    await FillImageAsync(group[i], result, i, source.Model);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var node in group.Where(n => n.Status == NodeStatus.Loading))
                    if (node.TaskId is not null && Http.Transient(ex)) KeepTask(node, ErrorText(ex, started));
                    else Fail([node], ErrorText(ex, started), false);
            }
        }));
    }

    private async Task FillImageAsync(CanvasNode node, ImageResult result, int index, string? model)
    {
        if (doc.Find(node.Id) != node) return;
        if (node.Status == NodeStatus.Loading) await FillAsync(node, result.Images[index].Bytes, result.Images[index].MimeType);
        if (node.Status != NodeStatus.Success) return;
        node.TaskId = null;
        // Remember where this image came from so the next edit can continue the same conversation.
        node.SetOption(NodeKeys.ConversationId, result.ConversationId);
        node.SetOption(NodeKeys.ConversationModel, result.ConversationId is null ? null : model);
        node.SetOption(NodeKeys.ModelText, result.Texts.Count > 0 ? string.Join("\n", result.Texts) : null);
    }

    /// <summary>
    /// Seedream layer decomposition: the base image fills the target, each layer becomes an image node placed by its
    /// bounding box on top of the base (stacked by z-index), and everything is wrapped in one group.
    /// </summary>
    private async Task RunLayersAsync(AiProvider provider, ChannelModel model, string prompt, List<MediaInput> images, ImageOptions options, CanvasNode target, CancellationToken ct)
    {
        var result = await provider.GenerateImageAsync(new ImageRequest(model.Name, prompt, images, null, 1, options, model.EndpointId), null, ct);
        if (result.Images.Count == 0) throw new ApiException(result.Errors.FirstOrDefault() ?? "图层拆分没有返回图片");
        await FillAsync(target, result.Images[0].Bytes, result.Images[0].MimeType);
        target.Title = "底图";
        var members = new List<CanvasNode> { target };
        foreach (var image in result.Images.Skip(1).Where(i => i.Layer?.Box is { Length: 4 }).OrderBy(i => i.Layer!.ZIndex))
        {
            var box = image.Layer!.Box!;
            var layer = new CanvasNode
            {
                Kind = NodeKind.Image,
                Title = image.Layer.Name ?? $"图层 {image.Layer.ZIndex}",
                X = target.X + box[0] / 1000 * target.Width,
                Y = target.Y + box[1] / 1000 * target.Height,
                Width = Math.Max(24, (box[2] - box[0]) / 1000 * target.Width),
                Height = Math.Max(24, (box[3] - box[1]) / 1000 * target.Height),
            };
            layer.SetOption(NodeKeys.ModelText, image.Layer.Description);
            doc.Nodes.Add(layer);
            await FillAsync(layer, image.Bytes, image.MimeType);
            members.Add(layer);
        }
        var rect = CanvasGraph.GroupRect(members);
        var group = new CanvasNode { Kind = NodeKind.Group, Title = "图层拆分", X = rect.Left, Y = rect.Top, Width = rect.Width, Height = rect.Height };
        doc.Nodes.Insert(0, group);
        foreach (var member in members) member.GroupId = group.Id;
        if (result.Errors.Count > 0) target.Error = string.Join("\n", result.Errors);
    }

    /// <summary>
    /// Local upscale into a new image node to the right of the source, connected from it.
    /// Registered like a generation, so 停止, deleting the node and the leave confirmation all apply (stopping kills the tool).
    /// </summary>
    public async Task UpscaleAsync(CanvasNode source, string model, int factor)
    {
        if (_shuttingDown || !source.HasContent || doc.Find(source.Id) != source) return;
        recordHistory();
        var target = CanvasNode.Create(NodeKind.Image, 0, 0);
        target.Title = $"放大 {factor}×";
        target.Width = source.Width;
        target.Height = source.Height;
        target.Y = source.Y;
        target.X = source.Right + 80;
        target.X = CanvasGraph.FreeX(target, doc.Nodes);
        target.GroupId = CanvasGraph.ContainingGroup(doc, target)?.Id;
        target.Status = NodeStatus.Loading;
        doc.Nodes.Add(target);
        doc.Connections.Add(CanvasConnection.Create(source.Id, target.Id));
        var cts = Begin(target);
        TargetsPrepared?.Invoke([target]);
        changed();
        try { await FillAsync(target, await Upscaler.RunAsync(ProjectStore.AssetPath(doc.Id, source.Content!), model, factor, cts.Token), "image/png"); }
        catch (OperationCanceledException) { Fail([target], _shuttingDown ? Interrupted : Stopped, false); }
        catch (Exception ex) { Fail([target], ErrorText(ex, null), false); }
        finally { Finish(cts); }
    }

    /// <summary>Resume background image tasks left running when the canvas was last closed; a video task from an earlier version is no longer polled.</summary>
    public void ResumeTasks()
    {
        if (_shuttingDown) return;
        foreach (var node in doc.Nodes.Where(n => n.TaskId is not null && !n.HasContent && !IsRunning(n.Id)).ToList())
        {
            if (node.Kind == NodeKind.Image) _ = ResumeAsync(node);
            else (node.TaskId, node.Status, node.Error) = (null, NodeStatus.Error, "这个版本不支持生成视频，未完成的视频任务已停止查询");
        }
    }

    private async Task ResumeAsync(CanvasNode node)
    {
        if (SettingsStore.Current.Resolve(node.TaskModel) is not { } resolved || string.IsNullOrWhiteSpace(resolved.Channel.ApiKey))
        {
            // Keep the task ID: once the channel, model or key is back, reopening the canvas continues polling.
            node.Status = NodeStatus.Error;
            node.Error = "找不到创建该任务的服务商、模型或 API Key，恢复后重新打开画布会继续查询";
            return;
        }
        var provider = AiProvider.For(resolved.Channel, App.Http);
        await TrackAsync(node, provider, async ct =>
        {
            await FillImageAsync(node, await provider.ResumeImageTaskAsync(node.TaskId!, node.Option("store") == "true", ct), 0, node.TaskModel);
            UsageStore.Record(resolved.Channel.Id, resolved.Model.Name, Capability.Image, 1, provider.Tokens);
        });
    }

    /// <summary>One output node working outside a normal generation (a resumed task), with the same stop and failure handling.</summary>
    private async Task TrackAsync(CanvasNode node, AiProvider provider, Func<CancellationToken, Task> work)
    {
        if (_shuttingDown || IsRunning(node.Id) || doc.Find(node.Id) != node) return;
        var cts = Begin(node);
        node.Status = NodeStatus.Loading;
        node.Error = null;
        changed();
        try { await work(cts.Token); }
        catch (OperationCanceledException)
        {
            if (!_shuttingDown) CancelTasks(provider, [node]);
            Fail([node], _shuttingDown ? Interrupted : Stopped, _shuttingDown);
        }
        catch (Exception ex) when (node.TaskId is not null && Http.Transient(ex)) { KeepTask(node, ErrorText(ex, null)); }
        catch (Exception ex) { Fail([node], ErrorText(ex, null), false); }
        finally { Finish(cts); }
    }

    private async Task RunTextAsync(AiProvider provider, ModelChannel channel, ChannelModel model, CanvasNode source, string? systemPrompt, string prompt, List<MediaInput> images, CanvasNode target, CancellationToken ct)
    {
        // Only Ark text models can be bound to an ep- endpoint; the request then targets the endpoint instead of the model ID.
        var name = channel.Vendor == Vendor.Ark && !string.IsNullOrWhiteSpace(model.EndpointId) ? model.EndpointId.Trim() : model.Name;
        var request = new TextRequest(name, prompt, images, systemPrompt, source.Option("reasoning", "auto"));
        // Each delta carries the whole text so far: keep only the newest and queue at most one UI update, so a busy UI thread never piles up stale copies.
        string? latest = null;
        try
        {
            var text = await provider.GenerateTextAsync(request, delta =>
            {
                if (Interlocked.Exchange(ref latest, delta) is null)
                    ui.TryEnqueue(() => { if (Interlocked.Exchange(ref latest, null) is { } current && target.Status == NodeStatus.Loading && doc.Find(target.Id) == target) target.Content = current; });
            }, ct);
            if (doc.Find(target.Id) != target) return;
            target.Content = text;
            target.Status = NodeStatus.Success;
            target.Error = null;
        }
        finally
        {
            // The last UI delta may still be queued when EOF / cancellation arrives; preserve it before marking the node failed.
            if (Interlocked.Exchange(ref latest, null) is { } current && target.Status == NodeStatus.Loading && doc.Find(target.Id) == target) target.Content = current;
        }
    }

    private async Task RunAudioAsync(AiProvider provider, ChannelModel model, CanvasNode source, string prompt, CanvasNode target, CancellationToken ct)
    {
        // The panel writes speeds like "1.25"; parse them the same way under every regional format.
        var speed = double.TryParse(source.Option("speed", "1"), CultureInfo.InvariantCulture, out var value) ? value : 1;
        var request = new SpeechRequest(model.Name, prompt, source.Option("voice", "alloy"), source.Option("audioFormat", "mp3"), speed, source.Option("instructions"));
        var audio = await provider.SpeechAsync(request, ct);
        await FillAsync(target, audio.Bytes, audio.MimeType);
    }
}
