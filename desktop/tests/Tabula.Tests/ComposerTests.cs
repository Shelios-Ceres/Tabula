using Tabula.Core;
using Tabula.Core.Canvas;

namespace Tabula.Tests;

/// <summary>The composer's rules that live in Core: its mode per selection, the references (pins, drops, groups), where a new generation lands and how 沿用 / 存为配方 fill and save.</summary>
public class ComposerTests
{
    private static CanvasNode Picture(double x = 0, double y = 0, string file = "图片-001.png") =>
        new() { Kind = NodeKind.Image, Content = file, Status = NodeStatus.Success, X = x, Y = y, Width = 340, Height = 240 };

    private static CanvasNode Text(string content = "文字", double x = 0, double y = 0) => new() { Kind = NodeKind.Text, Content = content, X = x, Y = y, Width = 340, Height = 240 };

    private static CanvasDocument Doc(params CanvasNode[] nodes)
    {
        var doc = new CanvasDocument();
        foreach (var node in nodes) doc.Nodes.Add(node);
        return doc;
    }

    private static ComposerState State(CanvasDocument doc, IEnumerable<CanvasNode> selected, Func<string, bool>? running = null) =>
        Composer.StateOf(doc, selected.Select(n => n.Id), running ?? (_ => false), n => n.Kind is not (NodeKind.Group or NodeKind.Video));

    // ---------- Mode ----------

    [Fact]
    public void Nothing_or_pictures_and_text_make_a_new_generation()
    {
        var picture = Picture();
        var text = Text();
        var doc = Doc(picture, text);
        Assert.Equal(ComposerMode.New, State(doc, []).Mode);
        Assert.Equal(ComposerMode.New, State(doc, [picture]).Mode);
        Assert.Equal(ComposerMode.New, State(doc, [picture, text]).Mode);
    }

    [Fact]
    public void One_recipe_binds_the_composer_and_one_empty_node_is_filled()
    {
        var recipe = new CanvasNode { Kind = NodeKind.Config };
        var empty = new CanvasNode { Kind = NodeKind.Image };
        var audio = new CanvasNode { Kind = NodeKind.Audio };
        var doc = Doc(recipe, empty, audio);
        Assert.Equal((ComposerMode.Recipe, recipe), (State(doc, [recipe]).Mode, State(doc, [recipe]).Bound));
        Assert.Equal((ComposerMode.Fill, empty), (State(doc, [empty]).Mode, State(doc, [empty]).Bound));
        Assert.Equal(ComposerMode.Fill, State(doc, [audio]).Mode);
        // Two of them are not one node to bind to.
        Assert.Equal(ComposerMode.New, State(doc, [recipe, empty]).Mode);
        Assert.Null(State(doc, [recipe, empty]).Bound);
    }

    [Fact]
    public void Running_nodes_are_stopped_before_anything_else_and_failed_ones_retried()
    {
        var recipe = new CanvasNode { Kind = NodeKind.Config };
        var busy = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Loading };
        var failed = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Error, Error = "出错了" };
        var stopped = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Error, Error = CanvasGeneratorStopped };
        var doc = Doc(recipe, busy, failed, stopped);
        var stop = State(doc, [recipe, busy, failed], id => id == busy.Id);
        Assert.Equal(ComposerMode.Stop, stop.Mode);
        Assert.Equal([busy], stop.Nodes);
        var retry = State(doc, [failed, stopped]);
        Assert.Equal(ComposerMode.Retry, retry.Mode);
        Assert.Equal([failed, stopped], retry.Nodes);
        // A failure that still holds a picture is not retried; the picture is just a reference.
        var kept = Picture();
        kept.Status = NodeStatus.Error;
        Assert.Equal(ComposerMode.New, State(Doc(kept), [kept]).Mode);
    }

    private const string CanvasGeneratorStopped = "已停止生成";

    [Fact]
    public void A_collapsed_batch_that_is_still_generating_offers_stop_from_its_cover()
    {
        var a = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Success, Content = "图片-001.png" };
        var b = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Loading };
        Stacks.Form([a, b]);
        var doc = Doc(a, b);
        var state = State(doc, [a], id => id == b.Id);
        Assert.Equal(ComposerMode.Stop, state.Mode);
        Assert.Equal([b], state.Nodes);
    }

    // ---------- References ----------

    private static List<string> Ids(IEnumerable<CanvasNode> nodes) => nodes.Select(n => n.Id).ToList();

    [Fact]
    public void Selected_pictures_and_texts_are_the_references_in_selection_order()
    {
        var a = Picture();
        var t = Text();
        var b = Picture(file: "图片-002.png");
        var video = new CanvasNode { Kind = NodeKind.Video, Content = "视频-001.mp4" };
        var emptyText = Text("  ");
        var emptyPicture = new CanvasNode { Kind = NodeKind.Image };
        var doc = Doc(a, t, b, video, emptyText, emptyPicture);
        var refs = Composer.References(doc, Ids([b, video, a, emptyText, emptyPicture, t]), [], []);
        Assert.Equal([b, a, t], refs);
    }

    [Fact]
    public void Pins_come_first_and_stay_while_other_nodes_are_selected()
    {
        var a = Picture();
        var b = Picture(file: "图片-002.png");
        var c = Picture(file: "图片-003.png");
        var doc = Doc(a, b, c);
        Assert.Equal([c, a], Composer.References(doc, [a.Id], [c.Id], []));
        // A pin that is also selected shows once, at its pinned place.
        Assert.Equal([b, a], Composer.References(doc, [a.Id, b.Id], [b.Id], []));
        // A pin whose node is gone is skipped.
        Assert.Equal([a], Composer.References(doc, [a.Id], ["node-gone"], []));
    }

    [Fact]
    public void Dropping_a_reference_leaves_it_out_without_deselecting_it()
    {
        var a = Picture();
        var b = Picture(file: "图片-002.png");
        var doc = Doc(a, b);
        Assert.Equal([b], Composer.References(doc, [a.Id, b.Id], [], [a.Id]));
        // Dropped also wins over a pin: × on a pinned chip removes it.
        Assert.Equal([b], Composer.References(doc, [b.Id], [a.Id], [a.Id]));
    }

    [Fact]
    public void A_selected_group_lends_its_pictures_and_texts_and_a_batch_gives_its_cover()
    {
        var group = new CanvasNode { Kind = NodeKind.Group };
        var inside = Picture();
        var note = Text("提示");
        var video = new CanvasNode { Kind = NodeKind.Video, Content = "视频-001.mp4" };
        foreach (var member in new[] { inside, note, video }) member.GroupId = group.Id;
        var cover = Picture(file: "图片-005.png");
        var hidden = Picture(file: "图片-006.png");
        Stacks.Form([cover, hidden]);
        var doc = Doc(group, inside, note, video, cover, hidden);
        Assert.Equal([inside, note], Composer.References(doc, [group.Id], [], []));
        // The hidden member of a collapsed batch never counts, not even when it is asked for directly.
        Assert.Equal([cover], Composer.References(doc, Ids([cover, hidden]), [], []));
    }

    [Fact]
    public void A_recipe_or_empty_node_shows_what_is_wired_into_it()
    {
        var a = Picture();
        var t = Text();
        var video = new CanvasNode { Kind = NodeKind.Video, Content = "视频-001.mp4" };
        var recipe = new CanvasNode { Kind = NodeKind.Config };
        var doc = Doc(a, t, video, recipe);
        foreach (var from in new[] { a, t, video }) doc.Connections.Add(CanvasConnection.Create(from.Id, recipe.Id));
        Assert.Equal([a, t], Composer.Wired(doc, recipe));
    }

    // ---------- A new generation ----------

    private static CanvasNode Draft(CanvasDocument doc, string prompt = "夏天的海边")
    {
        var draft = Composer.DraftOf(doc);
        draft.Prompt = prompt;
        draft.Model = "渠道::模型";
        draft.Count = 4;
        draft.Ratio = "16:9";
        draft.SetOption("background", "transparent");
        return draft;
    }

    [Fact]
    public void A_new_generation_is_an_empty_node_right_of_its_references_wired_from_each()
    {
        var a = Picture(100, 300);
        var b = Picture(600, 100, "图片-002.png");
        var doc = Doc(a, b);
        var node = Composer.NewGeneration(doc, Draft(doc), [a, b], 0, 0);
        Assert.Contains(node, doc.Nodes);
        Assert.Equal(NodeKind.Image, node.Kind);
        Assert.False(node.HasContent);
        Assert.Equal("夏天的海边", node.Prompt);
        Assert.Equal(("渠道::模型", 4, "16:9", "transparent"), (node.Model, node.Count, node.Ratio, node.Option("background")));
        Assert.True(node.X >= b.Right + 80);
        Assert.Equal(b.Y, node.Y);
        Assert.Equal([a.Id, b.Id], doc.Connections.Where(c => c.To == node.Id).Select(c => c.From).ToList());
    }

    [Fact]
    public void Without_references_the_node_lands_at_the_view_centre_and_never_inside_a_group()
    {
        var doc = new CanvasDocument();
        var node = Composer.NewGeneration(doc, Draft(doc), [], 1000, 500);
        Assert.Equal((1000 - node.Width / 2, 500 - node.Height / 2), (node.X, node.Y));
        Assert.Empty(doc.Connections);
        var group = new CanvasNode { Kind = NodeKind.Group, X = 800, Y = 300, Width = 800, Height = 500 };
        doc.Nodes.Add(group);
        var next = Composer.NewGeneration(doc, Draft(doc), [], 1000, 500);
        Assert.True(next.X >= group.Right + 80, $"{next.X}");
    }

    [Fact]
    public void The_draft_decides_the_output_kind_and_the_new_node_does_not_share_its_options()
    {
        var doc = new CanvasDocument();
        var draft = Draft(doc);
        draft.Mode = Capability.Text;
        var text = Composer.NewGeneration(doc, draft, [], 0, 0);
        Assert.Equal(NodeKind.Text, text.Kind);
        text.SetOption("background", "opaque");
        Assert.Equal("transparent", draft.Option("background"));
        draft.Mode = Capability.Audio;
        Assert.Equal(NodeKind.Audio, Composer.NewGeneration(doc, draft, [], 0, 0).Kind);
    }

    [Fact]
    public void A_new_picture_carries_the_marks_and_conversation_of_its_first_picture()
    {
        var note = Text("海边");
        var marked = Picture(file: "图片-004.png");
        marked.SetOption(NodeKeys.Mask, "蒙版-001.png");
        marked.SetOption(NodeKeys.ConversationId, "resp-1");
        marked.SetOption(NodeKeys.ConversationModel, "渠道::模型");
        var other = Picture(file: "图片-005.png");
        other.SetOption(NodeKeys.Mask, "蒙版-002.png");
        var doc = Doc(note, marked, other);
        // The text comes first but is not a picture: the first picture is the one the marks describe.
        var node = Composer.NewGeneration(doc, Draft(doc), [note, marked, other], 0, 0);
        Assert.Equal(("蒙版-001.png", "resp-1", "渠道::模型"), (node.Option(NodeKeys.Mask), node.Option(NodeKeys.ConversationId), node.Option(NodeKeys.ConversationModel)));
        // Only pictures take marks: text output and unmarked pictures carry nothing.
        var text = Draft(doc);
        text.Mode = Capability.Text;
        Assert.Equal("", Composer.NewGeneration(doc, text, [marked], 0, 0).Option(NodeKeys.Mask));
        Assert.Equal("", Composer.NewGeneration(doc, Draft(doc), [note], 0, 0).Option(NodeKeys.Mask));
    }

    [Fact]
    public void A_text_reference_feeds_the_prompt_of_the_new_node()
    {
        var note = Text("海边");
        var doc = Doc(note);
        var node = Composer.NewGeneration(doc, Draft(doc), [note], 0, 0);
        Assert.Equal(["海边"], CanvasGraph.GenerationInputs(doc, node).Select(r => r.Text).ToList());
    }

    [Fact]
    public void Save_recipe_makes_a_config_node_with_the_draft_and_the_wires()
    {
        var a = Picture();
        var doc = Doc(a);
        var draft = Draft(doc);
        draft.Mode = Capability.Text;
        var recipe = Composer.SaveRecipe(doc, draft, [a], 0, 0);
        Assert.Equal((NodeKind.Config, Capability.Text, "夏天的海边", 4), (recipe.Kind, recipe.Mode, recipe.Prompt, recipe.Count));
        Assert.Equal("配方", recipe.Title);
        Assert.True(recipe.X >= a.Right + 80);
        Assert.Equal([a.Id], doc.Connections.Where(c => c.To == recipe.Id).Select(c => c.From).ToList());
    }

    [Fact]
    public void The_draft_and_the_pins_are_saved_with_the_canvas()
    {
        var doc = new CanvasDocument();
        Draft(doc);
        doc.Pins.Add("node-a");
        var again = Json.Deserialize<CanvasDocument>(Json.Serialize(doc))!;
        Assert.Equal("夏天的海边", again.Draft!.Prompt);
        Assert.Equal(4, again.Draft.Count);
        Assert.Equal(["node-a"], again.Pins);
        // The draft is not a node: undo never sees it and the canvas never draws it.
        Assert.Empty(again.Nodes);
    }

    // ---------- 沿用 ----------

    [Fact]
    public void Reuse_reads_the_prompt_and_settings_of_a_generated_node_or_the_node_that_made_it()
    {
        var made = new CanvasNode { Kind = NodeKind.Image, Content = "图片-001.png", Prompt = "猫", Model = "渠道::模型", Ratio = "1:1", Count = 2 };
        made.SetOption("background", "transparent");
        made.SetOption(NodeKeys.Mask, "蒙版-001.png");
        var output = new CanvasNode { Kind = NodeKind.Image, Content = "图片-002.png" };
        output.SetOption(NodeKeys.SourceId, made.Id);
        var imported = Picture();
        var typed = Text("手写");
        var doc = Doc(made, output, imported, typed);
        Assert.Equal(made, Composer.ReuseSource(doc, made));
        Assert.Equal(made, Composer.ReuseSource(doc, output));
        Assert.Null(Composer.ReuseSource(doc, imported));
        Assert.Null(Composer.ReuseSource(doc, typed));

        var draft = Composer.DraftOf(doc);
        Composer.Reuse(draft, Composer.ReuseSource(doc, output)!);
        Assert.Equal(("猫", "渠道::模型", "1:1", 2, Capability.Image), (draft.Prompt, draft.Model, draft.Ratio, draft.Count, draft.Mode));
        Assert.Equal("transparent", draft.Option("background"));
        Assert.Equal("", draft.Option(NodeKeys.Mask));
    }

    [Fact]
    public void Reuse_is_offered_for_generated_text_and_for_running_or_failed_nodes_that_have_a_prompt()
    {
        var text = new CanvasNode { Kind = NodeKind.Text, Content = "结果", Prompt = "写一句话" };
        var typed = Text("手写");
        var failed = new CanvasNode { Kind = NodeKind.Image, Status = NodeStatus.Error, Prompt = "猫" };
        var doc = Doc(text, typed, failed);
        CommandContext On(CanvasNode node) => CommandContext.From(doc, [node.Id], _ => false, _ => true, _ => true, _ => false);
        Assert.Equal(["edit+", "copy+", "reuse+"], Bar(On(text)));
        Assert.Equal(["edit+", "copy+"], Bar(On(typed)));
        Assert.Equal(["retry+", "reuse+"], Bar(On(failed)));
        Assert.Contains("reuse", CanvasCommands.Palette(On(text)).Select(c => c.Id));
        Assert.DoesNotContain("reuse", CanvasCommands.Palette(On(typed)).Select(c => c.Id));
    }

    private static List<string> Bar(CommandContext context) => CanvasCommands.BarItems(context).Select(i => i.Spec is null ? "|" : i.Spec.Id + (i.Text ? "+" : "")).ToList();

    [Fact]
    public void Save_recipe_is_in_the_palette_only_when_the_composer_has_something_to_save()
    {
        var doc = new CanvasDocument();
        var none = CommandContext.From(doc, [], _ => false, _ => true, _ => true, _ => false);
        Assert.DoesNotContain("save-recipe", CanvasCommands.Palette(none).Select(c => c.Id));
        Assert.Contains("save-recipe", CanvasCommands.Palette(none with { CanSaveRecipe = true }).Select(c => c.Id));
        // It is not a node command: the node menu never lists it.
        Assert.DoesNotContain("save-recipe", CanvasCommands.Offered(none with { CanSaveRecipe = true }).Select(c => c.Id));
    }

    [Fact]
    public void The_config_node_is_called_recipe_in_the_interface()
    {
        Assert.Equal("配方", NodeSpec.Of(NodeKind.Config).Title);
        Assert.Equal("新建配方节点", CanvasCommands.Find("new-config").Name);
        Assert.DoesNotContain(CanvasCommands.All, c => c.Name.Contains("生成配置") || (c.Label ?? "").Contains("生成配置") || (c.Detail ?? "").Contains("生成配置"));
    }

    [Fact]
    public void Origin_reports_the_generating_node_even_without_a_prompt_and_ignores_imported_files()
    {
        var recipe = new CanvasNode { Kind = NodeKind.Config, Model = "渠道::gemini-2.1", Size = "2K", Ratio = "3:4" };
        var fromRecipe = Picture(file: "图片-001.png");
        fromRecipe.SetOption(NodeKeys.SourceId, recipe.Id);
        var modelOnly = Picture(file: "图片-002.png");
        modelOnly.Model = "渠道::seedream-5.5";
        var imported = Picture(file: "图片-003.png");
        var empty = new CanvasNode { Kind = NodeKind.Image, Model = "渠道::gemini-2.1", Prompt = "未完成" };
        var doc = Doc(recipe, fromRecipe, modelOnly, imported, empty);

        Assert.Same(recipe, Composer.OriginOf(doc, fromRecipe));
        Assert.Same(modelOnly, Composer.OriginOf(doc, modelOnly));
        Assert.Null(Composer.OriginOf(doc, imported));
        Assert.Null(Composer.OriginOf(doc, empty));
    }
}
