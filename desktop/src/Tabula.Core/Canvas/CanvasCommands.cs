namespace Tabula.Core.Canvas;

public enum CommandGroup { Canvas, Generate, View, Picture, Finish, Arrange, Edit, Delete, Go }

/// <summary>Whether a command acts on the whole selection, or only makes sense for one node and is hidden when several are selected.</summary>
public enum CommandScope { Selection, Single }

/// <summary>
/// What the current selection looks like, as far as commands care. A collapsed batch counts as one node (its cover); the node-specific facts
/// are only filled when exactly one node is selected.
/// </summary>
public sealed record CommandContext
{
    /// <summary>Selected nodes that are shown: members hidden inside a collapsed batch do not count.</summary>
    public int Count { get; init; }
    public NodeKind? Kind { get; init; }
    public bool HasContent { get; init; }
    public bool IsSvg { get; init; }
    public bool CanMark { get; init; }
    public bool HasMarks { get; init; }
    public bool CanGenerate { get; init; }
    public bool Running { get; init; }
    public bool Failed { get; init; }
    public bool InBatch { get; init; }
    public bool BatchOpen { get; init; }
    public bool Recomposable { get; init; }
    /// <summary>The node was generated (it, or the node that made it, carries a prompt): its prompt and settings can be reused.</summary>
    public bool Reusable { get; init; }
    /// <summary>The composer holds something a 配方 could be saved from: a prompt or references, and it is not already bound to a node.</summary>
    public bool CanSaveRecipe { get; init; }
    /// <summary>Selected bitmap pictures a composition or comparison can take.</summary>
    public int Pictures { get; init; }
    public bool Groupable { get; init; }
    public bool Ungroupable { get; init; }
    /// <summary>Selected pictures, videos and audio that have a file.</summary>
    public int Files { get; init; }
    public int Duplicable { get; init; }
    public bool Copyable { get; init; }
    /// <summary>Selected pictures and videos, whose aspect ratio can be freed; <see cref="AllFree"/> when every one already is.</summary>
    public int Resizable { get; init; }
    public bool AllFree { get; init; }
    /// <summary>At least two selected nodes that are not groups: size matching has a reference node.</summary>
    public bool Matchable { get; init; }
    public bool CanUndo { get; init; }
    public bool CanRedo { get; init; }

    public bool Single => Count == 1;
    public bool IsMedia => Single && Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio && HasContent;
    public bool Bitmap => Single && Kind == NodeKind.Image && HasContent && !IsSvg;

    /// <summary>
    /// Read the selection. <paramref name="canMark"/>, <paramref name="canGenerate"/>, <paramref name="canGroup"/> and <paramref name="running"/>
    /// answer the questions that depend on settings or on the running canvas.
    /// </summary>
    public static CommandContext From(CanvasDocument doc, IReadOnlyCollection<string> selected, Func<CanvasNode, bool> canMark, Func<CanvasNode, bool> canGenerate,
        Func<IReadOnlyCollection<string>, bool> canGroup, Func<string, bool> running)
    {
        var hidden = Stacks.Hidden(doc);
        var shown = doc.Nodes.Where(n => selected.Contains(n.Id) && !hidden.Contains(n)).ToList();
        var all = doc.Nodes.Where(n => selected.Contains(n.Id)).ToList();
        var media = shown.Where(n => n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio && n.HasContent).ToList();
        var resizable = shown.Where(n => n.Kind is NodeKind.Image or NodeKind.Video).ToList();
        var context = new CommandContext
        {
            Count = shown.Count,
            Pictures = shown.Count(n => n.Kind == NodeKind.Image && n.HasContent && !Storage.Media.IsSvg(n.Content)),
            Groupable = shown.Count > 1 && canGroup(selected),
            Ungroupable = all.Any(n => n.Kind == NodeKind.Group || n.GroupId is not null),
            Files = media.Count,
            Duplicable = shown.Count(n => n.Kind != NodeKind.Group),
            Copyable = all.Any(n => n.HasContent && n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio or NodeKind.Text),
            Resizable = resizable.Count,
            AllFree = resizable.Count > 0 && resizable.All(n => n.FreeResize),
            Matchable = shown.Count(n => n.Kind != NodeKind.Group) >= 2,
        };
        if (shown.Count != 1) return context;
        var node = shown[0];
        var stack = Stacks.Of(node);
        return context with
        {
            Kind = node.Kind,
            HasContent = node.HasContent,
            IsSvg = Storage.Media.IsSvg(node.Content),
            CanMark = node.Kind == NodeKind.Image && node.HasContent && canMark(node),
            HasMarks = node.Kind == NodeKind.Image && node.HasContent && NodeKeys.HasMarks(node),
            CanGenerate = canGenerate(node),
            Running = running(node.Id),
            Failed = node.Status == NodeStatus.Error && !node.HasContent,
            InBatch = stack is not null && Stacks.Members(doc, stack).Count > 1,
            BatchOpen = Stacks.IsOpen(node),
            Recomposable = node.Kind == NodeKind.Image && node.Option(NodeKeys.Compose).Length > 0,
            Reusable = Composer.ReuseSource(doc, node) is not null,
        };
    }
}

/// <summary>One canvas action: its name, icon, shortcut, when it is offered and what it acts on. The menus, bars, tooltips and help all read this.</summary>
public sealed record CommandSpec(string Id, string Name, string Glyph, string? Shortcut, CommandGroup Group, CommandScope Scope, Func<CommandContext, bool> Visible, bool Menu = true, string? Detail = null, string? Label = null)
{
    /// <summary>The word on the command bar when it shows text: the short label, else the name without its "…".</summary>
    public string Short => Label ?? Title;

    /// <summary>The name without the "…" that marks a dialog; error dialogs use it as their title.</summary>
    public string Title => Name.TrimEnd('…');

    /// <summary>Name, shortcut and what it does, for tooltips.</summary>
    public string Tip => (Shortcut is null ? Name : $"{Name} ({Shortcut})") + (Detail is null ? "" : $"：{Detail}");
}

/// <summary>The command table: every canvas action once, in the order the menus and bars show them.</summary>
public static class CanvasCommands
{
    private static CommandSpec C(string id, string name, string glyph, string? shortcut, CommandGroup group, CommandScope scope, Func<CommandContext, bool> visible, bool menu = true, string? detail = null, string? label = null) =>
        new(id, name, glyph, shortcut, group, scope, visible, menu, detail, label);

    private static bool Always(CommandContext _) => true;

    public static readonly IReadOnlyList<CommandSpec> All =
    [
        // Canvas level: not offered in the node menu.
        C("new-text", "新建文本节点", "\uE8D2", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, null, "文本"),
        C("new-image", "新建图片节点", "\uEB9F", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, null, "图片"),
        C("new-video", "新建视频节点", "\uE714", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, null, "视频"),
        C("new-audio", "新建音频节点", "\uE8D6", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, null, "音频"),
        C("new-config", "新建配方节点", "\uE945", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, "连接多个输入，统一生成", "配方"),
        C("import", "导入文件…", "\uE8E5", "Ctrl+O", CommandGroup.Canvas, CommandScope.Selection, Always, false, "导入图片、视频或音频"),
        C("paste", "粘贴", "\uE77F", "Ctrl+V", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("select-all", "全选", "\uE8B3", "Ctrl+A", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("undo", "撤销", "\uE7A7", "Ctrl+Z", CommandGroup.Canvas, CommandScope.Selection, c => c.CanUndo, false),
        C("redo", "重做", "\uE7A6", "Ctrl+Shift+Z / Ctrl+Y", CommandGroup.Canvas, CommandScope.Selection, c => c.CanRedo, false),
        C("fit", "显示全部内容", "\uE9A6", "Ctrl+1", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("zoom-reset", "恢复 100%", "\uE71E", "Ctrl+0", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("match-width", "同宽", "\uE746", null, CommandGroup.Canvas, CommandScope.Selection, c => c.Matchable, false, "宽度统一为基准节点（图片、视频保持比例）"),
        C("match-height", "同高", "\uE745", null, CommandGroup.Canvas, CommandScope.Selection, c => c.Matchable, false, "高度统一为基准节点（图片、视频保持比例）"),
        C("match-both", "同宽高", "\uE9A6", null, CommandGroup.Canvas, CommandScope.Selection, c => c.Matchable, false, "宽和高都统一为基准节点"),

        C("generate", "生成", "\uE768", "Ctrl+Enter", CommandGroup.Generate, CommandScope.Single, c => c.Single && c.CanGenerate && !c.Running && !c.Failed),
        C("retry", "重试", "\uE72C", "Ctrl+Enter", CommandGroup.Generate, CommandScope.Single, c => c.Single && c.CanGenerate && !c.Running && c.Failed),
        C("stop", "停止", "\uE71A", "Ctrl+Enter", CommandGroup.Generate, CommandScope.Single, c => c.Single && c.Running),
        C("reuse", "沿用这次的设置", "\uE8EE", null, CommandGroup.Generate, CommandScope.Single, c => c.Single && c.Reusable, true, "把它的提示词、模型和参数填进指令栏", "沿用"),
        C("save-recipe", "存为配方", "\uE945", null, CommandGroup.Generate, CommandScope.Selection, c => c.CanSaveRecipe, false, "把指令栏里的提示词、参考和设置存成一个配方节点"),

        C("view", "查看大图", "\uE7B3", null, CommandGroup.View, CommandScope.Single, c => c.Single && c.Kind == NodeKind.Image && c.HasContent, true, "双击图片也能打开", "查看"),
        C("play", "播放", "\uE768", null, CommandGroup.View, CommandScope.Single, c => c.IsMedia && c.Kind is NodeKind.Video or NodeKind.Audio, true, "在卡片上播放或暂停"),

        C("mark", "局部编辑…", "\uE70F", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap && c.CanMark, true, null, "局部编辑"),
        C("crop", "裁剪…", "\uE7A8", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("split", "拆分…", "\uF0E2", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("angle", "多角度…", "\uE89E", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("upscale", "本地放大…", "\uE8A3", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("reverse", "反推提示词", "\uE8C1", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),

        C("promote", "设为定稿…", "\uE735", null, CommandGroup.Finish, CommandScope.Single, c => c.Single && c.Kind == NodeKind.Image && c.HasContent),
        C("keep-one", "只留这张…", "\uE73E", null, CommandGroup.Finish, CommandScope.Single, c => c.Single && c.InBatch),
        C("recompose", "按上次设置重新拼", "\uE72C", null, CommandGroup.Finish, CommandScope.Single, c => c.Single && c.Recomposable),
        C("library", "加入资料库", "\uE8F1", null, CommandGroup.Finish, CommandScope.Selection, c => c.Files > 0),
        C("save-as", "另存为…", "\uE74E", "Ctrl+S", CommandGroup.Finish, CommandScope.Single, c => c.IsMedia),
        C("open", "用默认应用打开", "\uE8A7", null, CommandGroup.Finish, CommandScope.Single, c => c.IsMedia),
        C("reveal", "在资源管理器中显示", "\uE838", null, CommandGroup.Finish, CommandScope.Single, c => c.IsMedia),
        C("replace", "替换文件…", "\uE895", null, CommandGroup.Finish, CommandScope.Single, c => c.IsMedia && !c.Running, true, "换成另一个文件，直接改这个节点，可以撤销"),

        C("compare", "对比", "\uE8AB", "C", CommandGroup.Arrange, CommandScope.Selection, c => c.Pictures >= 2, true, "对比选中的 2 到 4 张图片"),
        C("compose", "拼版…", "\uE8B9", null, CommandGroup.Arrange, CommandScope.Selection, c => c.Pictures >= 2, true, "把选中的图片拼成一张"),
        C("group", "编组", "\uE799", "Ctrl+G", CommandGroup.Arrange, CommandScope.Selection, c => c.Groupable, true, "把选中节点放进一个分组"),
        C("ungroup", "取消编组", "\uE8E6", "Ctrl+Shift+G", CommandGroup.Arrange, CommandScope.Selection, c => c.Ungroupable),
        C("expand", "展开这一批", "\uE740", null, CommandGroup.Arrange, CommandScope.Single, c => c.Single && c.InBatch && !c.BatchOpen, true, null, "展开"),
        C("collapse", "收起这一批", "\uE73F", null, CommandGroup.Arrange, CommandScope.Single, c => c.Single && c.InBatch && c.BatchOpen, true, null, "收起"),

        C("edit", "编辑文本", "\uE70F", "Enter", CommandGroup.Edit, CommandScope.Single, c => c.Single && c.Kind == NodeKind.Text),
        C("copy", "复制", "\uE8C8", "Ctrl+C", CommandGroup.Edit, CommandScope.Selection, c => c.Copyable),
        C("duplicate", "创建副本", "\uE7C4", "Ctrl+D", CommandGroup.Edit, CommandScope.Selection, c => c.Duplicable > 0),
        C("rename", "重命名…", "\uE8AC", "F2", CommandGroup.Edit, CommandScope.Single, c => c.Single),
        C("free-resize", "自由缩放", "\uE9A6", null, CommandGroup.Edit, CommandScope.Selection, c => c.Resizable > 0, true, "按住 Shift 拖动时临时不锁比例"),
        C("clear-marks", "清除标注", "\uE894", null, CommandGroup.Edit, CommandScope.Single, c => c.Single && c.HasMarks),

        C("delete", "删除", "\uE74D", "Delete", CommandGroup.Delete, CommandScope.Selection, c => c.Count > 0),

        // Places and panels of the page: not node commands, offered by the Ctrl+K palette (the page runs them).
        C("open-library", "资料库", "\uE8F1", null, CommandGroup.Go, CommandScope.Selection, Always, false, "资料库与画布文件"),
        C("open-agent", "助手", "\uE99A", null, CommandGroup.Go, CommandScope.Selection, Always, false, "让本机的 Agent 操作画布"),
        C("open-quota", "额度与用量", "\uE8C7", null, CommandGroup.Go, CommandScope.Selection, Always, false, "各服务商的余额和本机用量"),
        C("minimap", "小地图", "\uE826", null, CommandGroup.Go, CommandScope.Selection, Always, false, "显示或隐藏"),
        C("fullscreen", "全屏", "\uE740", "F11", CommandGroup.Go, CommandScope.Selection, Always, false, "进入或退出全屏"),
        C("help", "操作说明", "\uE9CE", null, CommandGroup.Go, CommandScope.Selection, Always, false),
        C("settings", "设置", "\uE713", null, CommandGroup.Go, CommandScope.Selection, Always, false),
        C("home", "返回画布列表", "\uE80F", null, CommandGroup.Go, CommandScope.Selection, Always, false),
    ];

    public static CommandSpec Find(string id) => All.First(c => c.Id == id);

    /// <summary>The commands the node menu offers for this selection, in menu order.</summary>
    public static List<CommandSpec> Offered(CommandContext context) => All.Where(c => c.Menu && c.Visible(context)).ToList();

    /// <summary>
    /// What the command bar shows for this selection, in order: command ids, "|" for a divider, and an id ending in "+" for a command that
    /// shows its text next to the icon (the first few of a state; the rest are icons with the name in the tooltip). A command the
    /// selection does not offer is skipped when the bar is drawn. ⋯ at the end opens the whole node menu.
    /// </summary>
    public static IReadOnlyList<string> Bar(CommandContext c)
    {
        // Nothing selected, nothing shown: nodes are created and files imported from the canvas menu (right-click, double-click).
        if (c.Count == 0) return [];
        if (c.Count > 1) return ["compare+", "compose+", "group+", "ungroup+", "|", "match-width", "match-height", "match-both"];
        // A running or failed node offers to stop or retry first; the rest is under ⋯.
        if (c.Kind != NodeKind.Group && (c.Running || c.Failed)) return ["stop+", "retry+", "reuse+"];
        return c.Kind switch
        {
            NodeKind.Group => ["ungroup+", "rename+"],
            NodeKind.Text => ["edit+", "copy+", "reuse+"],
            NodeKind.Config => ["duplicate+", "rename+"],
            NodeKind.Video or NodeKind.Audio => c.HasContent ? ["play+", "library+", "open+"] : [],
            NodeKind.Image when !c.HasContent => [],
            NodeKind.Image when c.IsSvg => ["view+"],
            // A collapsed batch leads with 展开 and the tools act on its cover; an open one leads with 收起.
            NodeKind.Image => ["expand+", "collapse+", "|", "view+", "mark+", "crop+", "split+", "|", "angle", "upscale", "reverse"],
            _ => [],
        };
    }

    /// <summary>The bar's commands for a selection with the ones the selection does not offer and the dividers left with nothing beside them removed; the flag says whether it shows text.</summary>
    public static List<(CommandSpec? Spec, bool Text)> BarItems(CommandContext c)
    {
        var items = new List<(CommandSpec? Spec, bool Text)>();
        foreach (var token in Bar(c))
        {
            if (token == "|") { if (items.Count > 0 && items[^1].Spec is not null) items.Add((null, false)); continue; }
            var text = token.EndsWith('+');
            var spec = Find(token.TrimEnd('+'));
            if (spec.Visible(c)) items.Add((spec, text));
        }
        if (items.Count > 0 && items[^1].Spec is null) items.RemoveAt(items.Count - 1);
        return items;
    }

    /// <summary>The word the palette shows for a command's group.</summary>
    public static string GroupName(CommandGroup group) => group switch
    {
        CommandGroup.Canvas => "画布", CommandGroup.Generate => "生成", CommandGroup.View => "查看", CommandGroup.Picture => "图片", CommandGroup.Finish => "收尾",
        CommandGroup.Arrange => "排列", CommandGroup.Edit => "编辑", CommandGroup.Delete => "删除", _ => "前往",
    };

    /// <summary>
    /// What the Ctrl+K palette lists: every command the selection allows (node commands first, then the canvas ones, then the places to go),
    /// narrowed to those whose name, group or description holds every word of <paramref name="query"/>. Names are the menu's names.
    /// </summary>
    public static List<CommandSpec> Palette(CommandContext c, string query = "")
    {
        var words = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return All.Where(s => s.Visible(c) && words.All(w => $"{s.Name} {GroupName(s.Group)} {s.Detail}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Group == CommandGroup.Go ? 2 : s.Group == CommandGroup.Canvas ? 1 : 0).ToList();
    }

    /// <summary>Mouse and keyboard gestures that are not commands: a title and how to do it.</summary>
    public static readonly IReadOnlyList<(string Title, string Text)> Gestures =
    [
        ("移动画布", "在空白处按住右键拖动 · 按住空格或中键拖动 · 触控板双指滑动 · Shift+滚轮横向移动"),
        ("缩放", "滚轮 · 触控板双指捏合 · Ctrl+= / Ctrl+-"),
        ("选择", "单击 · Ctrl / Shift 单击加选"),
        ("框选", "在空白处按住左键拖动 · 按住 Shift 或 Ctrl 拖动可加选"),
        ("连线", "拖动节点两侧的圆点"),
        ("新建", "双击空白处 · 从资料库拖入 · 右键空白处"),
        ("指令栏", "选中节点时自动出现；什么都没选时按 Enter 或 / 调出 · 在里面 Enter 生成、Shift+Enter 换行 · Esc 回到画布"),
        ("参考", "选中的图片和文本就是指令栏的参考（参考 1、参考 2…）；点芯片上的图钉固定，选中别的节点时仍保留；× 只是这次不用它，节点仍保持选中"),
        ("编辑文本", "选中文本节点后按 Enter 或双击"),
        ("导入", "把文件拖进窗口 · Ctrl+V 粘贴图片、文字或文件"),
        ("统一尺寸", "选中两个以上节点，用命令栏的同宽 / 同高 / 同宽高，以第一个点选的节点为基准"),
        ("微调位置", "方向键移动 1 像素 · 按住 Shift 移动 10 像素"),
        ("对比", "选中 2 到 4 张图片后按 C 或点「对比」"),
        ("查看大图", "双击图片 · Ctrl+滚轮缩放 · Esc 关闭"),
        ("一批结果", "批量生成的结果叠成一摞，点击展开，点「收起」收回，被选中的那张做封面；选中一张结果再写提示词，它就是下一轮的参考 1"),
        ("更多", "在节点或空白处点右键，或按菜单键 / Shift+F10；命令栏的 ⋯ 打开同一个菜单"),
        ("返回", "Esc 一级一级返回：菜单和命令面板、大图、输入框、最后取消选择"),
        ("删除", "删除后几秒内可以点「撤销」找回"),
    ];

    /// <summary>Keys that belong to no command of the table.</summary>
    public static readonly IReadOnlyList<(string Title, string Text)> ExtraKeys =
    [
        ("搜索命令", "Ctrl+K"),
        ("液态玻璃设置", "Ctrl+Shift+L"),
    ];

    /// <summary>Every command with a shortcut or gesture, then the extra keys: title and keys.</summary>
    public static List<(string Title, string Text)> Shortcuts() =>
        All.Where(c => c.Shortcut is not null).GroupBy(c => c.Shortcut!).Select(g => (string.Join(" / ", g.Select(c => c.Title)), g.Key)).Concat(ExtraKeys).ToList();
}
