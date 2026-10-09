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

public sealed class LibraryEntry(LibraryItem item) : ObservableObject
{
    private ImageSource? _thumbnail;
    public LibraryItem Item { get; } = item;
    public string Name => Item.Name;
    public string Glyph => NodeView.Glyph(Item.Kind);
    public bool IsVideo => Item.Kind == NodeKind.Video;
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
}

/// <summary>Shared media library: drag items onto the canvas, double-click to drop them at the view centre.</summary>
public sealed partial class LibraryPanel : UserControl
{
    private List<LibraryEntry> _all = [];

    public LibraryPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { LibraryStore.Changed += Reload; Reload(); };
        Unloaded += (_, _) => LibraryStore.Changed -= Reload;
    }

    /// <summary>The canvas that receives double-clicked items.</summary>
    public CanvasView? Target { get; set; }

    public void CastShadowOn(UIElement receiver)
    {
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(receiver);
        Shell.Shadow = shadow;
        Shell.Translation = new System.Numerics.Vector3(0, 0, 24);
    }

    private void Reload()
    {
        _all = LibraryStore.List().Select(item => new LibraryEntry(item)).ToList();
        foreach (var entry in _all) _ = LoadThumbnailAsync(entry);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var tag = Filter.SelectedItem?.Tag as string ?? "All";
        var items = tag == "All" ? _all : _all.Where(e => e.Item.Kind.ToString() == tag).ToList();
        Items.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageHint.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static async Task LoadThumbnailAsync(LibraryEntry entry)
    {
        try
        {
            if (entry.Item.Kind == NodeKind.Image)
            {
                // Library files keep their names, so the same path may hold new content. 200 wide lets a 2:1 image fill the 132 x 100 cell without upscaling.
                entry.Thumbnail = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 200, UriSource = new Uri(entry.Item.Path) };
                return;
            }
            if (entry.Item.Kind != NodeKind.Video) return;
            var file = await StorageFile.GetFileFromPathAsync(entry.Item.Path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 264);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb);
            entry.Thumbnail = bitmap;
        }
        catch { /* No thumbnail: the type icon stays visible. */ }
    }

    private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ApplyFilter();

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Data.SetData(CanvasView.LibraryFormat, string.Join('\n', e.Items.OfType<LibraryEntry>().Select(i => i.Item.Path)));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryEntry entry && Target is { } canvas)
            await canvas.ImportFilesAsync([entry.Item.Path], canvas.ViewCenterWorld);
    }

    // Right click, Shift+F10 and the menu key all land here; from the keyboard the source is the focused GridViewItem.
    private void Items_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: LibraryEntry entry } target) return;
        var menu = new MenuFlyout();
        if (Target is { } canvas) menu.Items.Add(CanvasView.Item("放到画布中央", "", () => canvas.ImportFilesAsync([entry.Item.Path], canvas.ViewCenterWorld)));
        menu.Items.Add(CanvasView.Item("在资源管理器中显示", "", () => Process.Start("explorer.exe", $"/select,\"{entry.Item.Path}\"")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CanvasView.Item("从资料库删除（移到回收站）", "", () => LibraryStore.Remove(entry.Item.Path)));
        if (e.TryGetPosition(target, out var point)) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
        e.Handled = true;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".mp4", ".mov", ".webm", ".mp3", ".wav", ".m4a", ".aac", ".flac" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await LibraryStore.AddAsync(files.Select(f => f.Path).ToList());
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LibraryStore.Dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LibraryStore.Dir}\"") { UseShellExecute = true });
    }

    // Files dropped from Explorer go into the library; items dragged out of the library itself are ignored here.
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
        var items = await e.DataView.GetStorageItemsAsync();
        await LibraryStore.AddAsync(items.OfType<StorageFile>().Select(f => f.Path).ToList());
    }
}
