using System.Text;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;

namespace InfiniteCanvas.Tests;

internal static class TestEnvironment
{
    /// <summary>Point ProjectStore.Root at a throwaway folder before any test runs, so no test touches the real data folder.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Isolate() => Environment.SetEnvironmentVariable("INFINITE_CANVAS_DATA", Path.Combine(Path.GetTempPath(), "InfiniteCanvas.Tests"));
}

public class CoreTests
{
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
            File.WriteAllBytes(plain, [1]);
            Assert.True(ProjectStore.RecycleSilently(plain));
            Assert.False(File.Exists(plain));
            Assert.True(ProjectStore.RecycleSilently(plain)); // already gone

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
