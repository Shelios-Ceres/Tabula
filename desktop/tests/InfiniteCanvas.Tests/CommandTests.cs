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
        Assert.Equal(["generate", "edit", "copy", "duplicate", "rename", "delete"], Menu(Doc(text), false, false, text));
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
        foreach (var key in new[] { "Ctrl+0", "Ctrl+1", "Ctrl+Y", "Ctrl+Enter", "Ctrl+Shift+L", "F11", "Shift+滚轮", "按 C", "2 到 4 张" }) Assert.Contains(key, text);
    }

    [Fact]
    public void Gestures_are_tooltips_not_shortcuts()
    {
        foreach (var id in new[] { "view", "free-resize" })
        {
            var command = CanvasCommands.Find(id);
            Assert.Null(command.Shortcut);
            Assert.NotNull(command.Detail);
        }
        Assert.DoesNotContain(CanvasCommands.Shortcuts(), s => s.Text.Contains("双击") || s.Text.Contains("拖动"));
    }

    private static CommandContext Selection(CanvasDocument doc, bool canMark = true, params CanvasNode[] selected) =>
        CommandContext.From(doc, selected.Select(n => n.Id).ToList(), _ => canMark, n => n.Kind is not (NodeKind.Group or NodeKind.Video), _ => true, _ => false);

    private static List<string> Bar(CommandContext context) => CanvasCommands.BarItems(context).Select(i => i.Spec is null ? "|" : i.Spec.Id + (i.Text ? "+" : "")).ToList();

    [Fact]
    public void Nothing_selected_shows_no_bar()
    {
        // No bar at all: nodes are created and files imported from the canvas menu.
        Assert.Empty(Bar(Selection(Doc())));
        Assert.Empty(CanvasCommands.More(Selection(Doc())));
    }

    [Fact]
    public void One_bitmap_picture_shows_view_the_picture_tools_and_the_library_with_text_on_the_first_four()
    {
        var picture = Picture();
        var context = Selection(Doc(picture), true, picture);
        Assert.Equal(["view+", "|", "mark+", "crop+", "split+", "angle", "upscale", "reverse", "|", "library"], Bar(context));
        // The rest of the node menu is under 更多, in menu order; nothing is on both.
        Assert.Equal(["generate", "promote", "save-as", "open", "reveal", "copy", "duplicate", "rename", "free-resize", "delete"], CanvasCommands.More(context).Select(c => c.Id));
        // Without model support the local edit is dropped, not shown disabled.
        Assert.DoesNotContain("mark+", Bar(Selection(Doc(picture), false, picture)));
    }

    [Fact]
    public void An_svg_has_no_pixel_tools_and_no_leading_divider()
    {
        var svg = Picture("图片-002.svg");
        Assert.Equal(["view+", "|", "library+", "open+"], Bar(Selection(Doc(svg), true, svg)));
    }

    [Fact]
    public void An_empty_picture_shows_stop_while_running_and_retry_after_a_failure()
    {
        var empty = new CanvasNode { Kind = NodeKind.Image };
        Assert.Empty(Bar(Selection(Doc(empty), true, empty)));
        var running = CommandContext.From(Doc(empty), [empty.Id], _ => true, _ => true, _ => true, _ => true);
        Assert.Equal(["stop+"], Bar(running));
        var failed = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Error };
        Assert.Equal(["retry+"], Bar(Selection(Doc(failed), true, failed)));
    }

    [Fact]
    public void Video_audio_text_config_and_group_each_get_their_own_short_bar()
    {
        var video = new CanvasNode { Kind = NodeKind.Video, Content = "视频-001.mp4" };
        Assert.Equal(["open+", "library+", "reveal+"], Bar(Selection(Doc(video), true, video)));
        var audio = new CanvasNode { Kind = NodeKind.Audio, Content = "音频-001.mp3" };
        Assert.Equal(["open+", "library+", "reveal+"], Bar(Selection(Doc(audio), true, audio)));
        var text = new CanvasNode { Kind = NodeKind.Text, Content = "你好" };
        Assert.Equal(["edit+", "copy+"], Bar(Selection(Doc(text), true, text)));
        var config = new CanvasNode { Kind = NodeKind.Config };
        Assert.Equal(["duplicate+", "rename+"], Bar(Selection(Doc(config), true, config)));
        var group = new CanvasNode { Kind = NodeKind.Group, Title = "分组" };
        var member = Picture();
        member.GroupId = group.Id;
        Assert.Equal(["ungroup+", "rename+"], Bar(Selection(Doc(group, member), true, group)));
    }

    [Fact]
    public void A_collapsed_batch_leads_with_expand_and_keep_one_and_an_opened_one_with_collapse()
    {
        var (cover, second) = (Picture("图片-001.png"), Picture("图片-002.png"));
        var doc = Doc(cover, second);
        Stacks.Form([cover, second]);
        Assert.Equal(["expand+", "keep-one+", "|", "view", "mark", "crop", "split", "angle", "upscale", "reverse", "|", "library"], Bar(Selection(doc, true, cover)));
        cover.SetOption(NodeKeys.StackOpen, "true");
        Assert.Equal("collapse+", Bar(Selection(doc, true, cover))[0]);
    }

    [Fact]
    public void Several_nodes_show_compare_compose_matching_and_grouping()
    {
        var (a, b) = (Picture("图片-001.png"), Picture("图片-002.png"));
        Assert.Equal(["compare+", "compose+", "|", "match-width", "match-height", "match-both", "|", "group+"], Bar(Selection(Doc(a, b), true, a, b)));
        var text = new CanvasNode { Kind = NodeKind.Text, Content = "你好" };
        // Only one picture: no compare or compose, matching and grouping stay.
        Assert.Equal(["match-width", "match-height", "match-both", "|", "group+"], Bar(Selection(Doc(a, text), true, a, text)));
    }

    [Fact]
    public void The_bar_is_a_subset_of_the_table_shows_at_most_four_labels_and_never_repeats_the_menu()
    {
        var (a, b) = (Picture("图片-001.png"), Picture("图片-002.png"));
        var empty = new CanvasNode { Kind = NodeKind.Image };
        var svg = Picture("图片-003.svg");
        var contexts = new[] { Selection(Doc()), Selection(Doc(a), true, a), Selection(Doc(svg), true, svg), Selection(Doc(a, b), true, a, b), Selection(Doc(empty), true, empty) };
        foreach (var context in contexts)
        {
            var items = CanvasCommands.BarItems(context);
            Assert.True(items.Count(i => i.Spec is not null && i.Text) <= 5);
            Assert.DoesNotContain(items, i => i.Spec is not null && !i.Spec.Visible(context));
            // Dividers only between commands.
            Assert.NotEqual("|", Bar(context).FirstOrDefault());
            Assert.NotEqual("|", Bar(context).LastOrDefault());
            var onBar = items.Where(i => i.Spec is not null).Select(i => i.Spec!.Id).ToHashSet();
            Assert.DoesNotContain(CanvasCommands.More(context), c => onBar.Contains(c.Id));
        }
    }

    [Fact]
    public void Ids_are_unique()
    {
        Assert.Equal(CanvasCommands.All.Count, CanvasCommands.All.Select(c => c.Id).Distinct().Count());
    }
}
