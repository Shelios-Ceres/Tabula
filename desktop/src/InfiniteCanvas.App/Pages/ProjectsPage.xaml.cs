using System.Diagnostics;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace InfiniteCanvas.App.Pages;

public sealed partial class ProjectsPage : Page
{
    public ProjectsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Window.SetCaption(null);
        Reload();
    }

    private void Reload()
    {
        var projects = ProjectStore.List();
        Projects.ItemsSource = projects;
        Empty.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = projects.Count == 0 ? "画布保存在本机，不会上传到任何服务器" : $"共 {projects.Count} 个画布 · 保存在本机";
    }

    // Logical pixels, set before UriSource: sharp at 150%/200% scaling, and a 16:9 cover filling the card height is never upscaled.
    public static BitmapImage? Cover(string? path) => path is not null && File.Exists(path) ? new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 320, UriSource = new Uri(path) } : null;

    private static readonly InfiniteCanvas.Core.Canvas.NodeKind[] CoverKinds =
        [InfiniteCanvas.Core.Canvas.NodeKind.Image, InfiniteCanvas.Core.Canvas.NodeKind.Video, InfiniteCanvas.Core.Canvas.NodeKind.Config, InfiniteCanvas.Core.Canvas.NodeKind.Audio, InfiniteCanvas.Core.Canvas.NodeKind.Text, InfiniteCanvas.Core.Canvas.NodeKind.Group];

    /// <summary>A diagonal gradient between two neighbouring palette colours, picked from the canvas id so each canvas keeps its colour.</summary>
    public static Microsoft.UI.Xaml.Media.Brush CoverBrush(string id)
    {
        // string.GetHashCode changes per process; this one is stable.
        var index = (int)(id.Aggregate(17u, (hash, ch) => hash * 31 + ch) % (uint)CoverKinds.Length);
        var (from, to) = (Palette.Kind(CoverKinds[index], ElementTheme.Light), Palette.Kind(CoverKinds[(index + 1) % CoverKinds.Length], ElementTheme.Light));
        return new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops = { new() { Color = from, Offset = 0 }, new() { Color = to, Offset = 1 } },
        };
    }

    public static string Stats(int nodes, int connections, DateTimeOffset updated)
    {
        var (t, today) = (updated.LocalDateTime, DateTime.Today);
        var when = t.Date == today ? $"今天 {t:HH:mm}" : t.Date == today.AddDays(-1) ? $"昨天 {t:HH:mm}" : t.Year == today.Year ? $"{t.Month}月{t.Day}日" : $"{t.Year}年{t.Month}月{t.Day}日";
        return $"{nodes} 个节点 · {connections} 条连线 · {when}";
    }

    private void Create_Click(object sender, RoutedEventArgs e) => Open(ProjectStore.Create().Id);

    private void Settings_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());

    private void Projects_ItemClick(object sender, ItemClickEventArgs e) => Open(((ProjectInfo)e.ClickedItem).Id);

    private void Open(string id) => Frame.Navigate(typeof(CanvasPage), id, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectInfo project } button) ShowMenu(button, project, null);
    }

    // Mouse right-click, Shift+F10 and the menu key all raise ContextRequested.
    private void Projects_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ProjectInfo project } target) return;
        ShowMenu(target, project, e.TryGetPosition(target, out var point) ? point : null);
        e.Handled = true;
    }

    private void ShowMenu(FrameworkElement target, ProjectInfo project, Point? position)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) =>
            {
                // Rename or delete can fail half-way (file in use, cancelled shell dialog): report it and show what is left.
                try { await action(); }
                catch (Exception ex)
                {
                    App.Log(ex);
                    await Dialogs.MessageAsync("操作失败", ex.Message);
                    Reload();
                }
            };
            menu.Items.Add(item);
        }
        Add("打开", "", () => { Open(project.Id); return Task.CompletedTask; });
        Add("重命名", "", async () =>
        {
            if (await Dialogs.InputAsync("重命名画布", project.Title) is { Length: > 0 } title) { ProjectStore.Rename(project.Id, title); Reload(); }
        });
        Add("在资源管理器中显示", "", () =>
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(ProjectStore.AssetsDir(project.Id))}\"") { UseShellExecute = true });
            return Task.CompletedTask;
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("删除", "", async () =>
        {
            if (!await Dialogs.ConfirmAsync("删除画布？", $"「{project.Title}」及其中的图片、视频会移到回收站，可以从回收站恢复。", "删除", destructive: true)) return;
            ProjectStore.Delete(project.Id);
            Reload();
        });
        menu.ShowAt(target, position is { } p ? new FlyoutShowOptions { Position = p } : new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }
}
