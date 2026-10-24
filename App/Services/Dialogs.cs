using AstralStation.Launch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace AstralStation.Services;

public sealed class Dialogs(Window window, FrameworkElement root, LaunchSession session)
{
    public bool Busy { get; private set; }
    public IDisposable Interaction() => session.Interaction();
    public async Task<ContentDialogResult> Show(ContentDialog dialog, bool expand = false)
    {
        if (Busy) return ContentDialogResult.None;
        Busy = true; using var scope = session.Interaction();
        dialog.XamlRoot = root.XamlRoot; dialog.RequestedTheme = root.ActualTheme;
        void Fit()
        {
            dialog.MaxWidth = root.ActualWidth * .9;
            dialog.MaxHeight = root.ActualHeight * .9;
            if (expand)
            {
                double width = root.ActualWidth * .9, height = root.ActualHeight * .9;
                dialog.Resources["ContentDialogMaxWidth"] = width;
                dialog.Resources["ContentDialogMaxHeight"] = height;
                FrameworkElement? Find(DependencyObject node)
                {
                    if (node is FrameworkElement { Name: "BackgroundElement" } background) return background;
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                        if (Find(VisualTreeHelper.GetChild(node, i)) is { } match) return match;
                    return null;
                }
                if (Find(dialog) is { } panel) { panel.MaxWidth = panel.Width = width; panel.MaxHeight = panel.Height = height; }
                // Keep the popup host full-size so its centered surface has equal margins.
                dialog.MaxWidth = dialog.Width = root.ActualWidth;
                dialog.MaxHeight = dialog.Height = root.ActualHeight;
                if (dialog.Content is ScrollViewer viewer) { viewer.Width = Math.Max(200, width - 64); viewer.Height = Math.Max(100, height - 170); }
            }
            if (dialog.Content is ScrollViewer form) form.MaxHeight = Math.Max(120, root.ActualHeight - 200);
        }
        void Resized(object sender, SizeChangedEventArgs args) => Fit();
        void Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) => Fit();
        dialog.Opened += Opened;
        root.SizeChanged += Resized;
        Fit();
        try { return await dialog.ShowAsync(); }
        finally { dialog.Opened -= Opened; root.SizeChanged -= Resized; Busy = false; }
    }
    public Task Error(string message) => Show(new() { Title = "操作未完成", Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }, CloseButtonText = "知道了" });
    public async Task<bool> Confirm(string title, string message) => await Show(new() { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }, PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) == ContentDialogResult.Primary;
    public async Task<string?> Pick(params string[] extensions)
    {
        using var scope = session.Interaction(); var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }
    public async Task<string?> PickFolder()
    {
        using var scope = session.Interaction(); var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    public static void Copy(string text) { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); Clipboard.Flush(); }
}
