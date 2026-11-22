using Tabula.Core.Canvas;
using Tabula.Core.Settings;
using Tabula.Core.Storage;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.Core;

namespace Tabula.App.Board;

/// <summary>
/// Searchable outline drawer (R7): lists groups (with members), foldable batches, and loose nodes by kind.
/// Click locates a node, Ctrl/Shift-click toggles selection, double-click opens a picture, right-click opens the node menu.
/// </summary>
public sealed class OutlinePanel : UserControl
{
    private readonly Border _shell;
    private readonly TextBlock _countText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _searchBox = new() { PlaceholderText = "按标题、提示词或文字搜索", FontSize = 13 };
    private readonly StackPanel _rows = new() { Spacing = 2, Padding = new(0, 4, 0, 8) };
    private readonly TextBlock _emptyText = new() { Text = "画布上还没有节点", FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(12, 24, 12, 12), Visibility = Visibility.Collapsed };
    private readonly HashSet<string> _collapsed = [];
    private readonly Dictionary<string, (Button Row, TextBlock Title)> _nodeRows = [];
    private CanvasView? _board;

    public event Action? CloseRequested;

    public OutlinePanel()
    {
        _countText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _emptyText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        AutomationProperties.SetName(_searchBox, "搜索大纲");
        _searchBox.TextChanged += (_, _) => Refresh();

        var close = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Padding = new(9, 7, 9, 7),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 13 },
        };
        AutomationProperties.SetName(close, "关闭大纲");
        ToolTipService.SetToolTip(close, "关闭大纲");
        close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new(2, 0, 0, 0),
            Children =
            {
                new FontIcon { Glyph = "\uE8FD", FontSize = 15 },
                new TextBlock { Text = "大纲", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                _countText,
            },
        });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var body = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Grid { Children = { _rows, _emptyText } },
        };

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(_searchBox, 1);
        root.Children.Add(_searchBox);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        _shell = new Border { Style = (Style)Application.Current.Resources["FloatingPanel"], Padding = new(12, 10, 12, 10), Child = root };
        Content = _shell;
        ActualThemeChanged += (_, _) => Refresh();
    }

    public void Attach(CanvasView board)
    {
        _board = board;
        board.Changed += () => { if (Visibility == Visibility.Visible) Refresh(); };
        board.SelectionChanged += () => { if (Visibility == Visibility.Visible) UpdateSelection(); };
        board.RunningChanged += () => { if (Visibility == Visibility.Visible) Refresh(); };
    }

    public void CastShadowOn(UIElement receiver)
    {
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(receiver);
        _shell.Shadow = shadow;
        _shell.Translation = new System.Numerics.Vector3(0, 0, 24);
    }

    public void FocusSearch() => _searchBox.Focus(FocusState.Programmatic);

    public void Refresh()
    {
        if (_board is null) return;
        var doc = _board.Doc;
        var total = doc.Nodes.Count(n => n.Kind != NodeKind.Group);
        _countText.Text = $"{total} 个节点";

        var items = Outline.Build(doc, _searchBox.Text, _collapsed);
        _rows.Children.Clear();
        _nodeRows.Clear();
        _emptyText.Text = string.IsNullOrWhiteSpace(_searchBox.Text) ? "画布上还没有节点" : "没有匹配的节点";
        _emptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selected = _board.Selection.ToHashSet();
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        foreach (var row in items)
        {
            switch (row.Kind)
            {
                case OutlineRowKind.Header:
                    _rows.Children.Add(new TextBlock
                    {
                        Text = $"{row.Label} · {row.Count}",
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = secondary,
                        Margin = new(6, _rows.Children.Count == 0 ? 2 : 10, 6, 2),
                    });
                    break;

                case OutlineRowKind.Batch:
                    var batchId = row.BatchId!;
                    var batchGrid = new Grid { ColumnSpacing = 6 };
                    batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var chevron = new FontIcon { Glyph = row.Expanded ? "\uE70D" : "\uE76C", FontSize = 10, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
                    var stackIcon = new FontIcon { Glyph = "\uE8F1", FontSize = 13, Foreground = new SolidColorBrush(Palette.Kind(NodeKind.Image, ActualTheme)), VerticalAlignment = VerticalAlignment.Center };
                    var batchText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    batchText.Children.Add(new TextBlock { Text = row.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (!string.IsNullOrWhiteSpace(row.Subtitle))
                        batchText.Children.Add(new TextBlock { Text = SettingsStore.Current.Label(row.Subtitle), FontSize = 11, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(stackIcon, 1);
                    Grid.SetColumn(batchText, 2);
                    batchGrid.Children.Add(chevron);
                    batchGrid.Children.Add(stackIcon);
                    batchGrid.Children.Add(batchText);

                    var batchBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["ToolbarButton"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new(8 + row.Depth * 14, 5, 8, 5),
                        CornerRadius = new(8),
                        Content = batchGrid,
                    };
                    AutomationProperties.SetName(batchBtn, row.Label);
                    batchBtn.Click += (_, _) =>
                    {
                        if (!_collapsed.Add(batchId)) _collapsed.Remove(batchId);
                        Refresh();
                    };
                    _rows.Children.Add(batchBtn);
                    break;

                case OutlineRowKind.Node when row.Node is { } node:
                    var isSel = selected.Contains(node.Id);
                    var nodeGrid = new Grid { ColumnSpacing = 8 };
                    nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    UIElement icon;
                    if (node.Kind == NodeKind.Image && node.HasContent && ProjectStore.AssetPath(doc.Id, node.Content!) is { } path && File.Exists(path))
                    {
                        ImageSource src = Media.IsSvg(path) ? new SvgImageSource(new Uri(path)) : new BitmapImage { DecodePixelWidth = 48, UriSource = new Uri(path) };
                        icon = new Border { Width = 24, Height = 24, CornerRadius = new(5), Child = new Image { Source = src, Stretch = Stretch.UniformToFill } };
                    }
                    else
                    {
                        var glyph = node.Kind switch
                        {
                            NodeKind.Image => "\uEB9F",
                            NodeKind.Text => "\uE8D2",
                            NodeKind.Config => "\uE945",
                            NodeKind.Video => "\uE714",
                            NodeKind.Audio => "\uE8D6",
                            NodeKind.Group => "\uE8B7",
                            _ => "\uE946",
                        };
                        icon = new Border
                        {
                            Width = 24, Height = 24, CornerRadius = new(5),
                            Background = new SolidColorBrush(Palette.Kind(node.Kind, ActualTheme)),
                            Child = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                        };
                    }

                    var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var titleBlock = new TextBlock { Text = row.Label, FontSize = 12.5, FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
                    textCol.Children.Add(titleBlock);
                    if (!string.IsNullOrWhiteSpace(row.Subtitle))
                        textCol.Children.Add(new TextBlock { Text = row.Subtitle, FontSize = 11, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(textCol, 1);
                    nodeGrid.Children.Add(icon);
                    nodeGrid.Children.Add(textCol);

                    if (_board.Generator.IsRunning(node.Id))
                    {
                        var badge = new TextBlock { Text = "生成中", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] };
                        Grid.SetColumn(badge, 2);
                        nodeGrid.Children.Add(badge);
                    }
                    else if (node.Status == NodeStatus.Error && !node.HasContent)
                    {
                        var badge = new TextBlock { Text = "失败", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
                        Grid.SetColumn(badge, 2);
                        nodeGrid.Children.Add(badge);
                    }

                    var btn = new Button
                    {
                        Style = (Style)Application.Current.Resources["ToolbarButton"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new(8 + row.Depth * 14, 5, 8, 5),
                        CornerRadius = new(8),
                        Content = nodeGrid,
                    };
                    if (isSel) btn.Background = activeBg;
                    AutomationProperties.SetName(btn, row.Label);
                    ToolTipService.SetToolTip(btn, row.Subtitle is null ? row.Label : $"{row.Label}\n{row.Subtitle}");

                    btn.Click += (_, _) =>
                    {
                        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                        if (ctrl || shift) _board.ToggleSelect(node.Id);
                        else _board.FocusNode(node);
                    };
                    btn.DoubleTapped += (_, e) =>
                    {
                        e.Handled = true;
                        if (node is { Kind: NodeKind.Image, HasContent: true }) _board.OpenPicture(node);
                        else if (node.Kind != NodeKind.Group) _ = _board.RenameAsync(node);
                    };
                    btn.RightTapped += (_, e) =>
                    {
                        e.Handled = true;
                        if (!_board.Selection.Contains(node.Id)) _board.Select([node.Id]);
                        _board.ShowNodeMenu(btn);
                    };
                    _nodeRows[node.Id] = (btn, titleBlock);
                    _rows.Children.Add(btn);
                    break;
            }
        }
    }

    private void UpdateSelection()
    {
        if (_board is null) return;
        var selected = _board.Selection.ToHashSet();
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var (id, (row, title)) in _nodeRows)
        {
            var isSel = selected.Contains(id);
            if (isSel) row.Background = activeBg;
            else row.ClearValue(Control.BackgroundProperty);
            title.FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
