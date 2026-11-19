using InfiniteCanvas.Core.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The Ctrl+K palette: a glass box over the canvas that lists every command the current selection allows plus the canvas-level ones and
/// the places to go (<see cref="CanvasCommands.Palette"/>), filtered as you type. Enter runs the highlighted command, Esc or a click outside closes.
/// Names, groups and shortcuts come from the command table, so they equal the menus.
/// </summary>
public sealed class CommandPalette : UserControl
{
    private readonly TextBox _query = new() { PlaceholderText = "搜索命令…", MinHeight = 36 };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, MaxHeight = 340 };
    private readonly Border _shell = new() { Style = (Style)Application.Current.Resources["FloatingPanel"], Padding = new Thickness(8), Width = 560, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
    private Func<CommandContext>? _context;
    private Func<string, Task>? _run;

    public CommandPalette()
    {
        AutomationProperties.SetName(_query, "搜索命令");
        AutomationProperties.SetName(_list, "命令列表");
        AutomationProperties.SetName(_shell, "命令");
        _shell.Child = new StackPanel { Spacing = 6, Children = { _query, _list } };
        // The surrounding area is transparent but takes clicks, so a click outside the box closes it.
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _shell } };
        ((Grid)Content).Tapped += (_, e) => { if (ReferenceEquals(e.OriginalSource, Content)) Close(); };
        Visibility = Visibility.Collapsed;
        _query.TextChanged += (_, _) => Fill();
        _query.PreviewKeyDown += Query_PreviewKeyDown;
        // The clicked item is the row itself or, for a ListViewItem, its content: both carry the command.
        _list.ItemClick += async (_, e) => { if (e.ClickedItem is FrameworkElement { Tag: CommandSpec spec }) await RunAsync(spec); };
        Motion.ShowHide(this, 0.5f, 0, 0.97f);
    }

    /// <summary>The page supplies how to read the current selection and how to run a command by its id (the page's own panels included).</summary>
    public void Attach(Func<CommandContext> context, Func<string, Task> run) => (_context, _run) = (context, run);

    /// <summary>Gives the box a shadow cast onto the canvas, like the other floating panels.</summary>
    public void CastShadowOn(UIElement receiver)
    {
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(receiver);
        _shell.Shadow = shadow;
        _shell.Translation = new System.Numerics.Vector3(0, 0, 24);
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Open()
    {
        if (_context is null) return;
        _query.Text = "";
        Fill();
        Visibility = Visibility.Visible;
        _query.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
    }

    private void Fill()
    {
        if (_context is null) return;
        _list.Items.Clear();
        foreach (var spec in CanvasCommands.Palette(_context(), _query.Text.Trim())) _list.Items.Add(Row(spec));
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
    }

    private static ListViewItem Row(CommandSpec spec)
    {
        var grid = new Grid { Tag = spec, ColumnSpacing = 10, ColumnDefinitions = { new() { Width = new GridLength(24) }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } } };
        grid.Children.Add(new FontIcon { Glyph = spec.Glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = spec.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        var group = new TextBlock { Text = CanvasCommands.GroupName(spec.Group), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryCaption"] };
        Grid.SetColumn(group, 2);
        var keys = new TextBlock { Text = spec.Shortcut ?? "", VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Right, Style = (Style)Application.Current.Resources["SecondaryCaption"] };
        Grid.SetColumn(keys, 3);
        grid.Children.Add(name);
        grid.Children.Add(group);
        grid.Children.Add(keys);
        var item = new ListViewItem { Content = grid, Tag = spec };
        AutomationProperties.SetName(item, spec.Name);
        return item;
    }

    private async void Query_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape: e.Handled = true; Close(); break;
            case VirtualKey.Down or VirtualKey.Up when _list.Items.Count > 0:
                e.Handled = true;
                _list.SelectedIndex = (_list.SelectedIndex + (e.Key == VirtualKey.Down ? 1 : -1) + _list.Items.Count) % _list.Items.Count;
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            case VirtualKey.Enter when _list.SelectedItem is ListViewItem { Tag: CommandSpec spec }: e.Handled = true; await RunAsync(spec); break;
        }
    }

    private async Task RunAsync(CommandSpec spec)
    {
        Close();
        if (_run is not null) await _run(spec.Id);
    }
}
