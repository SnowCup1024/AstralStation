using AstralStation.Modules.Launch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace AstralStation.Services;

public sealed class Dialogs(Window window, FrameworkElement root, LaunchSession session)
{
    public bool Busy { get; private set; }
    public async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        if (Busy) return ContentDialogResult.None;
        Busy = true; using var scope = session.Interaction();
        dialog.XamlRoot = root.XamlRoot; dialog.RequestedTheme = root.ActualTheme;
        try { return await dialog.ShowAsync(); } finally { Busy = false; }
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
    public static void Copy(string text) { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); Clipboard.Flush(); }
}
