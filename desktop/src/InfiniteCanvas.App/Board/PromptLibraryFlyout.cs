using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The generation panel's local prompt library: search, filter by tag, click an entry to put it into the prompt,
/// save the current prompt, and edit or delete entries from their context menu.
/// </summary>
internal sealed class PromptLibraryFlyout : Flyout
{
    private const string AllTags = "全部标签";
    private readonly TextBox _search = new() { PlaceholderText = "搜索标题、内容或标签" };
    private readonly ComboBox _tags = new() { MinWidth = 110 };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly Func<string> _current;
    private readonly Action<string> _use;
    private bool _refreshing;

    public PromptLibraryFlyout(Func<string> current, Action<string> use)
    {
        _current = current;
        _use = use;
        Placement = FlyoutPlacementMode.RightEdgeAlignedBottom;
        AutomationProperties.SetName(_search, "搜索提示词");
        AutomationProperties.SetName(_tags, "按标签筛选");
        var save = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Padding = new(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        save.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "保存当前提示词" } } };
        AutomationProperties.SetName(save, "保存当前提示词");
        save.Click += async (_, _) => await EditAsync(null);
        var top = new Grid { ColumnSpacing = 6 };
        top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(_search);
        Grid.SetColumn(_tags, 1);
        top.Children.Add(_tags);
        Content = new StackPanel { Width = 360, Spacing = 8, Children = { top, new ScrollViewer { MaxHeight = 380, Padding = new(0, 0, 12, 0), Content = _list }, save } };
        Opening += (_, _) => Refresh();
        _search.TextChanged += (_, _) => Refresh();
        _tags.SelectionChanged += (_, _) => { if (!_refreshing) Refresh(); };
    }

    private void Refresh()
    {
        _refreshing = true;
        var tag = _tags.SelectedItem as string is { } chosen && chosen != AllTags ? chosen : null;
        var tags = PromptStore.Tags.Prepend(AllTags).ToList();
        _tags.ItemsSource = tags;
        _tags.SelectedItem = tag is not null && tags.Contains(tag) ? tag : AllTags;
        _tags.Visibility = tags.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = false;

        _list.Children.Clear();
        var entries = PromptStore.Search(_search.Text.Trim(), _tags.SelectedItem as string is AllTags ? null : _tags.SelectedItem as string).ToList();
        if (entries.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = PromptStore.Items.Count == 0 ? "还没有保存的提示词。写好提示词后点「保存当前提示词」，以后在任何画布都能一键填入。" : "没有匹配的提示词。",
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryCaption"], Margin = new(4, 8, 4, 8),
            });
        foreach (var entry in entries) _list.Children.Add(Row(entry));
    }

    private Button Row(PromptEntry entry)
    {
        var caption = (Style)Application.Current.Resources["SecondaryCaption"];
        var body = new StackPanel { Spacing = 2, Children =
        {
            new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = entry.Text.Replace('\n', ' '), Style = caption, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis },
        } };
        if (entry.Tags.Count > 0) body.Children.Add(new TextBlock { Text = string.Join("  ", entry.Tags.Select(t => "#" + t)), Style = caption, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"], Content = body, Padding = new(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        ToolTipService.SetToolTip(row, "点击填入提示词，右键编辑或删除");
        AutomationProperties.SetName(row, entry.Title);
        row.Click += (_, _) =>
        {
            Hide();
            _use(entry.Text);
        };
        var menu = new MenuFlyout();
        menu.Items.Add(CanvasView.Item("编辑…", "", () => EditAsync(entry)));
        menu.Items.Add(CanvasView.Item("删除", "", () => DeleteAsync(entry)));
        row.ContextFlyout = menu;
        return row;
    }

    /// <summary>Save the current prompt as a new entry (entry null) or edit an existing one.</summary>
    private async Task EditAsync(PromptEntry? entry)
    {
        var text = entry?.Text ?? _current().Replace('\r', '\n').Trim();
        var title = new TextBox { Header = "标题", Text = entry?.Title ?? Excerpt(text), PlaceholderText = "留空时取提示词开头" };
        // AcceptsReturn must come before Text: a single-line box drops everything after the first line break.
        var prompt = new TextBox { Header = "提示词", AcceptsReturn = true, Text = NodeView.BoxText(text), TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 280 };
        ScrollViewer.SetVerticalScrollBarVisibility(prompt, ScrollBarVisibility.Auto);
        var tags = new TextBox { Header = "标签（逗号或空格分隔，可留空）", Text = string.Join(", ", entry?.Tags ?? []), PlaceholderText = "例如：人物, 产品图" };
        Hide();
        var form = new StackPanel { Spacing = 10, Width = 440, Children = { title, prompt, tags } };
        if (!await Dialogs.FormAsync(entry is null ? "保存到提示词库" : "编辑提示词", form, "保存")) return;
        var value = prompt.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (value.Length == 0)
        {
            await Dialogs.MessageAsync("提示词为空", "没有可保存的内容。");
            return;
        }
        var target = entry ?? new PromptEntry();
        target.Title = title.Text.Trim() is { Length: > 0 } named ? named : Excerpt(value);
        target.Text = value;
        target.Tags = PromptEntry.ParseTags(tags.Text);
        target.Updated = DateTime.Now;
        if (entry is null) PromptStore.Items.Add(target);
        await SaveAsync();
    }

    private async Task DeleteAsync(PromptEntry entry)
    {
        Hide();
        if (!await Dialogs.ConfirmAsync("删除提示词", $"从提示词库删除「{entry.Title}」？", "删除", destructive: true)) return;
        PromptStore.Items.Remove(entry);
        await SaveAsync();
    }

    private static async Task SaveAsync()
    {
        try { PromptStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dialogs.MessageAsync("保存提示词库失败", ex.Message); }
    }

    private static string Excerpt(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 24 ? line[..24] + "…" : line;
    }
}
