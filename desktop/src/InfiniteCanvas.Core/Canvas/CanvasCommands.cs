namespace InfiniteCanvas.Core.Canvas;

public enum CommandGroup { Canvas, Generate, View, Picture, Finish, Arrange, Edit, Delete }

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
        };
    }
}

/// <summary>One canvas action: its name, icon, shortcut, when it is offered and what it acts on. The menus, bars, tooltips and help all read this.</summary>
public sealed record CommandSpec(string Id, string Name, string Glyph, string? Shortcut, CommandGroup Group, CommandScope Scope, Func<CommandContext, bool> Visible, bool Menu = true, string? Detail = null, string? Label = null)
{
    /// <summary>The word on the command bar when it shows text: the short label, else the name.</summary>
    public string Short => Label ?? Name;

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
        C("new-config", "新建生成配置节点", "\uE945", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, "连接多个输入，统一生成", "生成配置"),
        C("import", "导入文件…", "\uE8E5", null, CommandGroup.Canvas, CommandScope.Selection, Always, false, "导入图片、视频或音频"),
        C("paste", "粘贴", "\uE77F", "Ctrl+V", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("select-all", "全选", "\uE8B3", "Ctrl+A", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("undo", "撤销", "\uE7A7", "Ctrl+Z", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("redo", "重做", "\uE7A6", "Ctrl+Shift+Z / Ctrl+Y", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("fit", "显示全部内容", "\uE9A6", "Ctrl+1", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("zoom-reset", "恢复 100%", "\uE71E", "Ctrl+0", CommandGroup.Canvas, CommandScope.Selection, Always, false),
        C("match-width", "同宽", "\uE746", null, CommandGroup.Canvas, CommandScope.Selection, c => c.Matchable, false, "宽度统一为基准节点（图片、视频保持比例）"),
        C("match-height", "同高", "\uE745", null, CommandGroup.Canvas, CommandScope.Selection, c => c.Matchable, false, "高度统一为基准节点（图片、视频保持比例）"),
        C("match-both", "同宽高", "\uE9A6", null, CommandGroup.Canvas, CommandScope.Selection, c => c.Matchable, false, "宽和高都统一为基准节点"),

        C("generate", "生成", "\uE768", "Ctrl+Enter", CommandGroup.Generate, CommandScope.Single, c => c.Single && c.CanGenerate && !c.Running && !c.Failed),
        C("retry", "重试", "\uE72C", "Ctrl+Enter", CommandGroup.Generate, CommandScope.Single, c => c.Single && c.CanGenerate && !c.Running && c.Failed),
        C("stop", "停止", "\uE71A", "Ctrl+Enter", CommandGroup.Generate, CommandScope.Single, c => c.Single && c.Running),

        C("view", "查看大图", "\uE7B3", null, CommandGroup.View, CommandScope.Single, c => c.Single && c.Kind == NodeKind.Image && c.HasContent, true, "双击图片也能打开"),

        C("mark", "局部编辑…", "\uE70F", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap && c.CanMark),
        C("crop", "裁剪…", "\uE7A8", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("split", "拆分…", "\uF0E2", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("angle", "多角度…", "\uE89E", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("upscale", "本地放大…", "\uE8A3", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),
        C("reverse", "反推提示词", "\uE8C1", null, CommandGroup.Picture, CommandScope.Single, c => c.Bitmap),

        C("promote", "设为定稿…", "\uE735", null, CommandGroup.Finish, CommandScope.Single, c => c.Single && c.Kind == NodeKind.Image && c.HasContent),
        C("keep-one", "只留这张…", "\uE73E", null, CommandGroup.Finish, CommandScope.Single, c => c.Single && c.InBatch),
        C("recompose", "按上次设置重新拼", "\uE72C", null, CommandGroup.Finish, CommandScope.Single, c => c.Single && c.Recomposable),
        C("library", "加入资料库", "\uE8F1", null, CommandGroup.Finish, CommandScope.Selection, c => c.Files > 0),
        C("save-as", "另存为…", "\uE74E", null, CommandGroup.Finish, CommandScope.Single, c => c.IsMedia),
        C("open", "用默认应用打开", "\uE8A7", null, CommandGroup.Finish, CommandScope.Single, c => c.IsMedia),
        C("reveal", "在资源管理器中显示", "\uE838", null, CommandGroup.Finish, CommandScope.Single, c => c.IsMedia),

        C("compare", "对比", "\uE8AB", "C", CommandGroup.Arrange, CommandScope.Selection, c => c.Pictures >= 2, true, "对比选中的 2 到 4 张图片"),
        C("compose", "拼版…", "\uE8B9", null, CommandGroup.Arrange, CommandScope.Selection, c => c.Pictures >= 2, true, "把选中的图片拼成一张"),
        C("group", "编组", "\uE799", "Ctrl+G", CommandGroup.Arrange, CommandScope.Selection, c => c.Groupable, true, "把选中节点放进一个分组"),
        C("ungroup", "取消编组", "\uE8E6", "Ctrl+Shift+G", CommandGroup.Arrange, CommandScope.Selection, c => c.Ungroupable),
        C("expand", "展开这一批", "\uE740", null, CommandGroup.Arrange, CommandScope.Single, c => c.Single && c.InBatch && !c.BatchOpen),
        C("collapse", "收起这一批", "\uE73F", null, CommandGroup.Arrange, CommandScope.Single, c => c.Single && c.InBatch && c.BatchOpen),

        C("edit", "编辑文本", "\uE70F", "Enter", CommandGroup.Edit, CommandScope.Single, c => c.Single && c.Kind == NodeKind.Text),
        C("copy", "复制", "\uE8C8", "Ctrl+C", CommandGroup.Edit, CommandScope.Selection, c => c.Copyable),
        C("duplicate", "创建副本", "\uE7C4", "Ctrl+D", CommandGroup.Edit, CommandScope.Selection, c => c.Duplicable > 0),
        C("rename", "重命名…", "\uE8AC", "F2", CommandGroup.Edit, CommandScope.Single, c => c.Single),
        C("free-resize", "自由缩放", "\uE9A6", null, CommandGroup.Edit, CommandScope.Selection, c => c.Resizable > 0, true, "按住 Shift 拖动时临时不锁比例"),
        C("clear-marks", "清除标注", "\uE894", null, CommandGroup.Edit, CommandScope.Single, c => c.Single && c.HasMarks),

        C("delete", "删除", "\uE74D", "Delete", CommandGroup.Delete, CommandScope.Selection, c => c.Count > 0),
    ];

    public static CommandSpec Find(string id) => All.First(c => c.Id == id);

    /// <summary>The commands the node menu offers for this selection, in menu order.</summary>
    public static List<CommandSpec> Offered(CommandContext context) => All.Where(c => c.Menu && c.Visible(context)).ToList();

    /// <summary>
    /// What the command bar shows for this selection, in order: command ids, "|" for a divider, and an id ending in "+" for a command that
    /// shows its text next to the icon (the first three or four of a state; the rest are icons with the name in the tooltip). A command the
    /// selection does not offer is skipped when the bar is drawn. Whatever the node menu offers and the bar does not goes under 更多.
    /// </summary>
    public static IReadOnlyList<string> Bar(CommandContext c)
    {
        // Nothing selected, nothing shown: nodes are created and files imported from the canvas menu (right-click, double-click).
        if (c.Count == 0) return [];
        if (c.Count > 1) return ["compare+", "compose+", "|", "match-width", "match-height", "match-both", "|", "group+", "ungroup+"];
        return c.Kind switch
        {
            NodeKind.Group => ["ungroup+", "rename+"],
            NodeKind.Text => ["edit+", "copy+"],
            NodeKind.Config => ["duplicate+", "rename+"],
            NodeKind.Video or NodeKind.Audio => c.HasContent ? ["open+", "library+", "reveal+"] : [],
            NodeKind.Image when !c.HasContent => ["stop+", "retry+"],
            NodeKind.Image when c.IsSvg => ["view+", "|", "library+", "open+"],
            NodeKind.Image when c.InBatch => ["expand+", "collapse+", "keep-one+", "|", "view", "mark", "crop", "split", "angle", "upscale", "reverse", "|", "library"],
            NodeKind.Image => ["view+", "|", "mark+", "crop+", "split+", "angle", "upscale", "reverse", "|", "library"],
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

    /// <summary>The node menu's commands that are not on the bar: the "更多" menu.</summary>
    public static List<CommandSpec> More(CommandContext c)
    {
        var onBar = BarItems(c).Where(i => i.Spec is not null).Select(i => i.Spec!.Id).ToHashSet();
        return Offered(c).Where(s => !onBar.Contains(s.Id)).ToList();
    }

    /// <summary>Mouse and keyboard gestures that are not commands: a title and how to do it.</summary>
    public static readonly IReadOnlyList<(string Title, string Text)> Gestures =
    [
        ("移动画布", "空白处拖动 · 按住空格或中键拖动 · 触控板双指滑动 · Shift+滚轮横向移动"),
        ("缩放", "滚轮 · 触控板双指捏合 · Ctrl+= / Ctrl+-"),
        ("选择", "单击 · Ctrl / Shift 单击加选 · Esc 取消选择"),
        ("框选", "按住 Shift 或 Ctrl 在空白处拖动"),
        ("连线", "拖动节点两侧的圆点"),
        ("新建", "双击空白处 · 从资料库拖入 · 右键空白处"),
        ("编辑", "Enter 进入提示词或文本 · Esc 回到画布"),
        ("导入", "把文件拖进窗口 · Ctrl+V 粘贴图片、文字或文件"),
        ("统一尺寸", "选中两个以上节点，用顶部命令栏的同宽 / 同高 / 同宽高，以第一个点选的节点为基准"),
        ("微调位置", "方向键移动 1 像素 · 按住 Shift 移动 10 像素"),
        ("对比", "选中 2 到 4 张图片后按 C 或点「对比」"),
        ("查看大图", "双击图片 · Ctrl+滚轮缩放 · Esc 关闭"),
        ("一批结果", "批量生成的结果叠成一摞，点击展开，点「收起」收回，被选中的那张做封面"),
        ("更多", "在节点或空白处点右键，或按菜单键 / Shift+F10"),
    ];

    /// <summary>Keys that belong to no command of the table.</summary>
    public static readonly IReadOnlyList<(string Title, string Text)> ExtraKeys =
    [
        ("全屏", "F11"),
        ("液态玻璃设置", "Ctrl+Shift+L"),
    ];

    /// <summary>Every command with a shortcut or gesture, then the extra keys: title and keys.</summary>
    public static List<(string Title, string Text)> Shortcuts() =>
        All.Where(c => c.Shortcut is not null).GroupBy(c => c.Shortcut!).Select(g => (string.Join(" / ", g.Select(c => c.Title)), g.Key)).Concat(ExtraKeys).ToList();
}
