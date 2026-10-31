using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace InfiniteCanvas.App.Board;

/// <summary>A library file, or a node already on the canvas (画布内容) with its media file, if it has one.</summary>
public sealed class LibraryEntry(string path, string name, NodeKind kind, CanvasNode? node = null) : ObservableObject
{
    private ImageSource? _thumbnail;
    public string Path { get; } = path;
    public string Name { get; } = name;
    public NodeKind Kind { get; } = kind;
    public CanvasNode? Node { get; } = node;
    public string? Snippet { get; } = node?.Kind == NodeKind.Text ? node.Content : null;
    public string Glyph => NodeView.Glyph(Kind);
    public bool ShowGlyph => Snippet is null;
    public bool IsVideo => Kind == NodeKind.Video;
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
}

/// <summary>
/// The canvas's own media library (drag items onto the canvas, double-click to drop them at the view centre),
/// and a second tab listing what is already on the canvas: click an item to find it there.
/// </summary>
public sealed partial class LibraryPanel : UserControl
{
    private List<LibraryEntry> _library = [];
    private List<LibraryEntry> _canvas = [];
    private string? _canvasKeys;

    public LibraryPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LibraryStore.Changed += Reload;
            if (Target is { } canvas) canvas.Changed += CanvasChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            LibraryStore.Changed -= Reload;
            if (Target is { } canvas) canvas.Changed -= CanvasChanged;
        };
    }

    /// <summary>The canvas whose library and content the panel shows, and which receives double-clicked items.</summary>
    public CanvasView? Target { get; set; }

    private bool ShowingCanvas => Tabs.SelectedItem?.Tag as string == "Canvas";

    public void CastShadowOn(UIElement receiver)
    {
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(receiver);
        Shell.Shadow = shadow;
        Shell.Translation = new System.Numerics.Vector3(0, 0, 24);
    }

    /// <summary>Read the library folder again (files may have been added in Explorer) and rebuild the canvas content.</summary>
    public void Refresh()
    {
        _canvasKeys = null;
        Reload();
    }

    private void Reload()
    {
        if (Target is null) return;
        _library = LibraryStore.List(Target.Doc.Id).Select(item => Thumbnailed(new LibraryEntry(item.Path, item.Name, item.Kind))).ToList();
        ReloadCanvas();
        ApplyFilter();
    }

    private void CanvasChanged()
    {
        if (ShowingCanvas && ReloadCanvas()) ApplyFilter();
    }

    /// <summary>
    /// Media and text nodes, newest first; a stack's lead goes by its own number like the rest of its batch. Canvas edits arrive
    /// constantly (every drag step), so the list is rebuilt only when an entry changes, and unchanged entries keep their thumbnails.
    /// </summary>
    private bool ReloadCanvas()
    {
        if (Target is null) return false;
        var doc = Target.Doc;
        var batches = doc.Nodes.Where(n => Stacks.Of(n) is not null).GroupBy(n => Stacks.Of(n)!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var nodes = doc.Nodes.Where(n => n.HasContent && n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio or NodeKind.Text).Reverse()
            .Select(n => (Node: n, Name: Stacks.Of(n) is { } id && batches.Contains(id) ? Stacks.OwnTitle(n) : n.Title))
            .Select(e => (e.Node, e.Name, Key: Key(e.Node, e.Name)))
            .ToList();
        var keys = string.Join('\n', nodes.Select(e => e.Key));
        if (keys == _canvasKeys) return false;
        _canvasKeys = keys;
        var old = _canvas.ToDictionary(e => Key(e.Node!, e.Name));
        _canvas = nodes.Select(e => old.GetValueOrDefault(e.Key)
            ?? Thumbnailed(new LibraryEntry(e.Node.Kind == NodeKind.Text ? "" : ProjectStore.AssetPath(doc.Id, e.Node.Content!), e.Name, e.Node.Kind, e.Node))).ToList();
        return true;
    }

    private static string Key(CanvasNode node, string name) => $"{node.Id}|{node.Content}|{name}";

    private static LibraryEntry Thumbnailed(LibraryEntry entry)
    {
        _ = LoadThumbnailAsync(entry);
        return entry;
    }

    private void ApplyFilter()
    {
        var canvas = ShowingCanvas;
        var tag = Filter.SelectedItem?.Tag as string ?? "All";
        var all = canvas ? _canvas : _library;
        var items = tag == "All" ? all : all.Where(e => e.Kind.ToString() == tag).ToList();
        Items.ItemsSource = items;
        EmptyText.Text = canvas ? "画布上还没有图片、视频、音频或文本。" : "把这个画布常用的图片、视频或音频拖到这里，或点右上角 + 导入。\n之后可以直接拖到画布上使用。";
        UsageHint.Text = canvas ? "单击在画布上找到它，拖到画布上可复制一份" : "双击放到画布中央，或拖到画布任意位置";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageHint.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static async Task LoadThumbnailAsync(LibraryEntry entry)
    {
        try
        {
            if (entry.Kind == NodeKind.Image)
            {
                // Library files keep their names, so the same path may hold new content. 200 wide lets a 2:1 image fill the 132 x 100 cell without upscaling.
                entry.Thumbnail = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 200, UriSource = new Uri(entry.Path) };
                return;
            }
            if (entry.Kind != NodeKind.Video) return;
            var file = await StorageFile.GetFileFromPathAsync(entry.Path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 264);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb);
            entry.Thumbnail = bitmap;
        }
        catch { /* No thumbnail: the type icon stays visible. */ }
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var canvas = ShowingCanvas;
        ImportButton.Visibility = FolderButton.Visibility = canvas ? Visibility.Collapsed : Visibility.Visible;
        // Only canvas items are clicked to find them; library items keep plain selection for multi-item drags.
        Items.IsItemClickEnabled = canvas;
        TextFilter.Visibility = canvas ? Visibility.Visible : Visibility.Collapsed;
        if (!canvas && Filter.SelectedItem == TextFilter) Filter.SelectedItem = Filter.Items[0];
        ReloadCanvas();
        ApplyFilter();
    }

    private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ApplyFilter();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    // Text nodes have no file to copy, so a drag of only text does nothing.
    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<LibraryEntry>().Select(i => i.Path).Where(p => p.Length > 0).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        e.Data.SetData(CanvasView.LibraryFormat, string.Join('\n', paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryEntry { Node: { } node }) Target?.Locate(node);
    }

    private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryEntry { Node: null } entry && Target is { } canvas)
            await canvas.ImportFilesAsync([entry.Path], canvas.ViewCenterWorld);
    }

    // Right click, Shift+F10 and the menu key all land here; from the keyboard the source is the focused GridViewItem.
    private void Items_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: LibraryEntry entry } target || Target is not { } canvas) return;
        var menu = new MenuFlyout();
        if (entry.Node is { } node)
        {
            menu.Items.Add(CanvasView.Item("在画布上找到", "", () => canvas.Locate(node)));
            if (entry.Path.Length > 0) menu.Items.Add(CanvasView.Item("加入资料库", "", () => canvas.AddToLibraryAsync(node)));
        }
        else menu.Items.Add(CanvasView.Item("放到画布中央", "", () => canvas.ImportFilesAsync([entry.Path], canvas.ViewCenterWorld)));
        if (entry.Path.Length > 0) menu.Items.Add(CanvasView.Item("在资源管理器中显示", "", () => Process.Start("explorer.exe", $"/select,\"{entry.Path}\"")));
        if (entry.Node is null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CanvasView.Item("从资料库删除（移到回收站）", "", () => LibraryStore.Remove(entry.Path)));
        }
        if (e.TryGetPosition(target, out var point)) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
        e.Handled = true;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".mp4", ".mov", ".webm", ".mp3", ".wav", ".m4a", ".aac", ".flac" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
            await ImportAsync(await picker.PickMultipleFilesAsync());
        }
        catch (Exception ex) { ImportFailed(ex); }
    }

    private async Task ImportAsync(IReadOnlyList<IStorageItem> items)
    {
        if (items.Count == 0 || Target is null) return;
        SetImportStatus($"正在导入 {items.Count} 项…");
        var added = await LibraryStore.AddAsync(Target.Doc.Id, items.OfType<StorageFile>().Select(f => f.Path).ToList());
        var skipped = items.Count - added;
        SetImportStatus($"已导入 {added} 项，跳过 {skipped} 项" + (skipped > 0 ? "（不支持的类型或文件无法读取）" : ""));
    }

    private void SetImportStatus(string text)
    {
        ImportStatus.Text = text;
        ImportStatus.Visibility = Visibility.Visible;
    }

    private void ImportFailed(Exception ex)
    {
        App.Log(ex);
        SetImportStatus($"导入未完成：{ex.Message}");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Target is null) return;
        var dir = ProjectStore.LibraryDir(Target.Doc.Id);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // Files dropped from Explorer go into the library; items dragged out of the panel itself are ignored here.
    private void Shell_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(CanvasView.LibraryFormat)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "加入资料库";
        e.Handled = true;
    }

    private async void Shell_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(CanvasView.LibraryFormat)) return;
        e.Handled = true;
        if (ShowingCanvas) Tabs.SelectedItem = Tabs.Items[0];
        try { await ImportAsync(await e.DataView.GetStorageItemsAsync()); }
        catch (Exception ex) { ImportFailed(ex); }
    }
}
