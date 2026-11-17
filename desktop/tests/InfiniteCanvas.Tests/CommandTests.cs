using InfiniteCanvas.Core.Canvas;

namespace InfiniteCanvas.Tests;

/// <summary>The command table's visibility rules: what the node menu (and the bars built from the same table) offer for each kind of selection.</summary>
public class CommandTests
{
    private static CanvasNode Picture(string file = "图片-001.png") => new() { Kind = NodeKind.Image, Content = file, Status = NodeStatus.Success };

    private static List<string> Menu(CanvasDocument doc, bool canMark = false, bool running = false, params CanvasNode[] selected) =>
        CanvasCommands.Offered(CommandContext.From(doc, selected.Select(n => n.Id).ToList(), _ => canMark, n => n.Kind is not (NodeKind.Group or NodeKind.Video), _ => true, _ => running))
            .Select(c => c.Id).ToList();

    private static CanvasDocument Doc(params CanvasNode[] nodes)
    {
        var doc = new CanvasDocument();
        foreach (var node in nodes) doc.Nodes.Add(node);
        return doc;
    }

    [Fact]
    public void A_bitmap_picture_offers_the_whole_picture_toolkit_in_menu_order()
    {
        var picture = Picture();
        Assert.Equal(
            ["generate", "view", "mark", "crop", "split", "angle", "upscale", "reverse", "promote", "library", "save-as", "open", "reveal", "copy", "duplicate", "rename", "free-resize", "delete"],
            Menu(Doc(picture), true, false, picture));
    }

    [Fact]
    public void Local_edit_is_only_offered_when_the_model_supports_it()
    {
        var picture = Picture();
        Assert.DoesNotContain("mark", Menu(Doc(picture), false, false, picture));
        Assert.Contains("mark", Menu(Doc(picture), true, false, picture));
    }

    [Fact]
    public void An_svg_picture_has_no_pixel_tools()
    {
        var picture = Picture("图片-002.svg");
        var menu = Menu(Doc(picture), true, false, picture);
        Assert.Equal(["generate", "view", "promote", "library", "save-as", "open", "reveal", "copy", "duplicate", "rename", "free-resize", "delete"], menu);
    }

    [Fact]
    public void An_empty_picture_can_generate_but_has_nothing_to_view_or_open()
    {
        var empty = new CanvasNode { Kind = NodeKind.Image };
        Assert.Equal(["generate", "duplicate", "rename", "free-resize", "delete"], Menu(Doc(empty), true, false, empty));
    }

    [Fact]
    public void Generate_stop_and_retry_are_one_command_each_and_never_together()
    {
        var empty = new CanvasNode { Kind = NodeKind.Image };
        Assert.Contains("stop", Menu(Doc(empty), false, true, empty));
        Assert.DoesNotContain("generate", Menu(Doc(empty), false, true, empty));
        var failed = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Error };
        var menu = Menu(Doc(failed), false, false, failed);
        Assert.Contains("retry", menu);
        Assert.DoesNotContain("generate", menu);
        // A failure that still has a picture is a plain generate.
        var kept = new CanvasNode { Kind = NodeKind.Image, Content = "图片-003.png", Status = NodeStatus.Error };
        Assert.Contains("generate", Menu(Doc(kept), false, false, kept));
    }

    [Fact]
    public void Video_and_audio_get_the_media_commands_and_video_never_generates()
    {
        var video = new CanvasNode { Kind = NodeKind.Video, Content = "视频-001.mp4" };
        Assert.Equal(["library", "save-as", "open", "reveal", "copy", "duplicate", "rename", "free-resize", "delete"], Menu(Doc(video), false, false, video));
        var audio = new CanvasNode { Kind = NodeKind.Audio, Content = "音频-001.mp3" };
        Assert.Equal(["generate", "library", "save-as", "open", "reveal", "copy", "duplicate", "rename", "delete"], Menu(Doc(audio), false, false, audio));
    }

    [Fact]
    public void Text_copies_its_words_and_config_only_has_the_basics()
    {
        var text = new CanvasNode { Kind = NodeKind.Text, Content = "你好" };
        Assert.Equal(["generate", "copy", "duplicate", "rename", "delete"], Menu(Doc(text), false, false, text));
        var config = new CanvasNode { Kind = NodeKind.Config };
        Assert.Equal(["generate", "duplicate", "rename", "delete"], Menu(Doc(config), false, false, config));
    }

    [Fact]
    public void A_group_can_be_ungrouped_renamed_and_deleted_but_not_duplicated_twice_over()
    {
        var member = Picture();
        var group = new CanvasNode { Kind = NodeKind.Group, Title = "分组" };
        member.GroupId = group.Id;
        Assert.Equal(["ungroup", "rename", "delete"], Menu(Doc(group, member), false, false, group));
    }

    [Fact]
    public void A_collapsed_batch_acts_as_its_cover_and_offers_expand_and_keep_one()
    {
        var (cover, second, third) = (Picture("图片-001.png"), Picture("图片-002.png"), Picture("图片-003.png"));
        var doc = Doc(cover, second, third);
        Stacks.Form([cover, second, third]);
        var menu = Menu(doc, false, false, cover);
        Assert.Contains("expand", menu);
        Assert.DoesNotContain("collapse", menu);
        Assert.Contains("keep-one", menu);
        Assert.Contains("crop", menu);
        // Selecting every member (Ctrl+A) still counts the batch once, through its cover.
        var all = CommandContext.From(doc, [cover.Id, second.Id, third.Id], _ => false, _ => true, _ => true, _ => false);
        Assert.Equal(1, all.Count);
        Assert.True(all.Single);
        cover.SetOption(NodeKeys.StackOpen, "true");
        Assert.Contains("collapse", Menu(doc, false, false, cover));
    }

    [Fact]
    public void Several_nodes_hide_every_single_node_command_and_act_on_the_whole_selection()
    {
        var (a, b, text) = (Picture("图片-001.png"), Picture("图片-002.png"), new CanvasNode { Kind = NodeKind.Text, Content = "你好" });
        var doc = Doc(a, b, text);
        var menu = Menu(doc, true, false, a, b);
        Assert.Equal(new[] { "compare", "compose", "group", "copy", "duplicate", "free-resize", "library", "delete" }.Order(), menu.Order());
        var context = CommandContext.From(doc, [a.Id, b.Id, text.Id], _ => true, _ => true, _ => true, _ => false);
        Assert.Equal(3, context.Count);
        var offered = CanvasCommands.Offered(context);
        Assert.DoesNotContain(offered, c => c.Scope == CommandScope.Single);
        // Two pictures and a text node: still compare / compose the two pictures, group all three.
        Assert.Contains(offered, c => c.Id == "compare");
        Assert.Contains(offered, c => c.Id == "group");
    }

    [Fact]
    public void Compare_and_compose_need_two_bitmap_pictures_not_svg()
    {
        var (a, svg) = (Picture("图片-001.png"), Picture("图片-002.svg"));
        var menu = Menu(Doc(a, svg), false, false, a, svg);
        Assert.DoesNotContain("compare", menu);
        Assert.DoesNotContain("compose", menu);
    }

    [Fact]
    public void Clear_marks_shows_only_while_the_picture_has_marks()
    {
        var picture = Picture();
        Assert.DoesNotContain("clear-marks", Menu(Doc(picture), true, false, picture));
        picture.SetOption(NodeKeys.Mask, "蒙版-001.png");
        Assert.Contains("clear-marks", Menu(Doc(picture), false, false, picture));
    }

    [Fact]
    public void Recompose_needs_a_stored_composition()
    {
        var picture = Picture();
        Assert.DoesNotContain("recompose", Menu(Doc(picture), false, false, picture));
        picture.SetOption(NodeKeys.Compose, "{}");
        Assert.Contains("recompose", Menu(Doc(picture), false, false, picture));
    }

    [Fact]
    public void The_menu_follows_the_group_order_of_the_proposal()
    {
        var order = CanvasCommands.All.Where(c => c.Menu).Select(c => c.Group).Distinct().ToList();
        Assert.Equal(new[] { CommandGroup.Generate, CommandGroup.View, CommandGroup.Picture, CommandGroup.Finish, CommandGroup.Arrange, CommandGroup.Edit, CommandGroup.Delete }, order);
    }

    [Fact]
    public void Names_follow_the_glossary()
    {
        var names = CanvasCommands.All.ToDictionary(c => c.Id, c => c.Name);
        Assert.Equal("创建副本", names["duplicate"]);
        Assert.Equal("复制", names["copy"]);
        Assert.Equal("局部编辑…", names["mark"]);
        Assert.Equal("清除标注", names["clear-marks"]);
        Assert.Equal("只留这张…", names["keep-one"]);
        Assert.Equal("查看大图", names["view"]);
        Assert.Equal("对比", names["compare"]);
        Assert.Equal("停止", names["stop"]);
        // Every command that opens a dialog says so with an ellipsis, in its tooltip too.
        foreach (var id in new[] { "mark", "crop", "split", "angle", "upscale", "promote", "keep-one", "compose", "save-as", "rename", "import" })
            Assert.EndsWith("…", CanvasCommands.Find(id).Name);
        Assert.Equal(names.Count, names.Values.Distinct().Count());
        Assert.Equal("创建副本 (Ctrl+D)", CanvasCommands.Find("duplicate").Tip);
        Assert.Equal("Ctrl+C", CanvasCommands.Find("copy").Shortcut);
    }

    [Fact]
    public void The_help_lists_every_shortcut_of_the_table_and_the_ones_it_used_to_miss()
    {
        var text = string.Join("\n", CanvasCommands.Gestures.Select(g => g.Title + g.Text).Concat(CanvasCommands.Shortcuts().Select(s => s.Title + s.Text)));
        foreach (var command in CanvasCommands.All.Where(c => c.Shortcut is not null)) Assert.Contains(command.Shortcut!, text);
        foreach (var key in new[] { "Ctrl+0", "Ctrl+1", "Ctrl+Y", "Ctrl+Enter", "Ctrl+Shift+L", "Shift+滚轮", "按 C", "2 到 4 张" }) Assert.Contains(key, text);
    }

    [Fact]
    public void Ids_are_unique()
    {
        Assert.Equal(CanvasCommands.All.Count, CanvasCommands.All.Select(c => c.Id).Distinct().Count());
    }
}
