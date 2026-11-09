using System.Text;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;

namespace InfiniteCanvas.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Point ProjectStore.Root at a throwaway folder before any test runs, so no test touches the real data folder, and delete library files
    /// instead of recycling them, so a test run leaves nothing in the machine's Recycle Bin.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Isolate()
    {
        Environment.SetEnvironmentVariable("INFINITE_CANVAS_DATA", Path.Combine(Path.GetTempPath(), "InfiniteCanvas.Tests"));
        LibraryStore.Recycle = path =>
        {
            File.Delete(path);
            return true;
        };
    }
}

public class CoreTests
{
    private static byte[] Pixels(int width, int height, byte blue = 10, byte green = 20, byte red = 30, byte alpha = 255) =>
        Enumerable.Range(0, width * height).SelectMany(_ => new[] { blue, green, red, alpha }).ToArray();

    [Fact]
    public void Identical_pictures_differ_by_nothing_and_have_no_changed_box()
    {
        var result = ImageDifferenceMath.Measure(Pixels(4, 3), Pixels(4, 3), 4, 3);
        Assert.Equal((0d, 0d, 0d, null, 12), (result.MeanDifference, result.MaxDifference, result.ChangedFraction, result.ChangedBox, result.Compared));
        Assert.Equal(0.05, result.Threshold);
    }

    [Fact]
    public void One_changed_pixel_gives_its_box_and_the_exact_statistics()
    {
        var (a, b) = (Pixels(10, 5), Pixels(10, 5));
        var at = (3 + 2 * 10) * 4; // x = 3, y = 2
        b[at + 1] = (byte)(a[at + 1] + 51); // green up by 51 of 255 = 0.2
        var result = ImageDifferenceMath.Measure(a, b, 10, 5);
        Assert.Equal(0.2, result.MaxDifference, 6);
        Assert.Equal(0.2 / 3 / 50, result.MeanDifference, 9);
        Assert.Equal(1d / 50, result.ChangedFraction, 9);
        Assert.Equal(new UnitBox(0.3, 0.4, 0.1, 0.2), result.ChangedBox);

        // The threshold decides what counts as changed; the mean and the maximum do not depend on it.
        var strict = ImageDifferenceMath.Measure(a, b, 10, 5, 0.25);
        Assert.Equal((0d, null, 0.2), (strict.ChangedFraction, strict.ChangedBox, strict.MaxDifference));
        Assert.Equal(result.MeanDifference, strict.MeanDifference);
        Assert.Equal(0.25, strict.Threshold);

        // A region of changes: the box covers all of it.
        foreach (var (x, y) in new[] { (1, 1), (8, 3) }) b[(x + y * 10) * 4 + 2] = 255;
        var region = ImageDifferenceMath.Measure(a, b, 10, 5);
        Assert.Equal(new UnitBox(0.1, 0.2, 0.8, 0.6), region.ChangedBox);
        Assert.Equal(3d / 50, region.ChangedFraction, 9);
        Assert.Equal((255 - 30) / 255d, region.MaxDifference, 6);
    }

    [Fact]
    public void Pixels_the_second_picture_does_not_cover_are_left_out_and_bad_buffers_are_refused()
    {
        var a = Pixels(2, 2);
        var b = Pixels(2, 2);
        // The left column is not covered by B (transparent): only the right column is compared, and there B differs by 0.4 in blue.
        foreach (var x in new[] { 0 }) for (var y = 0; y < 2; y++) b[(x + y * 2) * 4 + 3] = 0;
        foreach (var y in new[] { 0, 1 }) b[(1 + y * 2) * 4] = (byte)(a[(1 + y * 2) * 4] + 102);
        var result = ImageDifferenceMath.Measure(a, b, 2, 2);
        Assert.Equal(2, result.Compared);
        Assert.Equal(0.4, result.MaxDifference, 6);
        Assert.Equal(1d, result.ChangedFraction);
        Assert.Equal(new UnitBox(0.5, 0, 0.5, 1), result.ChangedBox);

        var none = ImageDifferenceMath.Measure(a, Pixels(2, 2, alpha: 0), 2, 2);
        Assert.Equal((0, 0d, 0d, null), (none.Compared, none.MeanDifference, none.ChangedFraction, none.ChangedBox));
        Assert.Throws<ArgumentException>(() => ImageDifferenceMath.Measure(a, Pixels(2, 3), 2, 2));
        Assert.Throws<ArgumentException>(() => ImageDifferenceMath.Measure(a, a, 0, 2));
        Assert.Throws<ArgumentException>(() => ImageDifferenceMath.Measure(a, a, 3, 2));
    }

    private static ComposeSpec Spec(int count, string layout = "row", int columns = 0, string fit = "height", int gap = 64, int margin = 64, string background = "#FFFFFF") =>
        new(Enumerable.Range(0, count).Select(i => new ComposeCell($"n{i}")).ToList(), layout, columns, gap, margin, background, fit);

    [Fact]
    public void Compose_row_of_four_4K_portraits_is_8960_by_3968_without_scaling()
    {
        var result = ImageCompose.Layout(Spec(4), Enumerable.Repeat((2160, 3840), 4).ToList(), 0);
        Assert.Equal((8960, 3968, 1d), (result.Width, result.Height, result.Scale));
        Assert.Equal(2288, result.Cells[1].Cell.X);
        Assert.Equal([64, 2288, 4512, 6736], result.Cells.Select(c => c.Cell.X));
        Assert.All(result.Cells, c => Assert.Equal((64, 3840, 2160), (c.Cell.Y, c.Cell.Height, c.Cell.Width)));
        Assert.All(result.Cells, c => Assert.Equal((c.Cell.X, c.Cell.Y, 2160d, 3840d), (c.X, c.Y, c.Width, c.Height)));
    }

    [Fact]
    public void Compose_never_enlarges_a_picture_when_heights_differ_and_fit_by_width_uses_the_smallest_width()
    {
        var mixed = ImageCompose.Layout(Spec(2), [(2160, 3840), (1152, 2048)], 0);
        Assert.All(mixed.Cells, c => Assert.Equal(2048d, c.Height));
        Assert.True(mixed.Cells[0].Width <= 2160 && mixed.Cells[1].Width <= 1152);
        Assert.Equal(1152d, mixed.Cells[0].Width);

        var wide = ImageCompose.Layout(Spec(2, "column", fit: "width"), [(2000, 1000), (1000, 1500)], 0);
        Assert.All(wide.Cells, c => Assert.Equal(1000d, c.Width));
        Assert.Equal([500d, 1500d], wide.Cells.Select(c => c.Height));
        // Column: cells under each other with the gap between, margin all round.
        Assert.Equal((1000 + 128, 500 + 1500 + 64 + 128), (wide.Width, wide.Height));
        Assert.Equal(64 + 500 + 64, wide.Cells[1].Cell.Y);

        // A row by width: cells as tall as the tallest picture, shorter ones centred.
        var row = ImageCompose.Layout(Spec(2, fit: "width"), [(2000, 1000), (1000, 1500)], 0);
        Assert.Equal(1500, row.Cells[0].Cell.Height);
        Assert.Equal(row.Cells[0].Cell.Y + 500d, row.Cells[0].Y);
    }

    [Fact]
    public void Compose_grid_takes_columns_from_the_count_or_the_setting_and_centres_narrow_pictures()
    {
        var five = Enumerable.Repeat((1000, 1000), 5).ToList();
        var auto = ImageCompose.Layout(Spec(5, "grid"), five, 0);
        Assert.Equal([64, 1128, 2192, 64, 1128], auto.Cells.Select(c => c.Cell.X));
        Assert.Equal([64, 64, 64, 1128, 1128], auto.Cells.Select(c => c.Cell.Y)); // 3 columns, 2 rows
        Assert.Equal((3 * 1000 + 2 * 64 + 128, 2 * 1000 + 64 + 128), (auto.Width, auto.Height));

        var two = ImageCompose.Layout(Spec(5, "grid", columns: 2), five, 0);
        Assert.Equal([64, 1128, 64, 1128, 64], two.Cells.Select(c => c.Cell.X));
        Assert.Equal([64, 64, 1128, 1128, 2192], two.Cells.Select(c => c.Cell.Y)); // 2 columns, 3 rows
        Assert.Single(ImageCompose.Layout(Spec(5, "grid", columns: 99), five, 0).Cells.Select(c => c.Cell.Y).Distinct()); // more columns than pictures: capped at 5, one row

        var narrow = ImageCompose.Layout(Spec(2, "grid", columns: 2), [(1000, 1000), (500, 1000)], 0);
        Assert.Equal(1000d, narrow.Cells[0].Width);
        Assert.Equal(narrow.Cells[1].Cell.X + 250d, narrow.Cells[1].X);
    }

    [Fact]
    public void Compose_over_the_device_limit_shrinks_everything_evenly_and_under_it_leaves_the_scale_at_one()
    {
        var sizes = Enumerable.Repeat((2160, 3840), 4).ToList();
        var shrunk = ImageCompose.Layout(Spec(4), sizes, 8192);
        Assert.Equal(8192, shrunk.Width);
        Assert.InRange(shrunk.Scale, 0.9142, 0.9143);
        Assert.True(shrunk.Height <= 8192);
        Assert.Equal((int)Math.Round(2288 * shrunk.Scale), shrunk.Cells[1].Cell.X); // gap and margin shrink with the pictures
        Assert.Equal((int)Math.Round(64 * shrunk.Scale), shrunk.Cells[0].Cell.X);
        Assert.Equal(1d, ImageCompose.Layout(Spec(4), sizes, 16384).Scale);
        Assert.Equal(1d, ImageCompose.Layout(Spec(4), sizes, 0).Scale);
        // No edge leaves the output, and neighbouring cells meet at the gap without overlapping.
        Assert.All(shrunk.Cells, c => Assert.True(c.Cell.X + c.Cell.Width <= shrunk.Width && c.Cell.Y + c.Cell.Height <= shrunk.Height));
        Assert.True(shrunk.Cells.Zip(shrunk.Cells.Skip(1)).All(p => p.First.Cell.X + p.First.Cell.Width < p.Second.Cell.X));
    }

    [Fact]
    public void Compose_two_line_alignment_scales_figures_to_the_same_height_and_puts_their_feet_on_one_line()
    {
        var spec = new ComposeSpec([new("a", 0.10, 0.90), new("b", 0.20, 0.60)]);
        var result = ImageCompose.Layout(spec, [(2160, 3840), (2160, 3840)], 0);
        var (a, b) = (result.Cells[0], result.Cells[1]);
        Assert.Equal(1080d, a.Width, 6); // A is halved
        Assert.Equal(1920d, a.Height, 6);
        Assert.Equal((2160d, 3840d), (b.Width, b.Height)); // B is not scaled
        Assert.Equal((0.90 - 0.10) * a.Height, (0.60 - 0.20) * b.Height, 6);
        Assert.Equal(1536d, (0.90 - 0.10) * a.Height, 6);
        Assert.Equal(2304d, a.Y - a.Cell.Y + 0.90 * a.Height, 6); // feet at y = 2304 inside the cell, both
        Assert.Equal(2304d, b.Y - b.Cell.Y + 0.60 * b.Height, 6);
        Assert.Equal(576d, a.Y - a.Cell.Y, 6);
        Assert.Equal(3840, a.Cell.Height);
        Assert.Equal(3840, b.Cell.Height);
        // Cells follow the scaled widths.
        Assert.Equal((1080, 2160), (a.Cell.Width, b.Cell.Width));

        foreach (var bad in new ComposeSpec[]
        {
            new([new("a", 0.1, 0.9), new("b")]),                      // only one is marked
            new([new("a", 0.9, 0.1), new("b", 0.2, 0.6)]),             // top below bottom
            new([new("a", 0.5, 0.5), new("b", 0.2, 0.6)]),             // equal
            new([new("a", -0.1, 0.9), new("b", 0.2, 0.6)]),
            new([new("a", 0.1, 1.5), new("b", 0.2, 0.6)]),
            new([new("a", double.NaN, 0.9), new("b", 0.2, 0.6)]),
            new([new("a", 0.1, 0.9), new("b", 0.2, 0.6)], "column"),   // only a row aligns
            new([new("a", 0.1, 0.9), new("b", 0.2, 0.6)], Fit: "width"),
        })
            Assert.Throws<ArgumentException>(() => ImageCompose.Layout(bad, [(100, 200), (100, 200)], 0));
    }

    [Fact]
    public void Compose_spec_survives_json_and_bad_specs_are_named()
    {
        var spec = new ComposeSpec([new("a", 0.1, 0.9), new("b", 0.2, 0.6)], "row", 3, 10, 20, "#112233", "height");
        var back = InfiniteCanvas.Core.Json.Deserialize<ComposeSpec>(InfiniteCanvas.Core.Json.Serialize(spec))!;
        Assert.Equal(spec.Layout, back.Layout);
        Assert.Equal((spec.Columns, spec.Gap, spec.Margin, spec.Background, spec.Fit), (back.Columns, back.Gap, back.Margin, back.Background, back.Fit));
        Assert.Equal(spec.Cells, back.Cells);

        Assert.Equal((255, 128, 0, 255), ImageCompose.ParseBackground("#FF8000") is { } c ? (c.R, c.G, c.B, c.A) : default);
        Assert.Equal((byte)0, ImageCompose.ParseBackground("Transparent")!.Value.A);
        Assert.All(new[] { "", "FF8000", "#FF80", "#GG0000", "red", "#FF8000FF" }, text => Assert.Null(ImageCompose.ParseBackground(text)));
        Assert.Null(ImageCompose.ParseBackground(null));

        Assert.Null(ImageCompose.Problem(Spec(2)));
        Assert.NotNull(ImageCompose.Problem(Spec(1)));
        Assert.NotNull(ImageCompose.Problem(Spec(2, "diagonal")));
        Assert.NotNull(ImageCompose.Problem(Spec(2, fit: "both")));
        Assert.NotNull(ImageCompose.Problem(Spec(2, gap: -1)));
        Assert.NotNull(ImageCompose.Problem(Spec(2, margin: -1)));
        Assert.NotNull(ImageCompose.Problem(Spec(2, columns: -1)));
        Assert.NotNull(ImageCompose.Problem(Spec(2, background: "white")));
        Assert.Throws<ArgumentException>(() => ImageCompose.Layout(Spec(2), [(10, 10)], 0));      // sizes do not match
        Assert.Throws<ArgumentException>(() => ImageCompose.Layout(Spec(2), [(10, 10), (0, 10)], 0));
    }

    [Fact]
    public void A_generated_output_does_not_inherit_the_composition_of_the_image_it_came_from()
    {
        var source = new CanvasNode { Kind = NodeKind.Image };
        source.SetOption(NodeKeys.Compose, InfiniteCanvas.Core.Json.Serialize(Spec(2)));
        source.SetOption("watermark", "true");
        var output = new CanvasNode { Kind = NodeKind.Image };
        var options = NodeKeys.OutputOptions(source, output);
        Assert.DoesNotContain(NodeKeys.Compose, options.Keys);
        Assert.Equal("true", options["watermark"]);
        Assert.Contains(NodeKeys.Compose, NodeKeys.PerImage);
    }

    private static CanvasNode Picture(string title, double x = 0, double y = 0, int naturalWidth = 0, int naturalHeight = 0) =>
        new() { Kind = NodeKind.Image, Title = title, X = x, Y = y, Width = 340, Height = 240, Content = title + ".png", NaturalWidth = naturalWidth, NaturalHeight = naturalHeight };

    private static void Link(CanvasDocument doc, CanvasNode from, CanvasNode to) => doc.Connections.Add(CanvasConnection.Create(from.Id, to.Id));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keep_one_moves_the_batch_connections_to_the_kept_card_and_returns_the_others_to_delete(bool open)
    {
        var doc = new CanvasDocument();
        var (lead, second, third) = (Picture("正面", 100, 50), Picture("二", 100, 50), Picture("三", 100, 50));
        lead.Content = "图片-001.png"; // its file's number is not its title, so the batch has been named
        var (input, extra, config, group) = (new CanvasNode { Kind = NodeKind.Text, Title = "说明" }, new CanvasNode { Kind = NodeKind.Text, Title = "第二个输入" }, new CanvasNode { Kind = NodeKind.Config, Title = "配置" }, new CanvasNode { Kind = NodeKind.Group, Title = "组" });
        foreach (var node in new[] { group, input, extra, config, lead, second, third }) doc.Nodes.Add(node);
        Stacks.Form([lead, second, third]);
        lead.GroupId = group.Id;
        if (open) foreach (var node in new[] { lead, second, third }) node.SetOption(NodeKeys.StackOpen, "true");
        Link(doc, input, lead); Link(doc, input, second); Link(doc, input, third); // every member shares the inputs
        Link(doc, extra, lead);                                                    // only the lead has this one
        Link(doc, lead, config);                                                   // and an output
        Link(doc, third, config);
        Link(doc, second, third);                                                  // between members: gone with them

        var removed = Picks.KeepOne(doc, second.Id);

        Assert.Equal([lead.Id, third.Id], removed);
        Assert.Null(Stacks.Of(second));
        Assert.False(Stacks.IsOpen(second));
        Assert.Equal((100d, 50d, group.Id, "正面"), (second.X, second.Y, second.GroupId, second.Title));
        var kept = doc.Connections.Where(c => c.From == second.Id || c.To == second.Id).Select(c => (c.From, c.To)).ToList();
        Assert.Equal(1, kept.Count(c => c == (input.Id, second.Id)));
        Assert.Contains((extra.Id, second.Id), kept);
        Assert.Equal(1, kept.Count(c => c == (second.Id, config.Id)));
        // The connection between two members was not copied to the kept card; the one it already has to the removed third stays until that is deleted.
        Assert.Equal(4, kept.Count);
        Assert.DoesNotContain(kept, c => c.From == lead.Id || c.To == lead.Id);
        // Nothing was deleted here: the caller removes the returned nodes, files stay on disk.
        Assert.Contains(lead, doc.Nodes);
    }

    [Fact]
    public void Keep_one_on_the_lead_keeps_its_place_and_does_not_rename_a_batch_still_called_after_its_file()
    {
        var doc = new CanvasDocument();
        var (lead, second) = (Picture("图片-001", 10, 20), Picture("图片-002", 10, 20));
        (lead.Title, second.Title) = ("图片-001", "改过名");
        doc.Nodes.Add(lead); doc.Nodes.Add(second);
        Stacks.Form([lead, second]);
        Assert.Equal([second.Id], Picks.KeepOne(doc, lead.Id));
        Assert.Equal(("图片-001", 10d, 20d), (lead.Title, lead.X, lead.Y));
        Assert.Null(Stacks.Of(lead));

        // A lead that kept its file-number title does not pass it on.
        var again = new CanvasDocument();
        var (a, b) = (Picture("图片-001"), Picture("图片-002"));
        (b.Title, a.Title) = ("我的名字", "图片-001");
        again.Nodes.Add(a); again.Nodes.Add(b);
        Stacks.Form([a, b]);
        Picks.KeepOne(again, b.Id);
        Assert.Equal("我的名字", b.Title);
    }

    [Fact]
    public void Keep_one_refuses_a_loose_node_and_a_stack_of_one()
    {
        var doc = new CanvasDocument();
        var (loose, single) = (Picture("a"), Picture("b"));
        single.SetOption(NodeKeys.Stack, single.Id);
        doc.Nodes.Add(loose); doc.Nodes.Add(single);
        Assert.Throws<ArgumentException>(() => Picks.KeepOne(doc, loose.Id));
        Assert.Throws<ArgumentException>(() => Picks.KeepOne(doc, single.Id));
        Assert.Throws<ArgumentException>(() => Picks.KeepOne(doc, "missing"));
    }

    [Fact]
    public void Promote_puts_the_picture_in_the_old_ones_place_with_its_title_group_and_connections()
    {
        var doc = new CanvasDocument();
        var (old, fresh, input, other, group) = (Picture("正面·定稿", 500, 400), Picture("新图", 2000, 0, 1000, 1500), new CanvasNode { Kind = NodeKind.Text, Title = "输入" }, Picture("下游", 900, 400), new CanvasNode { Kind = NodeKind.Group, Title = "定稿" });
        old.GroupId = group.Id;
        foreach (var node in new[] { group, input, other, old, fresh }) doc.Nodes.Add(node);
        Link(doc, input, old); Link(doc, input, fresh); Link(doc, old, other); Link(doc, old, fresh);

        var removed = Picks.Promote(doc, fresh.Id, old.Id);

        Assert.Equal([old.Id], removed);
        Assert.Equal((500d, 400d, 340d, 510d), (fresh.X, fresh.Y, fresh.Width, fresh.Height)); // 340 x 1500 / 1000
        Assert.Equal(("正面·定稿", group.Id), (fresh.Title, fresh.GroupId));
        var links = doc.Connections.Where(c => c.From == fresh.Id || c.To == fresh.Id).Select(c => (c.From, c.To)).ToList();
        Assert.Equal(1, links.Count(c => c == (input.Id, fresh.Id)));
        Assert.Contains((fresh.Id, other.Id), links);
        Assert.DoesNotContain(links, c => c.From == fresh.Id && c.To == fresh.Id);
        Assert.Contains(old, doc.Nodes);

        // A picture taken out of a batch leaves it, and the batch stays.
        var (a, b, c2) = (Picture("A", 0, 0), Picture("B", 0, 0), Picture("C", 0, 0));
        var target = Picture("旧定稿", 50, 50);
        foreach (var node in new[] { a, b, c2, target }) doc.Nodes.Add(node);
        Stacks.Form([a, b, c2]);
        Picks.Promote(doc, b.Id, target.Id);
        Assert.Null(Stacks.Of(b));
        Assert.Equal(2, Stacks.Members(doc, a.Id).Count);
    }

    [Fact]
    public void Promote_refuses_what_it_cannot_do_and_changes_nothing_when_it_does()
    {
        var doc = new CanvasDocument();
        var (old, fresh, empty, text, a, b) = (Picture("旧"), Picture("新"), new CanvasNode { Kind = NodeKind.Image, Title = "空" }, new CanvasNode { Kind = NodeKind.Text, Title = "文" }, Picture("A"), Picture("B"));
        foreach (var node in new[] { old, fresh, empty, text, a, b }) doc.Nodes.Add(node);
        Stacks.Form([a, b]);
        var before = doc.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();

        Assert.Throws<ArgumentException>(() => Picks.Promote(doc, old.Id, old.Id));
        Assert.Throws<ArgumentException>(() => Picks.Promote(doc, empty.Id, old.Id));   // nothing to promote
        Assert.Throws<ArgumentException>(() => Picks.Promote(doc, text.Id, old.Id));
        Assert.Throws<ArgumentException>(() => Picks.Promote(doc, fresh.Id, text.Id));
        Assert.Throws<ArgumentException>(() => Picks.Promote(doc, fresh.Id, a.Id));     // would take a whole batch along
        Assert.Throws<ArgumentException>(() => Picks.Promote(doc, "missing", old.Id));
        Assert.True(Picks.CheckPromote(doc, fresh.Id, old.Id));
        Assert.Equal(before, doc.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
    }

    [Fact]
    public async Task Finalising_replaces_the_library_file_named_after_the_old_picture_and_leaves_the_old_one_when_it_cannot()
    {
        var (id, dir, source) = LibrarySandbox();
        try
        {
            string Make(string name, byte value) { var path = Path.Combine(source, name); File.WriteAllBytes(path, [value]); return path; }
            await LibraryStore.PutAsync(id, Make("正面.png", 1), false);
            await LibraryStore.PutAsync(id, Make("侧面.png", 2), false);
            var changed = 0;
            void OnChanged() => changed++;
            LibraryStore.Changed += OnChanged;
            try
            {
                Assert.Null(LibraryStore.ReplaceByTitle(id, "没有这个", Make("n.png", 9), DeleteInstead));
                // Same extension: same file name, new content.
                Assert.Equal("正面.png", LibraryStore.ReplaceByTitle(id, "正面", Make("新正面.png", 7), DeleteInstead));
                Assert.Equal([7], await File.ReadAllBytesAsync(Path.Combine(dir, "正面.png")));
                // Another extension: the new picture keeps the name and brings its own extension, the old file is gone.
                Assert.Equal("正面.jpg", LibraryStore.ReplaceByTitle(id, "正面", Make("再正面.JPG", 8), DeleteInstead));
                Assert.Equal(["侧面.png", "正面.jpg"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
                Assert.True(changed >= 2);

                // A failed recycle, a locked file and a name already taken all leave the library as it was.
                await LibraryStore.PutAsync(id, Make("侧面.jpg", 4), false);
                var before = Directory.GetFiles(dir).Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)[0])).Order().ToList();
                Assert.Throws<LibraryException>(() => LibraryStore.ReplaceByTitle(id, "侧面.png", Make("c.jpg", 5), DeleteInstead)); // 侧面.jpg is taken
                Assert.Throws<LibraryException>(() => LibraryStore.ReplaceByTitle(id, "侧面.png", Make("x.png", 5), _ => false));
                await using (new FileStream(Path.Combine(dir, "侧面.png"), FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.Throws<LibraryException>(() => LibraryStore.ReplaceByTitle(id, "侧面.png", Make("y.png", 5), DeleteInstead));
                Assert.Equal(before, Directory.GetFiles(dir).Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)[0])).Order().ToList());
                Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".tmp"));
            }
            finally { LibraryStore.Changed -= OnChanged; }
        }
        finally { Cleanup(id, source); }
    }

    [Fact]
    public async Task A_library_name_without_extension_that_fits_two_files_is_an_error_naming_both_and_never_picks_one()
    {
        var (id, dir, source) = LibrarySandbox();
        try
        {
            foreach (var name in new[] { "猫.png", "猫.jpg", "狗.png" }) await File.WriteAllBytesAsync(Path.Combine(source, name), [1]);
            foreach (var name in new[] { "猫.png", "猫.jpg", "狗.png" }) await LibraryStore.PutAsync(id, Path.Combine(source, name), false);

            var ambiguous = Assert.Throws<LibraryException>(() => LibraryStore.Find(id, "猫"));
            Assert.Contains("猫.png", ambiguous.Message);
            Assert.Contains("猫.jpg", ambiguous.Message);
            Assert.Throws<LibraryException>(() => LibraryStore.Rename(id, "猫", "小猫"));
            var (removed, failed) = LibraryStore.RemoveNames(id, ["猫", "狗"], DeleteInstead);
            Assert.Equal(["狗.png"], removed);
            Assert.Single(failed);
            Assert.Contains("猫.jpg", failed[0].Reason);
            Assert.Equal(["猫.jpg", "猫.png"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // The full name, the unique stem and a different case still resolve.
            Assert.Equal("猫.jpg", Path.GetFileName(LibraryStore.Find(id, "猫.JPG")!.Path));
            Assert.Equal("小猫.png", LibraryStore.Rename(id, "猫.png", "小猫"));
            Assert.Equal("猫.jpg", Path.GetFileName(LibraryStore.Find(id, "猫")!.Path));
            Assert.Null(LibraryStore.Find(id, "没有"));
        }
        finally { Cleanup(id, source); }
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(1e308)]
    public void A_gap_large_enough_to_overflow_never_gives_a_batch_a_coordinate_that_cannot_be_saved(double gap)
    {
        var batch = Enumerable.Range(0, 4).Select(_ => new CanvasNode { Kind = NodeKind.Image, X = 100, Y = 50, Width = 340, Height = 240 }).ToList();
        foreach (var layout in new[] { StackLayout.Row, StackLayout.Grid })
        {
            var (width, height) = Stacks.Footprint(batch, layout, gap);
            Assert.True(double.IsFinite(width) && double.IsFinite(height));
            var x = CanvasGraph.FreeBatchX(batch, layout, gap, [new CanvasNode { Kind = NodeKind.Image, X = 150, Y = 50, Width = 340, Height = 240 }]);
            Assert.True(double.IsFinite(x));
        }
        // The default gap stands in, so the footprint is the one a default spread has.
        Assert.Equal(Stacks.Footprint(batch, StackLayout.Row, Stacks.Gap), Stacks.Footprint(batch, StackLayout.Row, double.MaxValue));
    }

    [Fact]
    public void A_spread_batch_looks_for_a_spot_where_the_whole_row_or_grid_is_free_beside_the_nodes_there()
    {
        CanvasNode Card(double x, double y, double w = 340, double h = 240) => new() { Kind = NodeKind.Image, X = x, Y = y, Width = w, Height = h };
        var batch = Enumerable.Range(0, 4).Select(i => Card(0, 0)).ToList();
        var neighbour = Card(500, 0);
        var rowSize = Stacks.Footprint(batch, StackLayout.Row, 24);
        Assert.Equal((4 * 340 + 3 * 24d, 240d), rowSize);
        Assert.Equal((2 * 340 + 24d, 2 * 240 + 24d), Stacks.Footprint(batch, StackLayout.Grid, 24));
        Assert.Equal((340d, 240d), Stacks.Footprint(batch, StackLayout.Stack, 24));

        // One card fits at x = 0 beside a neighbour at 500 only if it ends before it; four cards in a row do not, so the batch moves past the neighbour.
        Assert.Equal(0, CanvasGraph.FreeBatchX(batch, StackLayout.Stack, 24, [neighbour]));
        var x = CanvasGraph.FreeBatchX(batch, StackLayout.Row, 24, [neighbour]);
        Assert.True(x >= neighbour.Right);
        var spot = Card(x, 0, rowSize.Width, rowSize.Height);
        Assert.False(Rect.Of(spot).Intersects(Rect.Of(neighbour)));

        // A neighbour above or below that the spread never reaches does not push it.
        Assert.Equal(0, CanvasGraph.FreeBatchX(batch, StackLayout.Row, 24, [Card(0, 2000)]));
        // Grid: two columns need less width than a row of four.
        var gridX = CanvasGraph.FreeBatchX(batch, StackLayout.Grid, 24, [Card(1000, 0)]);
        Assert.True(gridX < x + 1);
        Assert.False(Rect.Of(Card(gridX, 0, 2 * 340 + 24, 2 * 240 + 24)).Intersects(Rect.Of(Card(1000, 0))));
        Assert.Equal(340 * 16 / 9d, CanvasGraph.OutputHeight(340, "9:16"), 6);
        Assert.Equal((340d, 340d), (CanvasGraph.OutputHeight(340, "1:1"), CanvasGraph.OutputHeight(340, null)));
        Assert.Equal(640, CanvasGraph.OutputHeight(340, "1:9"));
    }

    [Fact]
    public void Usage_report_groups_channels_by_currency_and_never_adds_yuan_to_dollars()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var settings = new AppSettings();
        ModelChannel Add(Vendor vendor, params string[] models)
        {
            var channel = new ModelChannel { Id = Ids.New("report"), Vendor = vendor, Models = models.Select(m => new ChannelModel { Name = m }).ToList() };
            settings.Channels.Add(channel);
            return channel;
        }
        var google = Add(Vendor.Google, "gemini-2.5-flash-image", "mystery-model");
        var openAI = Add(Vendor.OpenAI, "gpt-image-1");
        var ark = Add(Vendor.Ark, "seedream");
        var qwen = Add(Vendor.Qwen, "qwen-image-2.1-pro");
        Add(Vendor.XAI, "grok-imagine-image");
        UsageStore.Record(google.Id, "gemini-2.5-flash-image", Capability.Image, 100, new TokenUsage(0, 0, 129000));
        UsageStore.Record(google.Id, "mystery-model", Capability.Text, 2, new TokenUsage(500, 500));
        UsageStore.Record(openAI.Id, "gpt-image-1", Capability.Image, 1, new TokenUsage(1_000_000, 0));
        UsageStore.Record(ark.Id, "seedream", Capability.Image, 4);
        UsageStore.Record(qwen.Id, "qwen-image-2.1-pro", Capability.Image, 3);

        var report = UsageReport.Summarize(settings, day);

        Assert.Equal(["CNY", "USD"], report.Select(c => c.Currency));
        var (cny, usd) = (report[0], report[1]);
        Assert.Equal(7, cny.Results["image"]);
        Assert.Null(cny.EstimatedAmount);
        Assert.Equal(2, cny.Channels.Count);
        Assert.Equal((101, 2), (usd.Results["image"], usd.Results["text"]));
        Assert.Equal(2, usd.Channels.Count);
        var gemini = usd.Channels.Single(c => c.UnpricedModels.Count > 0);
        Assert.Equal(["mystery-model"], gemini.UnpricedModels);
        Assert.Equal(129000 * 30m / 1_000_000m, gemini.EstimatedAmount);
        Assert.Equal(gemini.EstimatedAmount + 5m, usd.EstimatedAmount);
        Assert.Empty(UsageReport.Summarize(settings, day.AddDays(1)));
        Assert.Equal("CNY", UsageReport.CurrencyOf(Vendor.Ark));
        Assert.Equal("CNY", UsageReport.CurrencyOf(Vendor.Qwen));
        Assert.All(new[] { Vendor.OpenAI, Vendor.Google, Vendor.XAI }, v => Assert.Equal("USD", UsageReport.CurrencyOf(v)));
    }

    private static List<CanvasNode> Batch(int count, double x = 100, double y = 50)
    {
        var members = Enumerable.Range(0, count).Select(i => CanvasNode.Create(NodeKind.Image, 0, 0)).ToList();
        for (var i = 0; i < count; i++) (members[i].X, members[i].Y, members[i].Width, members[i].Height) = (x, y, 200 + i * 20, 100);
        Stacks.Form(members);
        return members;
    }

    [Theory]
    [InlineData("stack", StackLayout.Stack)]
    [InlineData(" ROW ", StackLayout.Row)]
    [InlineData("Grid", StackLayout.Grid)]
    [InlineData("1", null)]
    [InlineData("row,grid", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Stack_layouts_parse_only_their_three_names(string? text, StackLayout? expected) => Assert.Equal(expected, Stacks.ParseLayout(text));

    [Fact]
    public void Row_and_grid_lay_members_out_from_the_lead_with_the_given_gap()
    {
        var members = Batch(4);
        Assert.Equal([(100d, 50d), (324d, 50d), (568d, 50d), (832d, 50d)], Stacks.Row(members, 24));
        Assert.Equal([(100d, 50d), (330d, 50d), (560d, 50d), (790d, 50d)], Stacks.Row(members.Take(1).Concat(Enumerable.Range(0, 3).Select(_ => { var n = CanvasNode.Create(NodeKind.Image, 0, 0); (n.Width, n.Height) = (200, 100); return n; })).ToList(), 30));
        var grid = Stacks.Grid(members, 10);
        Assert.Equal([(100d, 50d), (100 + 260d + 10, 50d), (100d, 50 + 100d + 10), (100 + 260d + 10, 50 + 100d + 10)], grid);
        Assert.Equal(Stacks.Grid(members), Stacks.Grid(members, Stacks.Gap));
    }

    [Fact]
    public void Open_marks_the_stack_open_and_returns_the_same_grid_the_stack_button_uses_and_Collapse_undoes_it()
    {
        var members = Batch(3);
        var expected = Stacks.Grid(members);
        Assert.Equal(expected, Stacks.Open(members));
        Assert.All(members, n => Assert.True(Stacks.IsOpen(n)));
        for (var i = 0; i < members.Count; i++) (members[i].X, members[i].Y) = expected[i];
        Stacks.Collapse(members);
        Assert.All(members, n => Assert.Equal((members[0].X, members[0].Y, false), (n.X, n.Y, Stacks.IsOpen(n))));
        Assert.Equal((100d, 50d), (members[2].X, members[2].Y));
    }

    [Fact]
    public void FormBatch_spreads_only_the_new_nodes_and_leaves_a_stack_the_user_arranged_alone()
    {
        var earlier = Batch(3, 2000, 2000);
        Stacks.Open(earlier);
        (earlier[1].X, earlier[1].Y) = (2500, 2600);
        var moved = (earlier[1].X, earlier[1].Y);

        var row = Batch(3);
        foreach (var node in row) { node.SetOption(NodeKeys.Stack, null); (node.X, node.Y) = (100, 50); }
        Stacks.FormBatch(row, StackLayout.Row, 12);
        Assert.All(row, n => Assert.True(Stacks.IsOpen(n)));
        Assert.Equal(Stacks.Row(row, 12), row.Select(n => (n.X, n.Y)));
        Assert.Equal(row[0].Id, Stacks.Of(row[2]));

        var collapsed = Batch(3, 700, 700);
        foreach (var node in collapsed) { node.SetOption(NodeKeys.Stack, null); (node.X, node.Y) = (700, 700); }
        Stacks.FormBatch(collapsed, StackLayout.Stack, 99);
        Assert.All(collapsed, n => Assert.Equal((700d, 700d, false), (n.X, n.Y, Stacks.IsOpen(n))));

        Assert.Equal(moved, (earlier[1].X, earlier[1].Y));
        Assert.Contains(NodeKeys.Layout, NodeKeys.PerImage);
        Assert.Contains(NodeKeys.Gap, NodeKeys.PerImage);
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("0", 0d)]
    [InlineData(null, Stacks.Gap)]
    [InlineData("abc", Stacks.Gap)]
    [InlineData("-1", Stacks.Gap)]
    [InlineData("NaN", Stacks.Gap)]
    [InlineData("Infinity", Stacks.Gap)]
    public void A_batch_gap_set_through_raw_options_is_used_only_when_finite_and_not_negative(string? option, double expected)
    {
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        node.SetOption(NodeKeys.Gap, option);
        Assert.Equal(expected, Stacks.GapOf(node));
    }

    [Theory]
    [InlineData(StackLayout.Row)]
    [InlineData(StackLayout.Grid)]
    public void A_gap_large_enough_to_overflow_falls_back_to_the_default_so_every_position_can_be_saved(StackLayout layout)
    {
        var members = Batch(9);
        var expected = layout == StackLayout.Row ? Stacks.Row(members) : Stacks.Grid(members);
        Assert.Equal(expected, Stacks.Open(members, layout, double.MaxValue));

        var batch = Batch(9);
        foreach (var node in batch) node.SetOption(NodeKeys.Stack, null);
        Stacks.FormBatch(batch, layout, double.MaxValue);
        Assert.All(batch, n => Assert.True(double.IsFinite(n.X) && double.IsFinite(n.Y)));
        Assert.Equal(expected, batch.Select(n => (n.X, n.Y)));
    }

    [Fact]
    public void A_retried_output_takes_its_source_settings_but_stays_in_its_stack_and_keeps_its_source()
    {
        var source = CanvasNode.Create(NodeKind.Config, 0, 0);
        foreach (var (key, value) in new[] { ("background", "transparent"), ("layers", "true"), (NodeKeys.Mask, "蒙版-001.png"), (NodeKeys.Layout, "row"), (NodeKeys.Gap, "40"), (NodeKeys.Stack, "other") }) source.SetOption(key, value);
        var retry = CanvasNode.Create(NodeKind.Image, 0, 0);
        foreach (var (key, value) in new[] { (NodeKeys.SourceId, source.Id), (NodeKeys.Stack, "node-lead"), (NodeKeys.StackOpen, "true"), (NodeKeys.InputNote, "旧说明"), ("background", "opaque") }) retry.SetOption(key, value);

        var options = NodeKeys.OutputOptions(source, retry);

        Assert.Equal(new Dictionary<string, string> { ["background"] = "transparent", [NodeKeys.SourceId] = source.Id, [NodeKeys.Stack] = "node-lead", [NodeKeys.StackOpen] = "true" }, options);
        // A new output has nothing of its own yet: it gets the settings only.
        Assert.Equal(["background"], NodeKeys.OutputOptions(source, CanvasNode.Create(NodeKind.Image, 0, 0)).Keys);
    }

    /// <summary>Stands in for the shell in tests: the file is simply deleted, so nothing lands in the real Recycle Bin.</summary>
    private static bool DeleteInstead(string path)
    {
        File.Delete(path);
        return true;
    }

    [Fact]
    public async Task A_failed_recycle_during_replace_or_remove_leaves_the_library_file_and_reports_it()
    {
        var (id, dir, source) = LibrarySandbox();
        try
        {
            var first = Path.Combine(source, "猫.png");
            var second = Path.Combine(Directory.CreateDirectory(Path.Combine(source, "b")).FullName, "猫.png");
            await File.WriteAllBytesAsync(first, [1]);
            await File.WriteAllBytesAsync(second, [2, 2]);
            await LibraryStore.PutAsync(id, first, false);
            var asked = new List<string>();
            bool Refuse(string path) { asked.Add(Path.GetFileName(path)); return false; }

            var replace = await Assert.ThrowsAsync<LibraryException>(() => LibraryStore.PutAsync(id, second, true, Refuse));
            Assert.Contains("回收站", replace.Message);
            Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(dir, "猫.png")));
            Assert.Equal(["猫.png"], Directory.GetFiles(dir).Select(Path.GetFileName));

            var (removed, failed) = LibraryStore.RemoveNames(id, ["猫"], Refuse);
            Assert.Empty(removed);
            Assert.Equal([("猫", "没能移到回收站")], failed);
            Assert.True(File.Exists(Path.Combine(dir, "猫.png")));
            Assert.Equal(["猫.png", "猫.png"], asked);

            // A recycler that throws is a failed copy of the same kind, not a crash.
            await Assert.ThrowsAsync<LibraryException>(() => LibraryStore.PutAsync(id, second, true, _ => throw new UnauthorizedAccessException("拒绝访问")));
            Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(dir, "猫.png")));
            Assert.Equal(["猫.png"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Cleanup(id, source); }
    }

    [Fact]
    public void Silent_recycle_moves_a_local_file_away_and_returns_false_without_a_dialog_when_it_cannot()
    {
        var dir = Directory.CreateTempSubdirectory("ic-recycle").FullName;
        try
        {
            var plain = Path.Combine(dir, "plain.png");
            Assert.True(ProjectStore.RecycleSilently(plain)); // nothing there to recycle
            // Really recycling leaves the file in the machine's Recycle Bin, so that one step runs only on request (after a change to RecycleSilently).
            if (Environment.GetEnvironmentVariable("INFINITE_CANVAS_TEST_RECYCLE_BIN") == "1")
            {
                File.WriteAllBytes(plain, [1]);
                Assert.True(ProjectStore.RecycleSilently(plain));
                Assert.False(File.Exists(plain));
            }

            // A file held open would make the ordinary shell call raise an error dialog and wait; here it just fails and stays.
            var held = Path.Combine(dir, "held.png");
            File.WriteAllBytes(held, [1]);
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.False(ProjectStore.RecycleSilently(held));
            Assert.True(File.Exists(held));

        }
        finally { Directory.Delete(dir, true); }
    }

    private static (string Id, string Dir, string Source) LibrarySandbox()
    {
        var id = "lib-test-" + Guid.NewGuid().ToString("N")[..8];
        var source = Directory.CreateTempSubdirectory("ic-lib-src").FullName;
        return (id, ProjectStore.LibraryDir(id), source);
    }

    private static void Cleanup(string id, string source)
    {
        Directory.Delete(source, true);
        if (Directory.Exists(Path.GetDirectoryName(ProjectStore.LibraryDir(id))!)) Directory.Delete(Path.GetDirectoryName(ProjectStore.LibraryDir(id))!, true);
    }

    [Fact]
    public async Task Library_put_refuses_a_clash_unless_replacing_and_keeps_the_old_file_when_the_copy_fails()
    {
        var (id, dir, source) = LibrarySandbox();
        try
        {
            var first = Path.Combine(source, "猫.PNG");
            var second = Path.Combine(Path.Combine(Directory.CreateDirectory(Path.Combine(source, "b")).FullName), "猫.png");
            await File.WriteAllBytesAsync(first, [1]);
            await File.WriteAllBytesAsync(second, [2, 2]);
            var changed = 0;
            void OnChanged() => changed++;
            LibraryStore.Changed += OnChanged;
            try
            {
                Assert.Equal("猫.png", await LibraryStore.PutAsync(id, first, false));
                var clash = await Assert.ThrowsAsync<LibraryException>(() => LibraryStore.PutAsync(id, second, false));
                Assert.Contains("replace", clash.Message);
                Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(dir, "猫.png")));

                Assert.Equal("猫.png", await LibraryStore.PutAsync(id, second, true, DeleteInstead));
                Assert.Equal([2, 2], await File.ReadAllBytesAsync(Path.Combine(dir, "猫.png")));
                Assert.Equal(["猫.png"], Directory.GetFiles(dir).Select(Path.GetFileName));
                Assert.True(changed >= 2); // the event is static, so tests running in parallel may add to the count

                // A missing source fails before the old file is touched.
                await Assert.ThrowsAsync<LibraryException>(() => LibraryStore.PutAsync(id, Path.Combine(source, "gone", "猫.png"), true));
                Assert.Equal([2, 2], await File.ReadAllBytesAsync(Path.Combine(dir, "猫.png")));
                Assert.Equal(["猫.png"], Directory.GetFiles(dir).Select(Path.GetFileName));

                // A file another program holds open is not replaced.
                await using (new FileStream(Path.Combine(dir, "猫.png"), FileMode.Open, FileAccess.Read, FileShare.None))
                    await Assert.ThrowsAsync<LibraryException>(() => LibraryStore.PutAsync(id, first, true, DeleteInstead));
                Assert.Equal([2, 2], await File.ReadAllBytesAsync(Path.Combine(dir, "猫.png")));
            }
            finally { LibraryStore.Changed -= OnChanged; }
        }
        finally { Cleanup(id, source); }
    }

    [Fact]
    public async Task Library_rename_changes_only_the_stem_and_rejects_clashes_reserved_names_and_path_escapes()
    {
        var (id, dir, source) = LibrarySandbox();
        try
        {
            foreach (var name in new[] { "a.png", "b.png", "c.mp3" }) await File.WriteAllBytesAsync(Path.Combine(source, name), [1]);
            foreach (var name in new[] { "a.png", "b.png", "c.mp3" }) await LibraryStore.PutAsync(id, Path.Combine(source, name), false);
            var outside = Path.Combine(Path.GetDirectoryName(dir)!, "outside.png");
            await File.WriteAllBytesAsync(outside, [1]);

            Assert.Equal("新名.png", LibraryStore.Rename(id, "a", "新名"));
            Assert.Equal("新名二.png", LibraryStore.Rename(id, "新名.png", "新名二.png"));
            Assert.Equal("新名二.png", LibraryStore.Rename(id, "新名二", "新名二.PNG").ToLowerInvariant());
            Assert.Equal("C.mp3", LibraryStore.Rename(id, "c.mp3", "C"));
            Assert.Equal(["C.mp3", "b.png", "新名二.png"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            foreach (var newName in new[] { "b", "B.PNG", "CON", "nul.txt", "Com3", "LPT1.png", "", " ", "x ", "x.", "..", "a/b", @"a\b", @"..\outside", "../outside", @"C:\x", "x:y", "a?b", "x.jpg" })
                Assert.Throws<LibraryException>(() => LibraryStore.Rename(id, "新名二", newName));
            foreach (var name in new[] { "missing", @"..\outside.png", "../outside.png", outside, "..", "" })
                Assert.Throws<LibraryException>(() => LibraryStore.Rename(id, name, "y"));
            Assert.Equal(["C.mp3", "b.png", "新名二.png"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.True(File.Exists(outside));
        }
        finally { Cleanup(id, source); }
    }

    [Fact]
    public async Task Library_remove_by_name_reports_each_failure_and_skips_files_in_use()
    {
        var (id, dir, source) = LibrarySandbox();
        try
        {
            foreach (var name in new[] { "a.png", "b.png" }) await File.WriteAllBytesAsync(Path.Combine(source, name), [1]);
            foreach (var name in new[] { "a.png", "b.png" }) await LibraryStore.PutAsync(id, Path.Combine(source, name), false);
            var outside = Path.Combine(Path.GetDirectoryName(dir)!, "outside.png");
            await File.WriteAllBytesAsync(outside, [1]);

            (List<string> Removed, List<(string Name, string Reason)> Failed) result;
            await using (new FileStream(Path.Combine(dir, "b.png"), FileMode.Open, FileAccess.Read, FileShare.None))
                result = LibraryStore.RemoveNames(id, ["b", "missing", @"..\outside.png", outside], DeleteInstead);

            Assert.Empty(result.Removed);
            Assert.Equal(["b", "missing", @"..\outside.png", outside], result.Failed.Select(f => f.Name));
            Assert.Contains("占用", result.Failed[0].Reason);
            Assert.True(File.Exists(outside));
            Assert.Equal(2, Directory.GetFiles(dir).Length);

            result = LibraryStore.RemoveNames(id, ["a"], DeleteInstead);
            Assert.Equal(["a.png"], result.Removed);
            Assert.Equal(["b.png"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Cleanup(id, source); }
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con.tar", true)]
    [InlineData("Nul ", true)]
    [InlineData("COM1", true)]
    [InlineData("lpt9", true)]
    [InlineData("COM0", false)]
    [InlineData("COM10", false)]
    [InlineData("console", false)]
    [InlineData("猫", false)]
    public void Reserved_device_names_are_recognised_with_or_without_an_extension(string name, bool reserved) => Assert.Equal(reserved, Media.IsReservedName(name));

    [Fact]
    public void MoveToGroup_changes_only_group_ids_and_reports_the_groups_left_and_FitGroup_matches_the_members()
    {
        var doc = new CanvasDocument();
        var (a, b, c) = (CanvasNode.Create(NodeKind.Text, 0, 0), CanvasNode.Create(NodeKind.Text, 800, 0), CanvasNode.Create(NodeKind.Text, 3000, 0));
        var (one, two) = (new CanvasNode { Kind = NodeKind.Group, Title = "一" }, new CanvasNode { Kind = NodeKind.Group, Title = "二" });
        (a.GroupId, b.GroupId, c.GroupId) = (one.Id, one.Id, two.Id);
        foreach (var node in new[] { one, two, a, b, c }) doc.Nodes.Add(node);
        var position = (c.X, c.Y);

        var (moved, left) = CanvasGraph.MoveToGroup(doc, new[] { c.Id, a.Id, one.Id }.ToHashSet(), one.Id);

        Assert.Equal([c.Id], moved.Select(n => n.Id));
        Assert.True(left.SetEquals([two.Id]));
        Assert.Equal(one.Id, c.GroupId);
        Assert.Equal(("一", position), (one.Title, (c.X, c.Y)));
        CanvasGraph.FitGroup(doc, one.Id);
        var rect = CanvasGraph.GroupRect([a, b, c]);
        Assert.Equal((rect.Left, rect.Top, rect.Width, rect.Height), (one.X, one.Y, one.Width, one.Height));

        var (released, _) = CanvasGraph.MoveToGroup(doc, new[] { a.Id, b.Id, c.Id }.ToHashSet(), null);
        Assert.Equal(3, released.Count);
        var size = (one.X, one.Width);
        CanvasGraph.FitGroup(doc, one.Id);
        Assert.Equal(size, (one.X, one.Width));
    }

    [Fact]
    public void Collapsed_stack_selection_expands_all_members_but_open_members_stay_independent()
    {
        var doc = new CanvasDocument();
        var first = CanvasNode.Create(NodeKind.Image, 0, 0);
        var second = CanvasNode.Create(NodeKind.Image, 0, 0);
        var other = CanvasNode.Create(NodeKind.Image, 0, 0);
        foreach (var node in new[] { first, second, other }) doc.Nodes.Add(node);
        Stacks.Form([first, second]);
        Assert.True(Stacks.Expand(doc, [first.Id]).SetEquals([first.Id, second.Id]));
        Assert.True(Stacks.Expand(doc, [second.Id, other.Id]).SetEquals([first.Id, second.Id, other.Id]));
        foreach (var node in new[] { first, second }) node.SetOption(NodeKeys.StackOpen, "true");
        Assert.True(Stacks.Expand(doc, [first.Id]).SetEquals([first.Id]));
    }

    [Fact]
    public void Stack_name_belongs_to_the_batch_and_its_lead_goes_by_its_own_number()
    {
        var lead = CanvasNode.Create(NodeKind.Image, 0, 0);
        var second = CanvasNode.Create(NodeKind.Image, 0, 0);
        (lead.Title, lead.Content, second.Title, second.Content) = ("卸妆·锁眼长", "图片-007.png", "图片-008", "图片-008.png");
        Stacks.Form([lead, second]);
        Assert.Equal(["图片-007", "图片-008"], [Stacks.OwnTitle(lead), Stacks.OwnTitle(second)]);
        var single = CanvasNode.Create(NodeKind.Image, 0, 0);
        (single.Title, single.Content) = ("全身正面", "图片-009.png");
        Assert.Equal("全身正面", Stacks.OwnTitle(single));
    }

    [Theory]
    [InlineData("default::gpt-image-2.5-flare", "gpt-image-2.5-flare")]
    [InlineData("models/gemini-3-pro-image-preview", "gemini-3-pro-image")]
    [InlineData("doubao-seedream-4-5-251128", "doubao-seedream-4-5")]
    [InlineData("veo-3.1-generate-preview", "veo-3.1-generate")]
    public void Normalize_strips_prefixes_and_snapshots(string input, string expected) => Assert.Equal(expected, ModelFamilies.Normalize(input));

    [Fact]
    public void Every_recommended_media_model_has_a_family()
    {
        foreach (var vendor in Enum.GetValues<Vendor>())
        foreach (var model in ModelFamilies.Recommended(vendor))
        {
            var capability = ModelFamilies.GuessCapability(model);
            if (capability == Capability.Image) Assert.True(ModelFamilies.FindImage(vendor, model) is not null, model);
        }
        Assert.Equal("seedream-5-flash", ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-5-0-flash-260901")!.Id);
        Assert.Equal("seedream-5-lite", ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-5-0-260128")!.Id);
    }

    [Fact]
    public void Image_sizes_follow_each_vendor_table()
    {
        Assert.Equal("2048x1152", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2")!, "2K", "16:9"));
        Assert.Equal("auto", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2")!, "auto", "16:9"));
        Assert.Equal("2848x1600", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-5-0-pro-260628")!, "2K", "16:9"));
        Assert.Equal("2144x2144", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-5-0-pro-260628")!, "2K", "1:1"));
        Assert.Equal("1792x1344", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-5-0-pro-260628")!, "1.5K", "4:3"));
        Assert.Equal("5456x3072", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-4-5-251128")!, "4K", "16:9"));
        Assert.Equal("2848x1600", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-4-5-251128")!, "2K", "16:9"));
        Assert.Equal("1280x720", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-4-0-250828")!, "1K", "16:9"));
        Assert.Equal("3K", ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Ark, "doubao-seedream-5-0-260128")!, "3K", "auto"));
        var qwen = ModelFamilies.FindImage(Vendor.Qwen, "qwen-image-3.0-pro")!;
        Assert.Equal("2720*1536", ModelFamilies.ImageSize(qwen, "2K", "16:9"));
        Assert.Equal("2048*2048", ModelFamilies.ImageSize(qwen, "2K", "1:1"));
        Assert.Equal("1984*1120", ModelFamilies.ImageSize(qwen, "1K", "16:9"));
        Assert.Equal("1488*1488", ModelFamilies.ImageSize(qwen, "1K", "1:1"));
        Assert.Null(ModelFamilies.ImageSize(ModelFamilies.FindImage(Vendor.Qwen, "qwen-image-3.0")!, "auto", "16:9"));
        var legacy = ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-1.5")!;
        Assert.Equal("gpt-image-1", legacy.Id);
        Assert.Equal("gpt-image-1", ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-1-mini")!.Id);
        Assert.Equal("1536x1024", ModelFamilies.ImageSize(legacy, "1K", "3:2"));
        Assert.Equal("1024x1024", ModelFamilies.ImageSize(legacy, "1K", "16:9"));
        Assert.Equal("auto", ModelFamilies.ImageSize(legacy, "2K", "16:9"));
    }

    [Fact]
    public void Qwen_sizes_stay_within_official_limits_for_every_ratio()
    {
        foreach (var family in ModelFamilies.Image.Where(f => f.Vendor == Vendor.Qwen && f.Tiers.Length > 0))
        foreach (var tier in new[] { "1K", "2K" })
        foreach (var ratio in family.Ratios)
        {
            var parts = ModelFamilies.ImageSize(family, tier, ratio)!.Split('*').Select(int.Parse).ToArray();
            long area = (long)parts[0] * parts[1];
            Assert.InRange(area, 512L * 512, tier == "1K" ? 2_250_000 : 2048L * 2048);
            Assert.InRange((double)parts[0] / parts[1], 1 / 8.0, 8);
            Assert.InRange(Math.Abs((double)parts[0] / parts[1] - ModelFamilies.AspectOf(ratio)), 0, 0.03);
        }
    }

    [Fact]
    public void OpenAI_4k_sizes_fill_the_documented_ceiling()
    {
        var family = ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2.5-sunburst")!;
        foreach (var ratio in family.Ratios)
        {
            var parts = ModelFamilies.ImageSize(family, "4K", ratio)!.Split('x').Select(long.Parse).ToArray();
            Assert.All(parts, edge => Assert.True(edge % 16 == 0 && edge <= 3840, ratio));
            // 21:9 hits the 3840 px edge limit first.
            Assert.InRange((double)(parts[0] * parts[1]) / 8_294_400, ratio is "21:9" or "9:21" ? 0.76 : 0.99, 1);
            Assert.InRange(Math.Abs(Math.Log((double)parts[0] / parts[1] / ModelFamilies.AspectOf(ratio))), 0, 0.005);
        }
    }

    [Fact]
    public void Families_match_the_latest_documented_options()
    {
        var plus = ModelFamilies.FindImage(Vendor.Qwen, "qwen-image-max-2025-12-30")!;
        Assert.Equal("qwen-image-plus", plus.Id);
        Assert.Equal("qwen-image-plus", ModelFamilies.FindImage(Vendor.Qwen, "qwen-image")!.Id);
        Assert.Equal(("1472*1104", "1664*928"), (ModelFamilies.ImageSize(plus, null, "4:3"), ModelFamilies.ImageSize(plus, null, "auto")));
        Assert.Equal((0, 1), (plus.MaxReferences, plus.MaxImages));
        Assert.Equal("qwen-image", ModelFamilies.FindImage(Vendor.Qwen, "qwen-image-2.1-pro")!.Id);
        Assert.Contains("8:1", ModelFamilies.FindImage(Vendor.Google, "gemini-nano-banana-2.1")!.Ratios);
        Assert.Equal(["1k", "1.5k", "2k"], ModelFamilies.FindImage(Vendor.XAI, "grok-imagine-image-2.0")!.Tiers);
    }

    [Fact]
    public void GPT_Image_2_6_has_complexity_gif_svg_and_markup_on_the_Image_API_only()
    {
        foreach (var model in new[] { "gpt-image-2.6-flare", "gpt-image-2.6-sunburst" })
        {
            var family = ModelFamilies.FindImage(Vendor.OpenAI, model)!;
            Assert.Equal("gpt-image-2.6", family.Id);
            Assert.Equal(["low", "medium", "high"], family.Complexities);
            Assert.Contains("max", family.Qualities);
            Assert.Equal(["png", "jpeg", "webp", "gif", "svg"], family.OutputFormats);
            Assert.True(family.Markup);
            Assert.False(family.ResponsesTool);
            Assert.Equal("3840x2160", ModelFamilies.ImageSize(family, "4K", "16:9"));
        }
        var previous = ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2.5-flare")!;
        Assert.Equal((true, true, 0), (previous.Markup, previous.ResponsesTool, previous.Complexities.Length));
        Assert.DoesNotContain("gif", previous.OutputFormats);
        // A node left on the Responses mode falls back to the Image API for 2.6.
        var node = new CanvasNode();
        node.SetOption("openaiApi", "responses");
        Assert.Equal(("images", "responses"), (ModelFamilies.OpenAIApi(node, ModelFamilies.FindImage(Vendor.OpenAI, "gpt-image-2.6-flare")), ModelFamilies.OpenAIApi(node, previous)));
        Assert.Equal("gpt-image-2.6-flare", ModelFamilies.Recommended(Vendor.OpenAI)[0]);
    }

    [Fact]
    public void Svg_size_comes_from_width_and_height_or_the_view_box()
    {
        static (int, int) Size(string svg) => Media.SvgSize(Encoding.UTF8.GetBytes(svg));
        Assert.Equal((800, 600), Size("""<svg xmlns="http://www.w3.org/2000/svg" width="800px" height="600"/>"""));
        Assert.Equal((1536, 1024), Size("""<svg xmlns="http://www.w3.org/2000/svg" width="100%" viewBox="0 0 1536 1024"/>"""));
        Assert.Equal((0, 0), Size("""<svg xmlns="http://www.w3.org/2000/svg"/>"""));
        Assert.Equal((0, 0), Size("not xml"));
        Assert.Equal((0, 0), Media.SvgSize([0x89, 0x50, 0x4E, 0x47]));
        Assert.Equal(("image/svg+xml", ".svg", NodeKind.Image), (Media.MimeOf("图片-003.svg"), Media.Extension("image/svg+xml"), Media.KindOf("a.SVG")));
        Assert.True(Media.IsSvg("图片-003.SVG"));
        Assert.True(ProjectStore.IsMark("标注-001.png") && ProjectStore.IsMark("蒙版-002.png") && !ProjectStore.IsMark("图片-001.png"));
    }

    [Fact]
    public void Top_size_tiers_use_almost_all_of_the_documented_ceiling()
    {
        foreach (var family in ModelFamilies.Image.Where(f => f.MaxPixels > 0))
        foreach (var ratio in family.Ratios.Where(r => r != "auto"))
        {
            var parts = ModelFamilies.ImageSize(family, family.Tiers[^1], ratio)!.Split('x').Select(long.Parse).ToArray();
            Assert.InRange((double)(parts[0] * parts[1]) / family.MaxPixels, 0.97, 1);
            Assert.InRange(Math.Abs((double)parts[0] / parts[1] - ModelFamilies.AspectOf(ratio)), 0, 0.03);
        }
    }

    [Theory]
    [InlineData("doubao-seedream-5-0-pro-260628", "Seedream 5.0 Pro")]
    [InlineData("doubao-seedance-2-0-fast-260128", "Seedance 2.0 Fast")]
    [InlineData("veo-3.1-fast-generate-preview", "Veo 3.1 Fast")]
    [InlineData("gpt-image-2.5-flare", "GPT Image 2.5 Flare")]
    [InlineData("gpt-4o-mini-tts", "GPT 4o Mini TTS")]
    [InlineData("wan2.7-i2v", "Wan2.7 I2V")]
    public void Short_names_drop_vendor_prefixes_and_snapshot_dates(string model, string expected) => Assert.Equal(expected, ModelFamilies.ShortName(model));

    [Fact]
    public void Labels_prefer_the_alias_and_keep_ambiguous_short_names_whole()
    {
        var channel = new ModelChannel
        {
            Id = "c", Vendor = Vendor.Ark,
            Models = [new() { Name = "doubao-seedream-5-0-260128" }, new() { Name = "doubao-seedream-5-0-261015" }, new() { Name = "doubao-seedream-5-0-pro-260628", Alias = "SD 专业版" }, new() { Name = "doubao-seedance-2-5-260628" }],
        };
        var settings = new AppSettings { Channels = [channel] };
        // Two snapshots of one model would both read "Seedream 5.0", so they keep their full IDs.
        Assert.Equal("doubao-seedream-5-0-260128（火山方舟）", settings.Label(ModelRef.Encode("c", "doubao-seedream-5-0-260128")));
        Assert.Equal("SD 专业版", settings.Label(ModelRef.Encode("c", "doubao-seedream-5-0-pro-260628")));
        Assert.Equal("Seedance 2.5（火山方舟）", settings.Label(ModelRef.Encode("c", "doubao-seedance-2-5-260628")));
    }

    [Fact]
    public void Prompt_tags_split_on_common_separators() =>
        Assert.Equal(["人物", "产品图", "cyber"], InfiniteCanvas.Core.Storage.PromptEntry.ParseTags(" 人物，产品图; cyber  人物 "));

    [Theory]
    [InlineData(Vendor.OpenAI, "https://api.openai.com/", "https://api.openai.com/v1")]
    [InlineData(Vendor.OpenAI, "api.openai.com", "https://api.openai.com/v1")]
    [InlineData(Vendor.Google, "https://generativelanguage.googleapis.com", "https://generativelanguage.googleapis.com/v1beta")]
    [InlineData(Vendor.Ark, "https://ark.cn-beijing.volces.com/api/v3", "https://ark.cn-beijing.volces.com/api/v3")]
    [InlineData(Vendor.XAI, "", "https://api.x.ai/v1")]
    [InlineData(Vendor.Qwen, "https://ws1.cn-beijing.maas.aliyuncs.com", "https://ws1.cn-beijing.maas.aliyuncs.com/api/v1")]
    public void Api_base_appends_vendor_version_once(Vendor vendor, string baseUrl, string expected) =>
        Assert.Equal(expected, new ModelChannel { Vendor = vendor, BaseUrl = baseUrl }.ApiBase());

    [Theory]
    [InlineData("qwen3.8-max", true)]
    [InlineData("qwen3-vl-plus", true)]
    [InlineData("qwen-plus", false)]
    [InlineData("qwen-max", false)]
    public void Qwen_text_routes_multimodal_models(string model, bool multimodal) => Assert.Equal(multimodal, ModelFamilies.QwenMultimodal(model));

    [Theory]
    [InlineData("gpt-6-luna", "none")]
    [InlineData("gpt-6.1-sol", "low")]
    [InlineData("gpt-6-astra", "low")]
    [InlineData("gpt-5.5", "none")]
    [InlineData("gpt-5", "minimal")]
    [InlineData("gpt-5-nano", "minimal")]
    [InlineData("o3", "low")]
    [InlineData("gpt-4.1", null)]
    [InlineData("gpt-5-chat-latest", null)]
    public void Mainline_models_default_to_their_lowest_effort(string model, string? lowest) => Assert.Equal(lowest, ModelFamilies.SnapReasoning(model, null));

    [Fact]
    public void Mainline_effort_falls_back_when_the_model_rejects_it()
    {
        Assert.Equal("max", ModelFamilies.SnapReasoning("gpt-6.1-sol", "max"));
        Assert.Equal("low", ModelFamilies.SnapReasoning("gpt-6.1-sol", "none"));
        Assert.Equal("none", ModelFamilies.SnapReasoning("gpt-5.1", "xhigh"));
    }

    [Fact]
    public void Reference_budgets_sit_just_under_each_vendor_limit()
    {
        Assert.Equal(9_500_000, ReferenceLimits.Budget(ReferenceLimits.For(Vendor.Qwen), 1));
        Assert.Equal((long)(30_000_000 * 0.95), ReferenceLimits.Budget(ReferenceLimits.For(Vendor.Ark), 5));
        Assert.Equal((long)(6000L * 6000 * 0.95), ReferenceLimits.PixelBudget(ReferenceLimits.For(Vendor.Ark)));
        // Gemini shares one 100MB request (base64 inflated) across every reference.
        Assert.Equal((long)(75_000_000 / 3 * 0.95), ReferenceLimits.Budget(ReferenceLimits.For(Vendor.Google), 3));
        Assert.Equal(long.MaxValue, ReferenceLimits.PixelBudget(ReferenceLimits.For(Vendor.OpenAI)));
    }

    [Fact]
    public void Image_geometry_crops_and_splits_without_losing_pixels()
    {
        Assert.Equal(new PixelRect(250, 0, 1000, 1000), ImageGeometry.CenteredCrop(1500, 1000, 1));
        Assert.Equal(new PixelRect(0, 78, 1500, 844), ImageGeometry.CenteredCrop(1500, 1000, 16 / 9d));
        Assert.Equal(new PixelRect(1499, 990, 1, 10), ImageGeometry.Clamp(new PixelRect(2000, 990, 50, 50), 1500, 1000));
        var tiles = ImageGeometry.Split(1001, 700, 3, 4);
        Assert.Equal(12, tiles.Count);
        Assert.Equal(1001L * 700, tiles.Sum(t => (long)t.Width * t.Height));
        Assert.Equal((750, 466, 251, 234), (tiles[^1].X, tiles[^1].Y, tiles[^1].Width, tiles[^1].Height));
        Assert.Contains("向左旋转 30 度", ImageGeometry.AnglePrompt(-30, 15, 3, true));
        Assert.Contains("俯视 15 度", ImageGeometry.AnglePrompt(-30, 15, 3, true));
    }

    [Fact]
    public void Connections_keep_config_nodes_in_the_middle()
    {
        var text = CanvasNode.Create(NodeKind.Text, 0, 0);
        var config = CanvasNode.Create(NodeKind.Config, 0, 0);
        var image = CanvasNode.Create(NodeKind.Image, 0, 0);
        Assert.Equal((text.Id, config.Id), CanvasGraph.NormalizeConnection(text, config, false));
        Assert.Equal((config.Id, image.Id), CanvasGraph.NormalizeConnection(config, image, true));
        Assert.Equal((image.Id, config.Id), CanvasGraph.NormalizeConnection(config, image, false));
        Assert.Null(CanvasGraph.NormalizeConnection(config, CanvasNode.Create(NodeKind.Config, 0, 0), true));
        var group = new CanvasNode { Kind = NodeKind.Group };
        Assert.Equal((group.Id, image.Id), CanvasGraph.NormalizeConnection(image, group, false));
        Assert.Null(CanvasGraph.NormalizeConnection(image, group, true));
        Assert.Null(CanvasGraph.NormalizeConnection(group, image, false));
        Assert.Null(CanvasGraph.NormalizeConnection(new CanvasNode { Kind = NodeKind.Image, GroupId = group.Id }, group, false));
    }

    [Fact]
    public void Upstream_resources_expand_groups_and_number_prompts()
    {
        var doc = new CanvasDocument();
        var group = new CanvasNode { Kind = NodeKind.Group };
        var a = new CanvasNode { Kind = NodeKind.Text, Content = "第一段", GroupId = group.Id };
        var b = new CanvasNode { Kind = NodeKind.Image, Content = "x.png", GroupId = group.Id };
        var c = new CanvasNode { Kind = NodeKind.Text, Content = "第二段" };
        var config = new CanvasNode { Kind = NodeKind.Config };
        foreach (var node in new[] { group, a, b, c, config }) doc.Nodes.Add(node);
        doc.Connections.Add(CanvasConnection.Create(group.Id, config.Id));
        doc.Connections.Add(CanvasConnection.Create(c.Id, config.Id));
        var resources = CanvasGraph.UpstreamResources(doc, config.Id);
        Assert.Equal([a.Id, b.Id, c.Id], resources.Select(r => r.Node.Id));
        var prompt = CanvasGraph.ComposePrompt("画一张海报", resources);
        Assert.Contains("图片1", prompt);
        Assert.Contains("【文本2】\n第二段", prompt);
    }

    [Fact]
    public void Upstream_resources_skip_the_node_inside_its_source_group()
    {
        var doc = new CanvasDocument();
        var group = new CanvasNode { Kind = NodeKind.Group };
        var text = new CanvasNode { Kind = NodeKind.Text, Content = "参考", GroupId = group.Id };
        var config = new CanvasNode { Kind = NodeKind.Config, Prompt = "画海报", GroupId = group.Id };
        foreach (var node in new[] { group, text, config }) doc.Nodes.Add(node);
        doc.Connections.Add(CanvasConnection.Create(group.Id, config.Id));
        Assert.Equal([text.Id], CanvasGraph.UpstreamResources(doc, config.Id).Select(r => r.Node.Id));
    }

    [Fact]
    public void Upstream_resources_ignore_config_prompts()
    {
        var doc = new CanvasDocument();
        var config = new CanvasNode { Kind = NodeKind.Config, Prompt = "一只猫在太空" };
        var image = new CanvasNode { Kind = NodeKind.Image, Content = "o.png" };
        foreach (var node in new[] { config, image }) doc.Nodes.Add(node);
        doc.Connections.Add(CanvasConnection.Create(config.Id, image.Id));
        Assert.Empty(CanvasGraph.UpstreamResources(doc, image.Id));
    }

    [Fact]
    public void Containing_group_is_the_innermost()
    {
        CanvasNode Node(double x) => new() { Kind = NodeKind.Image, X = x, Width = 100, Height = 100 };
        CanvasNode Group(params CanvasNode[] members)
        {
            var rect = CanvasGraph.GroupRect(members);
            return new() { Kind = NodeKind.Group, X = rect.Left, Y = rect.Top, Width = rect.Width, Height = rect.Height };
        }
        var doc = new CanvasDocument();
        var (n1, n2, n3) = (Node(0), Node(200), Node(400));
        foreach (var node in new[] { n1, n2, n3 }) doc.Nodes.Add(node);
        var outer = Group(n1, n2, n3);
        doc.Nodes.Insert(0, outer);
        var inner = Group(n1, n2);
        doc.Nodes.Insert(0, inner);
        Assert.Same(inner, CanvasGraph.ContainingGroup(doc, n1));
        Assert.Same(outer, CanvasGraph.ContainingGroup(doc, n3));
    }

    [Fact]
    public void Match_size_keeps_media_aspect_for_one_dimension()
    {
        CanvasNode Node(NodeKind kind, double w, double h) => new() { Kind = kind, X = 10, Y = 20, Width = w, Height = h };
        var anchor = Node(NodeKind.Image, 400, 300);
        var portrait = Node(NodeKind.Image, 200, 300);
        var text = Node(NodeKind.Text, 340, 240);

        CanvasGraph.MatchSize(anchor, [portrait, text], width: true, height: false);
        Assert.Equal((400d, 600d), (portrait.Width, portrait.Height));
        Assert.Equal((400d, 240d), (text.Width, text.Height));

        CanvasGraph.MatchSize(anchor, [portrait, text], width: false, height: true);
        Assert.Equal((200d, 300d), (portrait.Width, portrait.Height));
        Assert.Equal((400d, 300d), (text.Width, text.Height));
        Assert.Equal((10d, 20d), (portrait.X, portrait.Y));

        CanvasGraph.MatchSize(anchor, [portrait], width: true, height: true);
        Assert.Equal((400d, 300d), (portrait.Width, portrait.Height));
        Assert.True(portrait.FreeResize);
    }

    [Fact]
    public void History_undoes_and_redoes_snapshots()
    {
        var doc = new CanvasDocument();
        var history = new CanvasHistory(doc);
        history.Record();
        doc.Nodes.Add(CanvasNode.Create(NodeKind.Text, 0, 0));
        history.Record();
        doc.Nodes[0].X = 500;
        Assert.True(history.Undo());
        Assert.Equal(-170, doc.Nodes[0].X);
        Assert.True(history.Undo());
        Assert.Empty(doc.Nodes);
        Assert.True(history.Redo());
        Assert.Single(doc.Nodes);
    }

    [Fact]
    public void History_keeps_generating_nodes_live()
    {
        var doc = new CanvasDocument();
        var history = new CanvasHistory(doc);
        var target = CanvasNode.Create(NodeKind.Image, 0, 0);
        target.Status = NodeStatus.Loading;
        doc.Nodes.Add(target);
        history.Record();
        doc.Nodes.Add(CanvasNode.Create(NodeKind.Text, 0, 0));
        Assert.True(history.Undo());
        Assert.Same(target, Assert.Single(doc.Nodes));
        target.Status = NodeStatus.Success;
        Assert.True(history.Redo());
        Assert.Same(target, doc.Nodes[0]);
        Assert.Equal(NodeStatus.Success, target.Status);
    }

    [Fact]
    public void Recycled_assets_cannot_reappear_from_undo_or_redo_but_other_edits_survive()
    {
        var doc = new CanvasDocument();
        var text = CanvasNode.Create(NodeKind.Text, 0, 0);
        var image = CanvasNode.Create(NodeKind.Image, 0, 0);
        text.Content = "before";
        image.Content = "图片-001.png";
        doc.Nodes.Add(text);
        doc.Nodes.Add(image);
        doc.Connections.Add(CanvasConnection.Create(image.Id, text.Id));
        var history = new CanvasHistory(doc);
        history.Record();
        text.Content = "after";
        history.Record();
        image.X = 700;
        Assert.True(history.Undo()); // Put another reference to the image into redo.

        history.ForgetAssets(new HashSet<string> { "图片-001.png" });
        doc.Nodes.Remove(doc.Find(image.Id)!);
        doc.Connections.Clear();
        Assert.True(history.Redo());
        Assert.Equal("after", Assert.Single(doc.Nodes).Content);
        Assert.Empty(doc.Connections);
        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.Equal("before", Assert.Single(doc.Nodes).Content);
        Assert.Empty(doc.Connections);
    }

    [Fact]
    public void Recycling_an_old_version_keeps_the_regenerated_node_in_later_snapshots()
    {
        var doc = new CanvasDocument();
        var image = CanvasNode.Create(NodeKind.Image, 0, 0);
        image.Content = "图片-005.png";
        doc.Nodes.Add(image);
        var history = new CanvasHistory(doc);
        history.Record();
        image.Content = "图片-010.png";
        history.Record();
        var x = image.X;
        image.X += 300;

        // 图片-005 is now off the canvas; deleting it must not take the node that moved on to 图片-010 out of the history.
        history.ForgetAssets(new HashSet<string> { "图片-005.png" });
        Assert.True(history.Undo());
        Assert.Equal(("图片-010.png", x), (Assert.Single(doc.Nodes).Content, doc.Nodes[0].X));
        Assert.True(history.Undo());
        Assert.Empty(doc.Nodes);
        Assert.True(history.Redo());
        Assert.Equal("图片-010.png", Assert.Single(doc.Nodes).Content);
    }

    [Fact]
    public void Recycling_an_off_canvas_file_retires_old_references_before_its_number_is_reused()
    {
        var doc = new CanvasDocument();
        var image = CanvasNode.Create(NodeKind.Image, 0, 0);
        image.Content = "图片-001.png";
        doc.Nodes.Add(image);
        var history = new CanvasHistory(doc);
        history.Record();
        doc.Nodes.Remove(image);
        history.ForgetAssets(new HashSet<string> { "图片-001.png" });
        history.Record();
        var replacement = CanvasNode.Create(NodeKind.Image, 0, 0);
        replacement.Content = "图片-001.png";
        doc.Nodes.Add(replacement);
        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.Empty(doc.Nodes);
        Assert.True(history.Redo());
        Assert.True(history.Redo());
        Assert.Equal(replacement.Id, Assert.Single(doc.Nodes).Id);
    }

    [Fact]
    public void History_coalesces_records_with_the_same_key()
    {
        var doc = new CanvasDocument();
        doc.Nodes.Add(CanvasNode.Create(NodeKind.Text, 0, 0));
        var history = new CanvasHistory(doc);
        history.Record("k");
        doc.Nodes[0].Content = "a";
        history.Record("k");
        doc.Nodes[0].Content = "ab";
        Assert.True(history.Undo());
        Assert.Null(doc.Nodes[0].Content);
        Assert.False(history.Undo());

        history.Record("k");
        history.Record();
        history.Record("k");
        for (var i = 0; i < 3; i++) Assert.True(history.Undo());
        Assert.False(history.Undo());

        history.Record("k");
        Assert.True(history.Undo());
        history.Record("k");
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void History_keeps_at_most_100_steps()
    {
        var doc = new CanvasDocument();
        doc.Nodes.Add(CanvasNode.Create(NodeKind.Text, 0, 0));
        var history = new CanvasHistory(doc);
        for (var i = 1; i <= 101; i++)
        {
            history.Record();
            doc.Nodes[0].X = i;
        }
        for (var i = 0; i < 100; i++) Assert.True(history.Undo());
        Assert.False(history.Undo());
        Assert.Equal(1, doc.Nodes[0].X);
    }

    [Fact]
    public void Usage_ledger_adds_up_per_day_and_capability()
    {
        var channel = "usage-" + Guid.NewGuid().ToString("N");
        UsageStore.Record(channel, "m1", Capability.Image, 3);
        UsageStore.Record(channel, "m1", Capability.Image, 2);
        UsageStore.Record(channel, "m2", Capability.Text, 1);
        UsageStore.Record(channel, "m2", Capability.Audio, 0);
        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal(new Dictionary<Capability, int> { [Capability.Image] = 5, [Capability.Text] = 1 }, UsageStore.Since(channel, today));
        Assert.Empty(UsageStore.Since(channel, today.AddDays(1)));
    }

    [Fact]
    public void Gemini_25_image_output_has_its_own_media_price_in_ledger_and_preview()
    {
        var channel = new ModelChannel { Vendor = Vendor.Google };
        var price = Prices.Default(Vendor.Google, "gemini-2.5-flash-image")!;
        Assert.Equal(30m, price.Media);
        Assert.Equal(0.0387m, Prices.Cost(new TokenUsage(0, 0, 1290), price));
        UsageStore.Record(channel.Id, "gemini-2.5-flash-image", Capability.Image, 100, new TokenUsage(0, 0, 129000));
        Assert.Equal(3.87m, Prices.Estimate(channel, DateOnly.FromDateTime(DateTime.Now)));
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        node.Count = 3;
        Assert.Equal(3 * 0.0387m, Prices.EstimateImage(channel, "gemini-2.5-flash-image", node, 0)!.Amount);
    }

    [Fact]
    public void Gemini_prices_match_the_longest_prefix_and_estimate_the_ledger()
    {
        Assert.Equal(120m, Prices.Default(Vendor.Google, "gemini-3-pro-image-preview")!.Media);
        Assert.Equal(6m, Prices.Default(Vendor.Google, "gemini-3.8-flash-lite-tts")!.Media);
        Assert.Equal(0.30m, Prices.Default(Vendor.Google, "models/gemini-3.5-flash-lite")!.Input);
        Assert.Equal(20m, Prices.Default(Vendor.Google, "gemini-3.1-flash-tts-preview")!.Media);
        Assert.Null(Prices.Default(Vendor.Google, "gemma-4"));
        Assert.Equal(30m, Prices.Default(Vendor.OpenAI, "gpt-image-2.5-flare")!.Output);
        Assert.Equal((5m, 30m), Prices.Default(Vendor.OpenAI, "gpt-image-2.6-sunburst") is { } image26 ? (image26.Input, image26.Output) : default);
        Assert.Null(Prices.Default(Vendor.Ark, "doubao-seedream-5-0-pro-260628"));

        var channel = new ModelChannel { Vendor = Vendor.Google, Models = [new() { Name = "gemini-3-pro-image-preview" }, new() { Name = "gemini-3.8-flash", Price = new(1, 10, 0) }] };
        UsageStore.Record(channel.Id, "gemini-3-pro-image-preview", Capability.Image, 1, new TokenUsage(560, 100, 1120));
        UsageStore.Record(channel.Id, "gemini-3.8-flash", Capability.Text, 1, new TokenUsage(1_000_000, 100_000));
        UsageStore.Record(channel.Id, "unknown-model", Capability.Text, 1, new TokenUsage(1_000_000, 1_000_000));
        // 560×2 + 100×12 + 1120×120 per million for the image, the user's 1 + 0.1×10 for text; the unpriced model is left out.
        Assert.Equal(0.13672m + 2m, Prices.Estimate(channel, DateOnly.FromDateTime(DateTime.Now)));
        Assert.Equal(new TokenUsage(560, 100, 1120), UsageStore.Tokens(channel.Id, DateOnly.FromDateTime(DateTime.Now))["gemini-3-pro-image-preview"]);
    }

    [Fact]
    public void Image_estimates_follow_each_vendors_published_prices()
    {
        static ImageEstimate? Estimate(Vendor vendor, string model, int references = 0, string? size = null, string? ratio = null, string? quality = null, int count = 1, string prompt = "") =>
            Prices.EstimateImage(new ModelChannel { Vendor = vendor }, model, new CanvasNode { Kind = NodeKind.Config, Size = size, Ratio = ratio, Quality = quality, Count = count, Prompt = prompt }, references);

        // Ark bills per picture; Seedream 5.0 pro doubles above 1.5K and charges references after the first, once per request.
        Assert.Equal((1.24m, "CNY"), Estimate(Vendor.Ark, "doubao-seedream-5-0-pro-260628", references: 2, size: "2K", count: 2) is { } ark ? (ark.Amount, ark.Currency) : default);
        Assert.Equal(0.22m, Estimate(Vendor.Ark, "doubao-seedream-5-0-260128")!.Amount);
        Assert.Equal(0.27m, Estimate(Vendor.Qwen, "qwen-image-3.0-pro", references: 1, size: "1K")!.Amount);
        Assert.Equal(0.50m, Estimate(Vendor.Qwen, "qwen-image-3.0-pro", size: "2K")!.Amount);
        Assert.Equal((0.12m, "USD"), Estimate(Vendor.XAI, "grok-imagine-image-2.0", count: 3) is { } xai ? (xai.Amount, xai.Currency) : default);
        // Gemini: per-tier image tokens at the image output price, plus prompt tokens (one per CJK character).
        Assert.Equal(0.240002m, Estimate(Vendor.Google, "gemini-3-pro-image-preview", size: "4K", prompt: "猫")!.Amount);
        // OpenAI: the published per-size table at 1024 sizes.
        Assert.Equal(0.106m, Estimate(Vendor.OpenAI, "gpt-image-2", size: "1K", ratio: "1:1", quality: "medium", count: 2)!.Amount);
        Assert.Null(Estimate(Vendor.OpenAI, "gpt-image-2", size: "2K", ratio: "1:1", quality: "medium"));
        // GPT Image 2.6 has no per-image table, so gpt-image-2's must not apply to it.
        Assert.Null(Estimate(Vendor.OpenAI, "gpt-image-2.6-flare", size: "1K", ratio: "1:1", quality: "medium"));
        Assert.Null(Estimate(Vendor.Ark, "doubao-seedream-9"));

        // No published price for GPT Image 2.5: the last run with the same settings is used once there is one.
        var ratio = Guid.NewGuid().ToString("N");
        Assert.Null(Estimate(Vendor.OpenAI, "gpt-image-2.5-flare", size: "1K", ratio: ratio, quality: "medium"));
        UsageStore.RememberImage(Prices.ImageKey("gpt-image-2.5-flare", new CanvasNode { Size = "1K", Ratio = ratio, Quality = "medium" }), new TokenUsage(40, 439));
        var learned = Estimate(Vendor.OpenAI, "gpt-image-2.5-flare", size: "1K", ratio: ratio, quality: "medium", count: 2)!;
        Assert.Equal(0.02674m, learned.Amount);
        Assert.Contains("上次", learned.Basis);
    }

    [Fact]
    public void Billing_credentials_are_encrypted_and_signed_vendors_need_the_id()
    {
        var channel = new ModelChannel { Vendor = Vendor.Ark, BillingKey = "secret" };
        Assert.NotEqual("secret", channel.ProtectedBillingKey);
        Assert.Equal("secret", channel.BillingKey);
        Assert.False(channel.HasBilling);
        channel.BillingId = "AK";
        Assert.True(channel.HasBilling);
        Assert.True(new ModelChannel { Vendor = Vendor.OpenAI, BillingKey = "x" }.HasBilling);
        Assert.False(new ModelChannel { Vendor = Vendor.Google, BillingKey = "x" }.HasBilling);
    }

    [Fact]
    public void Prune_removes_unused_masks_but_keeps_media_deleted_from_the_canvas()
    {
        var doc = new CanvasDocument();
        var image = new CanvasNode { Kind = NodeKind.Image, Content = "图片-001.png" };
        image.SetOption(NodeKeys.Mask, "蒙版-001.png");
        image.SetOption(NodeKeys.Markup, "标注-001.png");
        doc.Nodes.Add(image);
        var dir = ProjectStore.AssetsDir(doc.Id);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "图片-001.png", "图片-002.png", "蒙版-001.png", "蒙版-002.png", "标注-001.png", "标注-002.png" }) File.WriteAllText(Path.Combine(dir, name), "");
            ProjectStore.PruneMasks(doc);
            Assert.Equal(["图片-001.png", "图片-002.png", "标注-001.png", "蒙版-001.png"], Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}
