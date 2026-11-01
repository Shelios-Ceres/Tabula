using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;

namespace InfiniteCanvas.App.Board;

/// <summary>A library file, or a file of the canvas's folder (画布文件) with the first node showing it, if any.</summary>
public sealed class LibraryEntry(string path, string name, NodeKind kind, bool canvasFile = false, CanvasNode? node = null) : ObservableObject
{
    private ImageSource? _thumbnail;
    public string Path { get; } = path;
    public string Name { get; } = name;
    public NodeKind Kind { get; } = kind;
    public bool CanvasFile { get; } = canvasFile;
    public string? NodeId { get; } = node?.Id;
    public bool OffCanvas => CanvasFile && NodeId is null;
    public string Glyph => NodeView.Glyph(Kind);
    public bool IsVideo => Kind == NodeKind.Video;
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
}

/// <summary>
/// The canvas's own media library (drag items onto the canvas, double-click to drop them at the view centre), and a second tab
/// with every file in the canvas's folder: click one to find it on the canvas. Deleting nodes on the canvas keeps their files;
/// only deleting here sends files to the recycle bin.
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

    /// <summary>Read both folders again (files may have been added or removed in Explorer).</summary>
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
    /// Every picture, video and audio file in the canvas's folder, newest first, named after the first node showing it (a stack's lead
    /// goes by its own number like the rest of its batch). Canvas edits arrive constantly (every drag step), so the folder is read again
    /// only when a node's file or title changes, and unchanged entries keep their thumbnails.
    /// </summary>
    private bool ReloadCanvas()
    {
        if (Target is null) return false;
        var doc = Target.Doc;
        var batches = doc.Nodes.Where(n => Stacks.Of(n) is not null).GroupBy(n => Stacks.Of(n)!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var shown = doc.Nodes.Where(n => n.HasContent && n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio)
            .Select(n => (Node: n, Name: Stacks.Of(n) is { } id && batches.Contains(id) ? Stacks.OwnTitle(n) : n.Title)).ToList();
        var keys = string.Join('\n', shown.Select(e => $"{e.Node.Id}|{e.Node.Content}|{e.Name}"));
        if (keys == _canvasKeys) return false;
        _canvasKeys = keys;
        var users = shown.GroupBy(e => e.Node.Content!).ToDictionary(g => g.Key, g => g.First());
        var old = _canvas.ToDictionary(Key);
        _canvas = LibraryStore.Files(ProjectStore.AssetsDir(doc.Id))
            .Where(file => !file.Name.StartsWith(ProjectStore.MaskPrefix + "-"))
            .Select(file => users.TryGetValue(Path.GetFileName(file.Path), out var user)
                ? new LibraryEntry(file.Path, user.Name, file.Kind, true, user.Node)
                : new LibraryEntry(file.Path, file.Name, file.Kind, true))
            .Select(entry => old.GetValueOrDefault(Key(entry)) ?? Thumbnailed(entry)).ToList();
        return true;
    }

    private static string Key(LibraryEntry entry) => $"{entry.Path}|{entry.NodeId}|{entry.Name}";

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
        EmptyText.Text = canvas ? "这个画布还没有图片、视频或音频文件。" : "把这个画布常用的图片、视频或音频拖到这里，或点右上角 + 导入。\n之后可以直接拖到画布上使用。";
        UsageHint.Text = canvas ? "单击在画布上找到它，不在画布上的可双击或拖回画布。画布上删掉的图，文件仍留在这里；在这里删除才会移到回收站。" : "双击放到画布中央，或拖到画布任意位置";
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
        ImportButton.Visibility = canvas ? Visibility.Collapsed : Visibility.Visible;
        var folder = canvas ? "打开画布文件夹" : "打开资料库文件夹";
        ToolTipService.SetToolTip(FolderButton, folder);
        AutomationProperties.SetName(FolderButton, folder);
        // Only canvas files are clicked to find them; library items keep plain selection for multi-item drags.
        Items.IsItemClickEnabled = canvas;
        ReloadCanvas();
        ApplyFilter();
    }

    private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ApplyFilter();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Data.SetData(CanvasView.LibraryFormat, string.Join('\n', e.Items.OfType<LibraryEntry>().Select(i => i.Path)));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryEntry { NodeId: { } id } && Target is { } canvas && canvas.Doc.Find(id) is { } node) canvas.Locate(node);
    }

    // Library items are copied in; a canvas file that is off the canvas goes back as it is.
    private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryEntry { NodeId: null } entry && Target is { } canvas)
            await canvas.ImportFilesAsync([entry.Path], canvas.ViewCenterWorld);
    }

    private async void Items_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || Items.SelectedItems.Count == 0) return;
        e.Handled = true;
        await DeleteAsync(Items.SelectedItems.OfType<LibraryEntry>().ToList());
    }

    // Right click, Shift+F10 and the menu key all land here; from the keyboard the source is the focused GridViewItem.
    private void Items_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: LibraryEntry entry } target || Target is not { } canvas) return;
        var menu = new MenuFlyout();
        if (entry.NodeId is { } id && canvas.Doc.Find(id) is { } node)
        {
            menu.Items.Add(CanvasView.Item("在画布上找到", "\uE721", () => canvas.Locate(node)));
            menu.Items.Add(CanvasView.Item("加入资料库", "\uE8F1", () => canvas.AddToLibraryAsync(node)));
        }
        else
        {
            menu.Items.Add(CanvasView.Item("放到画布中央", "\uE8B3", () => canvas.ImportFilesAsync([entry.Path], canvas.ViewCenterWorld)));
            if (entry.CanvasFile) menu.Items.Add(CanvasView.Item("加入资料库", "\uE8F1", () => LibraryStore.AddAsync(canvas.Doc.Id, [entry.Path])));
        }
        menu.Items.Add(CanvasView.Item("在资源管理器中显示", "\uE838", () => Process.Start("explorer.exe", $"/select,\"{entry.Path}\"")));
        menu.Items.Add(new MenuFlyoutSeparator());
        // On a selected item, delete acts on the whole selection, like the Delete key.
        var targets = Items.SelectedItems.Contains(entry) ? Items.SelectedItems.OfType<LibraryEntry>().ToList() : [entry];
        var count = targets.Count > 1 ? $" {targets.Count} 个文件" : "";
        menu.Items.Add(CanvasView.Item(entry.CanvasFile ? $"删除{count}（移到回收站）" : $"从资料库删除{count}（移到回收站）", "\uE74D", () => DeleteAsync(targets)));
        if (e.TryGetPosition(target, out var point)) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
        e.Handled = true;
    }

    /// <summary>Only successfully recycled files lose their nodes and historical references; restore files through the system recycle bin.</summary>
    private async Task DeleteAsync(IReadOnlyList<LibraryEntry> entries)
    {
        if (entries.Count == 0 || Target is not { } canvas) return;
        if (!entries[0].CanvasFile)
        {
            foreach (var entry in entries) LibraryStore.Remove(entry.Path);
            return;
        }
        if (!await Dialogs.ConfirmAsync($"删除 {entries.Count} 个文件？", "文件会移到回收站，画布上引用它们的节点和相关撤销记录也会删除。此操作不能通过画布撤销恢复；需要时请从系统回收站还原文件。", "删除", destructive: true) || canvas.IsClosing) return;
        var (recycled, failures) = (new List<string>(), new List<string>());
        foreach (var entry in entries)
        {
            try
            {
                if (ProjectStore.Recycle(entry.Path)) recycled.Add(Path.GetFileName(entry.Path));
                else failures.Add($"{entry.Name}：已取消或文件仍被占用");
            }
            catch (Exception ex) { failures.Add($"{entry.Name}：{ex.Message}"); }
        }
        if (recycled.Count > 0) canvas.ForgetAssets(recycled);
        Refresh();
        if (failures.Count > 0) await Dialogs.MessageAsync("部分文件未删除", string.Join("\n", failures));
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
        var dir = ShowingCanvas ? ProjectStore.AssetsDir(Target.Doc.Id) : ProjectStore.LibraryDir(Target.Doc.Id);
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
