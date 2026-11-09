using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InfiniteCanvas.Agents;

/// <summary>The open canvas as the agent tools see it; implemented by the canvas page.</summary>
public interface ICanvasHost
{
    /// <summary>Run on the UI thread: every read and write of the document goes through here.</summary>
    Task<T> OnUiAsync<T>(Func<T> action);
    CanvasDocument Document { get; }
    IReadOnlyCollection<string> Selection { get; }
    Viewport Viewport { get; set; }
    /// <summary>World coordinates of the visible area's centre.</summary>
    (double X, double Y) ViewCenter { get; }
    void RecordHistory();
    void Select(IEnumerable<string> ids);
    /// <summary>Delete nodes with their connections, like the Delete key.</summary>
    void Delete(IEnumerable<string> ids);
    void Reveal(IReadOnlyList<CanvasNode> nodes);
    bool IsRunning(string nodeId);
    /// <summary>Why a generation cannot start (missing model, key or prompt), or null when it can.</summary>
    string? CannotGenerate(CanvasNode node);
    void Generate(CanvasNode node, bool recordHistory = true);
    /// <summary>The node's picture scaled to fit maxEdge, or null when it has none.</summary>
    Task<(byte[] Bytes, string MimeType)?> LoadImageAsync(CanvasNode node, int maxEdge);
    /// <summary>Put nodes (and members of groups among them) into one new group; its ID, or null when nothing changes. No history record.</summary>
    string? Group(IReadOnlyCollection<string> ids, string? title);
    /// <summary>Release nodes from their groups, or every member of the given groups. No history record.</summary>
    void Ungroup(IReadOnlyCollection<string> ids);
    /// <summary>Move nodes (collapsed stacks whole) into an existing group; groups left empty are deleted. The moved node IDs. No history record.</summary>
    IReadOnlyList<string> AddToGroup(string groupId, IReadOnlyCollection<string> ids, bool fit);
    /// <summary>Take nodes (collapsed stacks whole) out of their groups; groups left empty are deleted. The released node IDs. No history record.</summary>
    IReadOnlyList<string> RemoveFromGroup(IReadOnlyCollection<string> ids, bool fit);
    /// <summary>Import media files as new nodes from a world point (one undo step). Never shows a dialog: files that cannot be imported come back in Failed.</summary>
    Task<ImportResult> ImportAsync(IReadOnlyList<string> paths, double x, double y);
    /// <summary>Give a media node a new file of its kind, keeping its ID, position, width, group, stack and connections (one undo step). Throws when the copy fails; never shows a dialog.</summary>
    Task ReplaceMediaAsync(CanvasNode node, string path);
    /// <summary>Copy a node's media into this canvas's library, named after the node; its file name. A name already taken is an error unless <paramref name="replace"/>.</summary>
    Task<string> AddToLibraryAsync(CanvasNode node, bool replace);
}

public sealed record ImportFailure(string Path, string Reason);

/// <summary>New node IDs and the files that could not be imported.</summary>
public sealed record ImportResult(IReadOnlyList<string> Ids, IReadOnlyList<ImportFailure> Failed);

/// <summary>One canvas_apply_ops step; fields not used by the op type are ignored.</summary>
public sealed class CanvasOp
{
    [Description("add_node、update_node、delete_nodes、connect、disconnect、select、set_viewport、run_generation、group、ungroup、group_add、group_remove、stack_open、stack_close、keep_one、promote")]
    public string Type { get; set; } = "";
    [Description("add_node：给新节点起的临时名，本批后续操作可用它代替节点 ID")]
    public string? Ref { get; set; }
    [Description("update_node / run_generation 的节点 ID（或本批的 ref）；group_add 的目标分组 ID（或本批的 ref）；stack_open / stack_close 里这一批（摞）的任意一个成员的 ID；keep_one 要留下的那个成员；promote 定稿的新图片（或本批的 ref）")]
    public string? Id { get; set; }
    [Description("delete_nodes / select / group / ungroup / group_add / group_remove 的节点 ID 列表（ungroup 也可以直接给分组 ID）")]
    public List<string>? Ids { get; set; }
    [Description("add_node 的类型：text、image、video、audio、config")]
    public string? Kind { get; set; }
    [Description("config 节点的生成类型：image、text、audio")]
    public string? Mode { get; set; }
    public string? Title { get; set; }
    [Description("左上角世界坐标；add_node 省略时放在可视区域中央附近的空位")]
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    [Description("文本节点的正文")]
    public string? Text { get; set; }
    [Description("生成用的提示词")]
    public string? Prompt { get; set; }
    [Description("模型 ID，取自 canvas_get_state 的 models")]
    public string? Model { get; set; }
    [Description("图片比例，如 1:1、16:9")]
    public string? Ratio { get; set; }
    [Description("图片分辨率档位，如 1K、2K、4K")]
    public string? Size { get; set; }
    public string? Quality { get; set; }
    [Description("一次生成的张数")]
    public int? Count { get; set; }
    [Description("其他生成参数，键值与 canvas_get_state 节点的 options 相同，例如 background=transparent、outputFormat=webp、voice=Kore；watermark、layers、store 等开关取 \"true\"；值为 null 时清除")]
    public Dictionary<string, string?>? Options { get; set; }
    [Description("connect / disconnect 的上游节点")]
    public string? From { get; set; }
    [Description("connect / disconnect 的下游节点")]
    public string? To { get; set; }
    [Description("set_viewport：缩放倍数，X / Y 为要移到视口中央的世界坐标")]
    public double? Zoom { get; set; }
    [Description("group_add / group_remove：是否按成员重算分组边界，默认 true")]
    public bool? Fit { get; set; }
    [Description("add_node（count 大于 1 时）：这一批生成结果的排布，stack（默认，叠在一起）、row（横排）、grid（网格）；stack_open：row 或 grid，默认 grid")]
    public string? Layout { get; set; }
    [Description("add_node / stack_open：row、grid 排布时成员之间的间距（世界坐标），默认 24")]
    public double? Gap { get; set; }
    [Description("promote：被定稿的新图片取代的图片节点 ID（或本批的 ref）；它在画布上被删除，文件不删")]
    public string? Replaces { get; set; }
    [Description("promote：资料库里有和被取代图片同名的文件时，是否用新图片替换它（旧文件移到回收站，不能撤销），默认 false")]
    public bool? Library { get; set; }
}

/// <summary>MCP tools over the open canvas.</summary>
public static class CanvasTools
{
    public const string Instructions = """
        你正在操作用户在「无限画布」桌面客户端里当前打开的画布。
        - 先用 canvas_get_state 了解节点、连线、选区和可用模型；用户说「这张图」「选中的」时指的是 selection 里的节点。
        - canvas_get_state 默认不带提示词、文本正文和生成参数；要读这些用 canvas_get_node（或 detail=full）。画布很大时先用 rect 或 groupId 缩小范围。
        - 生成图片、文本或语音用 canvas_generate（这个版本不支持生成视频，视频节点只用来放导入的视频）：它会新建节点、连上参考节点并立刻开始生成。生成是异步的，用 generation_get_status 查看结果，不要在没有结果时声称已经完成。
        - 增删改节点、连线、选择和视口用 canvas_apply_ops，一次调用就是一步撤销。
        - 需要看图片内容时用 canvas_get_image；资料库（这个画布自己的常用素材）用 library_list / library_place / library_add；一次生成多张时用 layout=row 或 grid 直接排开，已有的一批用 stack_open / stack_close 展开或收起，挑定稿用 keep_one（留下一批里的一张）和 promote（让一张图取代另一张，占它的位置和连线）；编组用 canvas_apply_ops 的 group / ungroup，把节点加入或移出已有分组（分组 ID 和连线不变）用 group_add / group_remove。
        - 节点 ID 只用工具返回的值，不要编造；模型 ID 只用 canvas_get_state.models 里列出的。
        """;

    // Chinese titles and prompts stay readable instead of escape sequences, which also costs the agent fewer tokens.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IEnumerable<McpServerTool> Create(ICanvasHost host) =>
    [
        McpServerTool.Create((
            [Description("brief（默认）或 full；full 带提示词、正文和生成参数")] string? detail = null,
            [Description("只返回这些节点")] List<string>? ids = null,
            [Description("只返回这个分组节点和它的成员")] string? groupId = null,
            [Description("世界坐标矩形 [left, top, right, bottom]，只返回与它相交的节点")] List<double>? rect = null) => host.OnUiAsync(() => State(host, detail, ids, groupId, rect)), new()
        {
            Name = "canvas_get_state", ReadOnly = true,
            Description = "读取当前画布。默认只返回节点的精简字段（类型、位置、尺寸、状态、媒体文件、分组、摞），不含提示词、正文和生成参数；可用 ids / groupId / rect 缩小范围，detail=full 返回完整字段。",
        }),
        McpServerTool.Create(([Description("要读取的节点 ID")] List<string> ids) => host.OnUiAsync(() => Json(new { Nodes = FindAll(host, ids).Select(n => Full(host, n)) })), new()
        {
            Name = "canvas_get_node", ReadOnly = true,
            Description = "读取节点的完整信息（提示词、文本正文、模型、比例、分辨率、options 等）。",
        }),
        McpServerTool.Create(([Description("按顺序执行的操作")] List<CanvasOp> ops) => host.OnUiAsync(() => Apply(host, ops)), new()
        {
            Name = "canvas_apply_ops", Destructive = true,
            Description = "批量修改画布，整批算一步撤销。type 支持 add_node、update_node、delete_nodes、connect、disconnect、select、set_viewport、run_generation、group、ungroup、group_add、group_remove、stack_open、stack_close（展开 / 收起一批生成结果，stack_open 可选 layout=row|grid 和 gap）、keep_one（留下一批里的这一张，删掉同批其余，连线转给它）、promote（id 定稿，取代 replaces：占它的位置、标题、分组和连线，旧节点从画布删除，文件都不删；library=true 时资料库里同名的旧文件移到回收站）；add_node 可用 ref 给新节点起名，供本批后续操作引用。",
        }),
        McpServerTool.Create((
            [Description("image、text、audio")] string mode,
            [Description("提示词")] string prompt,
            [Description("作为输入的已有节点 ID（参考图、文本等）")] List<string>? referenceNodeIds = null,
            [Description("模型 ID，省略时用默认模型")] string? model = null,
            [Description("比例，如 1:1、16:9")] string? ratio = null,
            [Description("分辨率档位，如 1K、2K、4K")] string? size = null,
            string? quality = null,
            [Description("张数")] int? count = null,
            [Description("新节点左上角世界坐标，省略时自动放置")] double? x = null,
            double? y = null,
            [Description("张数大于 1 时这一批结果的排布：stack（默认，叠成一摞）、row（横排）、grid（网格）")] string? layout = null,
            [Description("row / grid 排布时成员之间的间距（世界坐标），默认 24")] double? gap = null,
            [Description("true 时只校验参数并估价，不建节点、不发请求")] bool? dryRun = null) => host.OnUiAsync(() => Generate(host, mode, prompt, referenceNodeIds, model, ratio, size, quality, count, x, y, layout, gap, dryRun ?? false)), new()
        {
            Name = "canvas_generate",
            Description = "新建一个生成节点（图片 / 文本 / 语音），连上参考节点并立刻开始生成，返回节点 ID。多张图默认叠成一摞，用 layout=row 或 grid 可直接排成横排或网格。图片生成会返回 estimatedCost {amount, currency, basis}，没有官方单价时为 null 并附 estimatedCostNote；dryRun=true 只校验和估价、不花钱。估价是本机估算，不含思考 token，也不是账单。",
        }),
        McpServerTool.Create(([Description("要生成的节点 ID")] string nodeId) => host.OnUiAsync(() => RunTool(host, Find(host, nodeId))), new()
        {
            Name = "canvas_run_generation",
            Description = "按节点当前的提示词、模型和连线输入重新触发生成。图片生成会返回 estimatedCost {amount, currency, basis}，没有官方单价时为 null 并附 estimatedCostNote。",
        }),
        McpServerTool.Create(([Description("起始日期 yyyy-MM-dd（含当天），或 today、month（本月 1 号起）；省略时是今天")] string? since = null) => host.OnUiAsync(() => Usage(since)), new()
        {
            Name = "usage_summary", ReadOnly = true,
            Description = "本机账本里从某天起各渠道生成的数量和估算金额，按币种分开列（火山方舟、千问是人民币，其他是美元，不会相加）。金额只含有 token 单价的模型，其余在 unpricedModels 里；这是本机估算，不是服务商的账单，也不会限制生成。",
        }),
        McpServerTool.Create(([Description("要查询的节点 ID；省略时返回所有生成中或失败的节点")] List<string>? nodeIds = null) => host.OnUiAsync(() => Status(host, nodeIds)), new()
        {
            Name = "generation_get_status", ReadOnly = true,
            Description = "查询节点的生成状态（idle / loading / success / error）、错误信息和结果文件。batch 列出同批全部成员（含节点本身），outputs 列出下游输出。",
        }),
        McpServerTool.Create(async ([Description("图片节点 ID")] string nodeId) => await GetImageAsync(host, nodeId), new()
        {
            Name = "canvas_get_image", ReadOnly = true,
            Description = "查看图片节点的画面（长边缩到 1024 像素以内）。",
        }),
        McpServerTool.Create(async ([Description("image、video、audio，省略时全部")] string? kind = null, [Description("按文件名筛选")] string? keyword = null) => Library(await host.OnUiAsync(() => host.Document.Id), kind, keyword), new()
        {
            Name = "library_list", ReadOnly = true,
            Description = "列出当前画布资料库里的常用图片、视频、音频，新的在前。",
        }),
        McpServerTool.Create(async (
            [Description("资料库文件名，取自 library_list 的 name")] List<string> names,
            [Description("放置位置左上角的世界坐标，省略时放在可视区域中央")] double? x = null,
            double? y = null) => await PlaceAsync(host, names, x, y), new()
        {
            Name = "library_place",
            Description = "把当前画布资料库里的文件放到画布上，成为新的图片 / 视频 / 音频节点，返回节点 ID（可作为生成的参考节点）；放不上去的文件列在 failed 里，全部失败时报错。",
        }),
        McpServerTool.Create(async (
            [Description("本机文件的绝对路径（带盘符，不接受网络路径）；支持 png、jpg、jpeg、webp、gif、svg、bmp、mp4、mov、webm、mp3、wav、aac、flac、opus、m4a")] List<string> paths,
            [Description("放置位置左上角的世界坐标，省略时放在可视区域中央")] double? x = null,
            double? y = null) => await ImportFilesAsync(host, paths, x, y), new()
        {
            Name = "canvas_import_file",
            Description = "把本机文件导入画布，成为新的图片 / 视频 / 音频节点（文件会复制进画布自己的文件夹），返回 nodeIds，导入不了的文件列在 failed: [{path, reason}] 里。只导入用户在对话里明确点名的文件，不要自己翻找或导入其他本机文件。",
        }),
        McpServerTool.Create(async (
            [Description("要替换内容的图片、视频或音频节点 ID")] string nodeId,
            [Description("新文件的绝对路径，类型必须和节点一致（图片换图片）")] string path) => await ReplaceMediaAsync(host, nodeId, path), new()
        {
            Name = "canvas_replace_media", Destructive = true,
            Description = "给已有的图片 / 视频 / 音频节点换上本机的一个新文件，节点 ID、位置、宽度、分组和连线都不变，整次算一步撤销，旧文件仍留在画布文件夹里。节点正在生成时不能替换；节点上的提示词和模型参数不会清除，之后重新生成会盖掉替换进去的文件。只用用户在对话里明确点名的文件。",
        }),
        McpServerTool.Create(async (
            [Description("图片、视频或音频节点 ID")] string nodeId,
            [Description("资料库里已有同名文件时是否覆盖，默认 false（报错）；true 时旧文件移到回收站")] bool? replace = null) => await AddToLibraryAsync(host, nodeId, replace ?? false), new()
        {
            Name = "library_add", Destructive = true,
            Description = "把节点里的图片、视频或音频加入当前画布的资料库，文件名用节点标题。同名文件已存在时报错并提示用 replace；replace=true 会覆盖它（旧文件进回收站）。资料库操作不在画布撤销记录里，不能撤销。",
        }),
        McpServerTool.Create((
            [Description("资料库文件名，取自 library_list 的 name")] string name,
            [Description("新的文件名，不含扩展名（扩展名保持不变）")] string newName) => LibraryAsync(host, projectId => Json(new { Renamed = Library(() => LibraryStore.Rename(projectId, name, newName)) })), new()
        {
            Name = "library_rename", Destructive = true,
            Description = "给当前画布资料库里的文件改名，只改主文件名、扩展名不变；新名字已存在、含非法字符或是 Windows 设备名时报错。画布上已经放出去的节点用的是复制品，不受影响。不能撤销。",
        }),
        McpServerTool.Create((
            [Description("资料库文件名，取自 library_list 的 name")] List<string> names) => LibraryAsync(host, projectId =>
            {
                var (removed, failed) = LibraryStore.RemoveNames(projectId, names);
                return Json(new { Removed = removed, Failed = failed.Select(f => new { f.Name, f.Reason }).ToList() });
            }), new()
        {
            Name = "library_remove", Destructive = true,
            Description = "把当前画布资料库里的文件移到回收站，返回 removed 和 failed: [{name, reason}]。只在用户明确要求删除时使用；不能在画布里撤销，只能从回收站找回。画布上已经放出去的节点不受影响。",
        }),
    ];

    /// <summary>
    /// Library errors are the agent's to read. The work runs on the UI thread: it raises LibraryStore.Changed, which the open library panel answers by
    /// updating its list, and the canvas is read once there so a canvas switched meanwhile cannot change which library is touched.
    /// </summary>
    private static Task<string> LibraryAsync(ICanvasHost host, Func<string, string> action) => host.OnUiAsync(() => Library(() => action(host.Document.Id)));

    private static T Library<T>(Func<T> action)
    {
        try { return action(); }
        catch (LibraryException ex) { throw new McpException(ex.Message); }
    }

    private static string Library(string projectId, string? kind, string? keyword)
    {
        var items = LibraryStore.List(projectId)
            .Where(i => kind is null || i.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Where(i => string.IsNullOrWhiteSpace(keyword) || i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(200)
            .Select(i => new { Name = Path.GetFileName(i.Path), Kind = i.Kind.ToString().ToLowerInvariant(), AddedAt = i.AddedAt.ToString("yyyy-MM-dd HH:mm") });
        return Json(new { Items = items });
    }

    private static async Task<string> PlaceAsync(ICanvasHost host, List<string> names, double? x, double? y)
    {
        ValidateGeometry(x, y);
        var canvas = await host.OnUiAsync(() => host.Document);
        var paths = names.Select(name => Library(() => LibraryStore.Find(canvas.Id, name))?.Path
            ?? throw new McpException($"资料库里没有「{name}」，请先用 library_list 查看文件名")).ToList();
        var result = await ImportAsync(host, canvas, paths, x, y);
        if (result.Ids.Count == 0 && result.Failed.Count > 0) throw new McpException($"文件放不上画布：{string.Join("；", result.Failed.Select(f => $"{Path.GetFileName(f.Path)}（{f.Reason}）"))}");
        return Json(new { NodeIds = result.Ids, Failed = result.Failed.Count > 0 ? result.Failed : null });
    }

    private static async Task<ImportResult> ImportAsync(ICanvasHost host, CanvasDocument canvas, List<string> paths, double? x, double? y) => await await host.OnUiAsync(() =>
    {
        if (!ReferenceEquals(canvas, host.Document)) throw new McpException("画布已切换，请重新读取当前画布后再操作");
        return host.ImportAsync(paths, x ?? host.ViewCenter.X, y ?? host.ViewCenter.Y);
    });

    /// <summary>Paths the agent gave are checked here, so a bad one is reported in failed instead of reaching the host.</summary>
    private static async Task<string> ImportFilesAsync(ICanvasHost host, List<string> paths, double? x, double? y)
    {
        ValidateGeometry(x, y);
        if (paths.Count == 0) throw new McpException("paths 不能为空");
        var canvas = await host.OnUiAsync(() => host.Document);
        var failed = paths.Select(path => (Path: path, Reason: Media.ImportProblem(path))).Where(p => p.Reason is not null).Select(p => new ImportFailure(p.Path, p.Reason!)).ToList();
        // What was checked is what is imported: the normalised path, never the text as the agent wrote it.
        var valid = paths.Where(path => failed.All(f => f.Path != path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = valid.Count > 0 ? await ImportAsync(host, canvas, valid, x, y) : new ImportResult([], []);
        failed.AddRange(result.Failed);
        if (result.Ids.Count == 0 && failed.Count > 0) throw new McpException($"没有文件被导入：{string.Join("；", failed.Select(f => $"{f.Path}（{f.Reason}）"))}");
        return Json(new { NodeIds = result.Ids, Failed = failed.Count > 0 ? failed : null });
    }

    private static async Task<string> ReplaceMediaAsync(ICanvasHost host, string nodeId, string path)
    {
        if (Media.ImportProblem(path) is { } problem) throw new McpException($"{path}：{problem}");
        // What was checked is what is used: the normalised path, never the text as the agent wrote it.
        path = Path.GetFullPath(path);
        var (canvas, node) = await host.OnUiAsync(() =>
        {
            var node = Find(host, nodeId);
            if (node.Kind is not (NodeKind.Image or NodeKind.Video or NodeKind.Audio)) throw new McpException("只有图片、视频、音频节点可以替换内容");
            if (Media.KindOf(path) != node.Kind) throw new McpException($"文件类型和节点不一致：节点是{node.Kind.ToString().ToLowerInvariant()}，文件是{Media.KindOf(path)!.Value.ToString().ToLowerInvariant()}");
            if (node.Status == NodeStatus.Loading || host.IsRunning(node.Id)) throw new McpException("节点正在生成，请等它结束或先停止后再替换");
            return (host.Document, node);
        });
        try
        {
            await await host.OnUiAsync(() =>
            {
                if (!ReferenceEquals(canvas, host.Document) || host.Document.Find(node.Id) != node) throw new McpException("画布已切换或节点已被删除，请重新读取当前画布");
                return host.ReplaceMediaAsync(node, path);
            });
        }
        // A file that cannot be copied (held open, no permission) is the agent's to read, like a failed import.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new McpException($"文件复制失败：{ex.Message}"); }
        return Json(new { NodeId = node.Id, File = node.Content, Note = "节点上的提示词和模型参数没有清除，重新生成会盖掉替换进去的文件；旧文件仍在画布文件夹里，可以撤销。" });
    }

    private static async Task<string> AddToLibraryAsync(ICanvasHost host, string nodeId, bool replace)
    {
        var work = await host.OnUiAsync(() =>
        {
            var node = Find(host, nodeId);
            if (node.Kind is not (NodeKind.Image or NodeKind.Video or NodeKind.Audio) || !node.HasContent) throw new McpException("只有带图片、视频或音频的节点可以加入资料库");
            return host.AddToLibraryAsync(node, replace);
        });
        try { return Json(new { Added = await work }); }
        catch (LibraryException ex) { throw new McpException(ex.Message); }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Expected cost of an image run from this node, worked out as the generation panel does it (same inputs, same prices); or why there is none.</summary>
    private static (object? Cost, string? Note) Estimate(CanvasDocument doc, CanvasNode node)
    {
        // A retry of an empty output runs the node that produced it, for one picture.
        if (!node.HasContent && string.IsNullOrWhiteSpace(node.Prompt) && node.Option(NodeKeys.SourceId) is { Length: > 0 } sourceId && doc.Find(sourceId) is { } origin && origin != node)
        {
            node = origin.Clone();
            node.Count = 1;
        }
        if (NodeSpec.CapabilityOf(node) != Capability.Image) return (null, "只估算图片生成的费用，文本和语音暂不估价");
        var settings = SettingsStore.Current;
        var model = settings.ModelFor(Capability.Image, node.Model);
        if (settings.Resolve(model) is not { } resolved) return (null, "还没有可用的图片模型，无法估价");
        var references = CanvasGraph.GenerationInputs(doc, node).Count(r => r.Type == NodeKind.Image);
        return Prices.EstimateImage(resolved.Channel, resolved.Model.Name, node, references) is { } estimate
            ? (new { estimate.Amount, estimate.Currency, estimate.Basis }, null)
            : (null, "这个模型和设置还没有官方单价，生成一次后才能按实际用量估算");
    }

    /// <summary>The key stays in the result as null when there is no estimate, so the agent can tell "unknown" from "not reported".</summary>
    private static Dictionary<string, object?> WithCost(Dictionary<string, object?> result, (object? Cost, string? Note) cost)
    {
        result["estimatedCost"] = cost.Cost;
        if (cost.Cost is null) result["estimatedCostNote"] = cost.Note;
        return result;
    }

    private static string Usage(string? since)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = since?.Trim().ToLowerInvariant() switch
        {
            null or "" or "today" => today,
            "month" => new DateOnly(today.Year, today.Month, 1),
            var text when DateOnly.TryParseExact(text, "yyyy-MM-dd", out var day) => day,
            var text => throw new McpException($"since 只能是 yyyy-MM-dd、today 或 month，收到「{text}」"),
        };
        return Json(new { Since = from.ToString("yyyy-MM-dd"), Note = "本机账本的估算，不是服务商的账单；其他程序用同一个 Key 的花费看不到。", Currencies = UsageReport.Summarize(SettingsStore.Current, from) });
    }

    private static CanvasNode Find(ICanvasHost host, string? id) =>
        id is not null && host.Document.Find(id) is { } node ? node : throw new McpException($"找不到节点「{id}」，请先用 canvas_get_state 获取节点 ID");

    /// <summary>The nodes for these IDs, in the order given; every unknown ID is named in one error.</summary>
    private static List<CanvasNode> FindAll(ICanvasHost host, IEnumerable<string> ids)
    {
        var missing = ids.Where(id => host.Document.Find(id) is null).ToList();
        if (missing.Count > 0) throw new McpException($"找不到节点「{string.Join("、", missing)}」，请先用 canvas_get_state 获取节点 ID");
        return ids.Select(id => host.Document.Find(id)!).ToList();
    }

    /// <summary>Core's checks of a pick are written as argument errors; the agent reads them as tool errors.</summary>
    private static T Pick<T>(Func<T> action)
    {
        try { return action(); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }

    private static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "…";

    private static string? FileOf(CanvasNode n) => n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio && n.HasContent ? n.Content : null;

    /// <summary>What an overview needs: no prompts, text or generation settings, which repeat across a batch and dominate the payload.</summary>
    private static object Brief(ICanvasHost host, CanvasNode n) => new
    {
        n.Id,
        Kind = n.Kind.ToString().ToLowerInvariant(),
        n.Title,
        X = Math.Round(n.X), Y = Math.Round(n.Y), Width = Math.Round(n.Width), Height = Math.Round(n.Height),
        Status = n.Status.ToString().ToLowerInvariant(),
        Running = host.IsRunning(n.Id) ? true : (bool?)null,
        n.Error,
        File = FileOf(n),
        n.GroupId,
        Stack = Stacks.Of(n),
        StackOpen = Stacks.IsOpen(n) ? true : (bool?)null,
    };

    private static object Full(ICanvasHost host, CanvasNode n) => new
    {
        n.Id,
        Kind = n.Kind.ToString().ToLowerInvariant(),
        n.Title,
        X = Math.Round(n.X), Y = Math.Round(n.Y), Width = Math.Round(n.Width), Height = Math.Round(n.Height),
        Status = n.Status.ToString().ToLowerInvariant(),
        Running = host.IsRunning(n.Id) ? true : (bool?)null,
        n.Error,
        Text = n.Kind == NodeKind.Text ? Clip(n.Content, 4000) : null,
        Media = n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio && n.HasContent ? new { File = n.Content, Width = n.NaturalWidth > 0 ? n.NaturalWidth : (int?)null, Height = n.NaturalHeight > 0 ? n.NaturalHeight : (int?)null } : null,
        Prompt = Clip(n.Prompt, 2000),
        Mode = n.Kind == NodeKind.Config ? n.Mode.ToString().ToLowerInvariant() : null,
        n.Model, n.Ratio, n.Size, n.Quality,
        Count = n.Count > 1 ? n.Count : (int?)null,
        Options = n.Options.Count > 0 ? n.Options : null,
        n.GroupId,
    };

    private static string State(ICanvasHost host, string? detail, List<string>? ids, string? groupId, List<double>? rect)
    {
        if (detail is not (null or "brief" or "full")) throw new McpException($"detail 只能是 brief 或 full，收到「{detail}」");
        var doc = host.Document;
        IEnumerable<CanvasNode> matching = doc.Nodes;
        if (ids is not null)
        {
            var wanted = FindAll(host, ids).ToHashSet();
            matching = matching.Where(wanted.Contains);
        }
        if (groupId is not null)
        {
            var group = Find(host, groupId);
            if (group.Kind != NodeKind.Group) throw new McpException($"「{groupId}」不是分组节点");
            var wanted = CanvasGraph.GroupMembers(doc, groupId).Append(group).ToHashSet();
            matching = matching.Where(wanted.Contains);
        }
        if (rect is not null)
        {
            if (rect.Count != 4 || rect.Any(v => !double.IsFinite(v)) || rect[2] <= rect[0] || rect[3] <= rect[1]) throw new McpException("rect 必须是 4 个有限数 [left, top, right, bottom]，且 right > left、bottom > top");
            var area = new Rect(rect[0], rect[1], rect[2], rect[3]);
            matching = matching.Where(n => area.Intersects(Rect.Of(n)));
        }
        var nodes = matching.ToList();
        var kept = nodes.Select(n => n.Id).ToHashSet();
        var settings = SettingsStore.Current;
        object Models(Capability capability) => settings.ModelsFor(capability).Select(id => new { Id = id, Label = settings.Label(id) }).ToList();
        return Json(new
        {
            Canvas = new { doc.Id, doc.Title },
            Viewport = new { host.Viewport.X, host.Viewport.Y, Zoom = host.Viewport.K, Center = new { host.ViewCenter.X, host.ViewCenter.Y } },
            Selection = host.Selection,
            Nodes = nodes.Select(n => detail == "full" ? Full(host, n) : Brief(host, n)),
            Connections = doc.Connections.Where(c => kept.Contains(c.From) && kept.Contains(c.To)).Select(c => new { c.From, c.To }),
            Models = new { Image = Models(Capability.Image), Text = Models(Capability.Text), Audio = Models(Capability.Audio) },
        });
    }

    private static NodeKind ParseKind(string? kind) =>
        Enum.TryParse<NodeKind>(kind, true, out var parsed) && Enum.IsDefined(parsed) && parsed != NodeKind.Group ? parsed : throw new McpException($"不支持的节点类型「{kind}」，可用 text、image、video、audio、config");

    private static Capability ParseMode(string? mode) =>
        Enum.TryParse<Capability>(mode, true, out var parsed) && Enum.IsDefined(parsed) && parsed != Capability.Video ? parsed : throw new McpException($"不支持的生成类型「{mode}」，可用 image、text、audio（这个版本不支持生成视频）");

    private static void ValidateModel(string? model)
    {
        if (model is not null && SettingsStore.Current.Resolve(model) is null) throw new McpException($"未知模型「{model}」，请使用 canvas_get_state.models 里的 ID");
    }

    private static void ValidateGeometry(double? x, double? y, double? width = null, double? height = null)
    {
        if (new[] { x, y, width, height }.Any(v => v is { } n && !double.IsFinite(n))) throw new McpException("坐标和尺寸必须是有限数值");
        if (width <= 0 || height <= 0) throw new McpException("节点宽度和高度必须大于 0");
    }

    private static void ValidateLayout(string? layout, double? gap)
    {
        if (layout is not null && Stacks.ParseLayout(layout) is null) throw new McpException($"layout 只能是 stack、row 或 grid，收到「{layout}」");
        if (gap is { } g && (!double.IsFinite(g) || g < 0)) throw new McpException("gap 必须是不小于 0 的有限数值");
    }

    /// <summary>Remember how the next batch from this node is laid out; the generator applies it when it creates the outputs.</summary>
    private static void SetLayout(CanvasNode node, string? layout, double? gap)
    {
        if (layout is not null) node.SetOption(NodeKeys.Layout, Stacks.ParseLayout(layout) is StackLayout.Stack ? null : layout.Trim().ToLowerInvariant());
        if (gap is { } g) node.SetOption(NodeKeys.Gap, g.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void ValidateNodeOp(CanvasOp op, NodeKind kind)
    {
        ValidateGeometry(op.X, op.Y, op.Width, op.Height);
        ValidateModel(op.Model);
        if (op.Mode is not null) _ = ParseMode(op.Mode);
        if (op.Text is not null && kind != NodeKind.Text) throw new McpException("只有文本节点可以设置 text");
    }

    /// <summary>Copy the generation fields an op or tool call sets; unknown model IDs are rejected instead of silently falling back.</summary>
    private static void SetParams(CanvasNode node, string? prompt, string? model, string? ratio, string? size, string? quality, int? count)
    {
        ValidateModel(model);
        if (prompt is not null) node.Prompt = prompt;
        if (model is not null) node.Model = model;
        if (ratio is not null) node.Ratio = ratio;
        if (size is not null) node.Size = size;
        if (quality is not null) node.Quality = quality;
        if (count is not null) node.Count = Math.Clamp(count.Value, 1, 15);
    }

    private static CanvasNode Place(ICanvasHost host, NodeKind kind, double? x, double? y)
    {
        var node = CanvasNode.Create(kind, host.ViewCenter.X, host.ViewCenter.Y);
        if (x is not null) node.X = x.Value;
        if (y is not null) node.Y = y.Value;
        if (x is null) node.X = CanvasGraph.FreeX(node, host.Document.Nodes);
        node.GroupId = CanvasGraph.ContainingGroup(host.Document, node)?.Id;
        host.Document.Nodes.Add(node);
        return node;
    }

    private static bool Connect(CanvasDocument doc, CanvasNode from, CanvasNode to)
    {
        if (CanvasGraph.NormalizeConnection(from, to, true) is not { } pair) return false;
        if (!doc.Connections.Any(c => c.From == pair.From && c.To == pair.To)) doc.Connections.Add(CanvasConnection.Create(pair.From, pair.To));
        return true;
    }

    /// <summary>The op's node IDs, all validated before anything changes: every one must exist and none may be a group (groups never nest).</summary>
    private static List<string> GroupMembersOf(ICanvasHost host, CanvasOp op, Func<string?, string> resolve)
    {
        var ids = (op.Ids ?? []).Select(resolve).ToList();
        if (ids.Count == 0) throw new McpException("ids 不能为空");
        if (FindAll(host, ids).Any(n => n.Kind == NodeKind.Group)) throw new McpException("分组不能嵌套，ids 里不能有分组节点");
        return ids;
    }

    public static string Apply(ICanvasHost host, IReadOnlyList<CanvasOp> ops)
    {
        var doc = host.Document;
        var refs = new Dictionary<string, string>();
        string Resolve(string? id) => id is not null && refs.TryGetValue(id, out var real) ? real : id ?? "";
        var results = new List<object>();
        var touched = new List<CanvasNode>();
        if (ops.Count > 0) host.RecordHistory();
        foreach (var (op, index) in ops.Select((op, index) => (op, index)))
        {
            try
            {
                switch (op.Type)
                {
                    case "add_node":
                        var kind = ParseKind(op.Kind);
                        ValidateNodeOp(op, kind);
                        ValidateLayout(op.Layout, op.Gap);
                        var node = Place(host, kind, op.X, op.Y);
                        if (op.Width is { } w) node.Width = w;
                        if (op.Height is { } h) node.Height = h;
                        if (op.Title is not null) node.Title = op.Title;
                        if (kind == NodeKind.Config) node.Mode = ParseMode(op.Mode ?? "image");
                        if (kind == NodeKind.Text && op.Text is not null) node.Content = op.Text;
                        SetParams(node, op.Prompt, op.Model, op.Ratio, op.Size, op.Quality, op.Count);
                        foreach (var (key, value) in op.Options ?? []) node.SetOption(key, value);
                        SetLayout(node, op.Layout, op.Gap);
                        if (op.Ref is not null) refs[op.Ref] = node.Id;
                        touched.Add(node);
                        results.Add(new { Index = index, node.Id, op.Ref });
                        break;
                    case "update_node":
                        var target = Find(host, Resolve(op.Id));
                        ValidateNodeOp(op, target.Kind);
                        if (op.Title is not null) target.Title = op.Title;
                        if (op.X is { } nx) target.X = nx;
                        if (op.Y is { } ny) target.Y = ny;
                        if (op.Width is { } nw) target.Width = nw;
                        if (op.Height is { } nh) target.Height = nh;
                        if (op.Text is not null)
                            target.Content = op.Text;
                        if (op.Mode is not null) target.Mode = ParseMode(op.Mode);
                        SetParams(target, op.Prompt, op.Model, op.Ratio, op.Size, op.Quality, op.Count);
                        foreach (var (key, value) in op.Options ?? []) target.SetOption(key, value);
                        touched.Add(target);
                        results.Add(new { Index = index, target.Id });
                        break;
                    case "delete_nodes":
                        var ids = (op.Ids ?? []).Select(Resolve).Where(id => doc.Find(id) is not null).ToList();
                        host.Delete(ids);
                        results.Add(new { Index = index, Deleted = ids });
                        break;
                    case "connect":
                        if (!Connect(doc, Find(host, Resolve(op.From)), Find(host, Resolve(op.To)))) throw new McpException("这两个节点不能这样连接（例如连到自己，或分组节点）");
                        results.Add(new { Index = index, Connected = new { From = Resolve(op.From), To = Resolve(op.To) } });
                        break;
                    case "disconnect":
                        var removed = doc.Connections.Where(c => c.From == Resolve(op.From) && c.To == Resolve(op.To)).ToList();
                        foreach (var connection in removed) doc.Connections.Remove(connection);
                        results.Add(new { Index = index, Removed = removed.Count });
                        break;
                    case "select":
                        host.Select((op.Ids ?? []).Select(Resolve).Where(id => doc.Find(id) is not null));
                        results.Add(new { Index = index, Selected = host.Selection });
                        break;
                    case "set_viewport":
                        ValidateGeometry(op.X, op.Y);
                        if (op.Zoom is { } requestedZoom && !double.IsFinite(requestedZoom)) throw new McpException("缩放倍数必须是有限数值");
                        var zoom = Math.Clamp(op.Zoom ?? host.Viewport.K, 0.05, 5);
                        var (cx, cy) = (op.X ?? host.ViewCenter.X, op.Y ?? host.ViewCenter.Y);
                        // Keep the screen position of the current centre while moving the requested world point onto it.
                        var screen = (X: host.ViewCenter.X * host.Viewport.K + host.Viewport.X, Y: host.ViewCenter.Y * host.Viewport.K + host.Viewport.Y);
                        host.Viewport = new Viewport(screen.X - cx * zoom, screen.Y - cy * zoom, zoom);
                        results.Add(new { Index = index, Viewport = new { host.Viewport.X, host.Viewport.Y, Zoom = host.Viewport.K } });
                        break;
                    case "run_generation":
                        results.Add(new { Index = index, Started = Run(host, Find(host, Resolve(op.Id)), recordHistory: false) });
                        break;
                    case "group":
                        var requested = (op.Ids ?? []).Select(Resolve).ToList();
                        var members = requested.Where(id => doc.Find(id) is not null).ToList();
                        if (members.Count < 2)
                        {
                            var gone = requested.Except(members).ToList();
                            throw new McpException("至少要有两个节点" + (gone.Count > 0 ? $"；这些节点已不存在（同批里被删除或解散的分组也算）：{string.Join("、", gone)}" : ""));
                        }
                        var groupId = host.Group(members, op.Title) ?? throw new McpException("这些节点已经都在同一个分组里，或少于两个可编组节点");
                        if (op.Ref is not null) refs[op.Ref] = groupId;
                        results.Add(new { Index = index, GroupId = groupId, op.Ref });
                        break;
                    case "group_add":
                        var groupTarget = Find(host, Resolve(op.Id));
                        if (groupTarget.Kind != NodeKind.Group) throw new McpException("group_add 的 id 必须是分组节点");
                        var added = host.AddToGroup(groupTarget.Id, GroupMembersOf(host, op, Resolve), op.Fit ?? true);
                        results.Add(new { Index = index, GroupId = groupTarget.Id, Added = added });
                        break;
                    case "group_remove":
                        results.Add(new { Index = index, Removed = host.RemoveFromGroup(GroupMembersOf(host, op, Resolve), op.Fit ?? true) });
                        break;
                    case "stack_open":
                    case "stack_close":
                        var stackNode = Find(host, Resolve(op.Id));
                        if (Stacks.Of(stackNode) is not { } stackId || Stacks.Members(doc, stackId) is not { Count: > 1 } stackMembers) throw new McpException("这个节点不在一批（摞）里，或这一批只有一个成员");
                        if (op.Type == "stack_close")
                        {
                            Stacks.Collapse(stackMembers);
                            // The cards under the lead are hidden now, so they leave the selection, as when the stack button closes it.
                            var kept = host.Selection.Where(s => stackMembers.All(m => m.Id != s) || s == stackMembers[0].Id).ToList();
                            if (kept.Count != host.Selection.Count) host.Select(kept);
                        }
                        else
                        {
                            ValidateLayout(op.Layout, op.Gap);
                            var layout = Stacks.ParseLayout(op.Layout) ?? StackLayout.Grid;
                            if (layout == StackLayout.Stack) throw new McpException("stack_open 的 layout 只能是 row 或 grid；要收起用 stack_close");
                            var positions = Stacks.Open(stackMembers, layout, op.Gap ?? Stacks.Gap);
                            for (var i = 0; i < stackMembers.Count; i++) (stackMembers[i].X, stackMembers[i].Y) = positions[i];
                        }
                        touched.AddRange(stackMembers);
                        results.Add(new { Index = index, StackId = stackId, Open = op.Type == "stack_open", Members = stackMembers.Select(m => m.Id).ToList() });
                        break;
                    case "keep_one":
                        var keptId = Resolve(op.Id);
                        var dropped = Pick(() => Picks.KeepOne(doc, keptId));
                        host.Delete(dropped);
                        touched.Add(doc.Find(keptId)!);
                        results.Add(new { Index = index, KeptId = keptId, Removed = dropped });
                        break;
                    case "promote":
                        var freshId = Resolve(op.Id);
                        var oldId = Resolve(op.Replaces);
                        object? library = null;
                        // The one step that cannot be undone goes first, once the rest is known to work: if it fails nothing has changed.
                        Pick(() => Picks.CheckPromote(doc, freshId, oldId));
                        if (op.Library == true && doc.Find(freshId) is { } finalPicture && doc.Find(oldId) is { } oldPicture)
                        {
                            var name = Library(() => LibraryStore.ReplaceByTitle(doc.Id, oldPicture.Title, ProjectStore.AssetPath(doc.Id, finalPicture.Content!)));
                            library = name is null ? new { Replaced = false, Note = "资料库里没有和被取代图片同名的文件" } : new { Replaced = true, File = name };
                        }
                        var replaced = Pick(() => Picks.Promote(doc, freshId, oldId));
                        host.Delete(replaced);
                        touched.Add(doc.Find(freshId)!);
                        results.Add(new { Index = index, NodeId = freshId, Replaced = replaced, Library = library });
                        break;
                    case "ungroup":
                        host.Ungroup((op.Ids ?? []).Select(Resolve).Where(id => doc.Find(id) is not null).ToList());
                        results.Add(new { Index = index, Ungrouped = true });
                        break;
                    default:
                        throw new McpException($"未知操作类型「{op.Type}」");
                }
            }
            catch (McpException ex)
            {
                results.Add(new { Index = index, Error = ex.Message });
            }
        }
        if (touched.Count > 0) host.Reveal(touched);
        return Json(new { Results = results });
    }

    private static string Generate(ICanvasHost host, string mode, string prompt, List<string>? references, string? model, string? ratio, string? size, string? quality, int? count, double? x, double? y, string? layout, double? gap, bool dryRun)
    {
        var capability = ParseMode(mode);
        ValidateGeometry(x, y);
        ValidateLayout(layout, gap);
        ValidateModel(model);
        var inputs = (references ?? []).Select(id => Find(host, id)).ToList();
        if (dryRun)
        {
            // The same checks and the same estimate on a node that never joins the canvas: no history, no node, no request.
            var probe = CanvasNode.Create(NodeSpec.KindOf(capability), 0, 0);
            SetParams(probe, prompt, model, ratio, size, quality, count);
            SetLayout(probe, layout, gap);
            var scratch = new CanvasDocument { Nodes = new(host.Document.Nodes.Append(probe)), Connections = new(host.Document.Connections) };
            foreach (var input in inputs) Connect(scratch, input, probe);
            var check = probe.Clone();
            // Only inputs that carry something count (an empty image or text node feeds nothing), as in the real call; the host checks the real canvas, which does not hold this scratch node.
            if (string.IsNullOrWhiteSpace(prompt) && CanvasGraph.GenerationInputs(scratch, probe).Count > 0) check.Prompt = "（参考节点）";
            return Json(WithCost(new Dictionary<string, object?> { ["dryRun"] = true, ["valid"] = true, ["cannotStart"] = host.CannotGenerate(check) }, Estimate(scratch, probe)));
        }
        host.RecordHistory();
        var node = Place(host, NodeSpec.KindOf(capability), x, y);
        if (x is null && inputs.Count > 0)
        {
            // Sit to the right of the inputs, like generation outputs do.
            node.X = inputs.Max(i => i.Right) + 80;
            node.Y = inputs.Min(i => i.Y);
            node.X = CanvasGraph.FreeX(node, host.Document.Nodes.Where(n => n != node));
        }
        try { SetParams(node, prompt, model, ratio, size, quality, count); SetLayout(node, layout, gap); }
        catch
        {
            host.Document.Nodes.Remove(node);
            throw;
        }
        if (x is null && Stacks.ParseLayout(layout) is { } spread and not StackLayout.Stack && node.Count > 1)
        {
            // A row or grid spreads right from this card: find a spot where all of it, not just one card, is free.
            var batch = new List<CanvasNode> { node };
            for (var i = 1; i < node.Count; i++)
            {
                var card = CanvasNode.Create(node.Kind, 0, 0);
                card.Width = node.Width;
                if (node.Kind == NodeKind.Image) card.Height = CanvasGraph.OutputHeight(node.Width, node.Ratio);
                batch.Add(card);
            }
            node.X = CanvasGraph.FreeBatchX(batch, spread, Stacks.GapOf(node), host.Document.Nodes.Where(n => n != node));
        }
        foreach (var input in inputs) Connect(host.Document, input, node);
        host.Reveal([node]);
        if (host.CannotGenerate(node) is { } reason) throw new McpException($"节点已创建（{node.Id}），但无法开始生成：{reason}");
        var cost = Estimate(host.Document, node);
        host.Generate(node, recordHistory: false);
        return Json(WithCost(new Dictionary<string, object?> { ["nodeId"] = node.Id, ["started"] = true, ["note"] = "生成是异步的，用 generation_get_status 查看结果；多张图会在旁边追加节点并从同样的输入连线，按 layout 叠成一摞或排成横排 / 网格。" }, cost));
    }

    private static string RunTool(ICanvasHost host, CanvasNode node)
    {
        // Worked out before the run starts: the run changes the node and its outputs.
        var cost = Estimate(host.Document, node);
        var started = Run(host, node);
        return Json(started ? WithCost(new Dictionary<string, object?> { ["started"] = true }, cost) : new Dictionary<string, object?> { ["started"] = false, ["note"] = "这个节点已经在生成中" });
    }

    private static bool Run(ICanvasHost host, CanvasNode node, bool recordHistory = true)
    {
        if (host.IsRunning(node.Id)) return false;
        if (host.CannotGenerate(node) is { } reason) throw new McpException(reason);
        host.Generate(node, recordHistory);
        return true;
    }

    private static string Status(ICanvasHost host, List<string>? ids)
    {
        var nodes = ids is { Count: > 0 }
            ? ids.Select(id => Find(host, id)).ToList()
            : host.Document.Nodes.Where(n => n.Status is NodeStatus.Loading or NodeStatus.Error || host.IsRunning(n.Id)).ToList();
        // Outputs of a node are listed too, so the agent finds the extra images of a multi-image run.
        return Json(new
        {
            Nodes = nodes.Select(n => new
            {
                Node = Brief(host, n),
                Batch = Stacks.Of(n) is { } stack ? Stacks.Members(host.Document, stack).Select(member => new { member.Id, Status = member.Status.ToString().ToLowerInvariant(), File = FileOf(member), member.Error }).ToList() : null,
                Outputs = host.Document.Connections.Where(c => c.From == n.Id).Select(c => host.Document.Find(c.To)).OfType<CanvasNode>()
                    .Where(o => o.Kind != NodeKind.Config).Select(o => new { o.Id, Status = o.Status.ToString().ToLowerInvariant(), o.Error, File = o.Content }),
            }),
        });
    }

    private static async Task<CallToolResult> GetImageAsync(ICanvasHost host, string nodeId)
    {
        var operation = await host.OnUiAsync(() =>
        {
            var node = Find(host, nodeId);
            return (Image: host.LoadImageAsync(node, 1024), Description: $"节点 {node.Id}「{node.Title}」，原图 {node.NaturalWidth}×{node.NaturalHeight}");
        });
        if (await operation.Image is not { } picture) throw new McpException("这个节点没有可查看的图片");
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(picture.Bytes, picture.MimeType),
                new TextContentBlock { Text = operation.Description },
            ],
        };
    }
}
