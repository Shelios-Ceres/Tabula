using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tabula.App;

/// <summary>Shared confirm / input / message dialogs bound to the main window.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static ContentDialog Create(string title, object content, string primary, string? close = "取消") => new()
    {
        XamlRoot = App.Window.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
    };

    /// <summary>WinUI shows one ContentDialog at a time; the rest wait their turn instead of making ShowAsync throw.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { Gate.Release(); }
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string primary = "确定", bool destructive = false)
    {
        var dialog = Create(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, primary);
        // Destructive actions default to Cancel so Enter never confirms them by accident.
        if (destructive) dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task<string?> InputAsync(string title, string value, string placeholder = "")
    {
        var box = new TextBox { Text = value, PlaceholderText = placeholder, MinWidth = 320 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await ShowAsync(Create(title, box, "确定")) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>A dialog around caller-built controls; true when the primary button was pressed.</summary>
    public static async Task<bool> FormAsync(string title, object content, string primary) =>
        await ShowAsync(Create(title, content, primary)) == ContentDialogResult.Primary;

    public static Task MessageAsync(string title, string message) =>
        ShowAsync(Create(title, new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, "知道了", null));

    public static async Task<List<string>?> PickAsync(string title, IEnumerable<string> items, IEnumerable<string> selected)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, ItemsSource = items.ToList(), MaxHeight = 420, MinWidth = 380 };
        list.Loaded += (_, _) =>
        {
            foreach (var item in selected) if (list.Items.Contains(item)) list.SelectedItems.Add(item);
        };
        return await ShowAsync(Create(title, list, "添加")) == ContentDialogResult.Primary ? list.SelectedItems.Cast<string>().ToList() : null;
    }
}
