using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace AstralStation.Services;

public sealed class GameIcon : Grid
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(GameIcon), new PropertyMetadata("", PathChanged));
    public string Path { get => (string)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    private readonly ImageBrush brush = new() { Stretch = Stretch.UniformToFill };
    private readonly FontIcon fallback = new() { Glyph = "\uE7C3", FontSize = 26, Opacity = .4 };
    private int generation;
    private bool attached;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer retry;
    private int retrySeconds = 5;

    public GameIcon()
    {
        Width = Height = 52;
        CornerRadius = new CornerRadius(8);
        Background = brush;
        Children.Add(fallback);
        VerticalAlignment = VerticalAlignment.Center;
        retry = DispatcherQueue.CreateTimer();
        retry.IsRepeating = false;
        retry.Tick += (_, _) => { if (attached) Refresh(); };
        Loaded += (_, _) => { attached = true; Refresh(); };
        Unloaded += (_, _) => { attached = false; generation++; retry.Stop(); };
    }
    private static void PathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var icon = (GameIcon)sender;
        icon.generation++;
        icon.retry.Stop(); icon.retrySeconds = 5;
        icon.brush.ImageSource = null;
        icon.fallback.Visibility = Visibility.Visible;
        if (icon.attached) icon.Refresh();
    }
    private async void Refresh()
    {
        int request = ++generation;
        retry.Stop();
        string path = Path;
        if (string.IsNullOrWhiteSpace(path)) return;
        // A recycled row can change paths while an earlier shell request is still pending.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var image = await GameIconLoader.Load(path);
            if (!attached || request != generation || Path != path) return;
            if (image is not null)
            {
                brush.ImageSource = image;
                fallback.Visibility = Visibility.Collapsed;
                retrySeconds = 5;
                return;
            }
            if (attempt < 2) await Task.Delay(250 * (attempt + 1));
            if (!attached || request != generation) return;
        }
        // Pivot may retain a hidden page without raising Loaded again. Retry failures
        // independently; stop on success/unload and back off for permanently missing files.
        retry.Interval = TimeSpan.FromSeconds(retrySeconds);
        retrySeconds = Math.Min(30, retrySeconds * 2);
        retry.Start();
    }
}

// Used on the UI dispatcher: coalesce pending requests, but never retain failed results.
internal static class GameIconLoader
{
    private static readonly Dictionary<string, Task<BitmapImage?>> pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapImage> cache = new(StringComparer.OrdinalIgnoreCase);
    public static async Task<BitmapImage?> Load(string path)
    {
        string key;
        try { key = System.IO.Path.GetFullPath(path); }
        catch { return null; }
        if (cache.TryGetValue(key, out var cached)) return cached;
        if (!pending.TryGetValue(key, out var work)) pending[key] = work = Read(key);
        var image = await work;
        // Only the first waiter publishes the shared result and evicts an old entry.
        if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, work))
        {
            pending.Remove(key);
            if (image is not null)
            {
                if (cache.Count >= 128) cache.Remove(cache.Keys.First());
                cache[key] = image;
            }
        }
        return image;
    }
    private static async Task<BitmapImage?> Read(string path)
    {
        try
        {
            // Shell activation can pump STA messages before its first await. Keep it out of
            // Loaded/layout callbacks, where nested XAML dispatch causes a native fail-fast.
            var bytes = await Task.Run(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.None);
                if (thumbnail is null || thumbnail.Size == 0 || thumbnail.Size > 4 * 1024 * 1024) return null;
                using var reader = new DataReader(thumbnail.GetInputStreamAt(0));
                uint count = (uint)thumbnail.Size;
                if (await reader.LoadAsync(count) != count) return null;
                var result = new byte[count]; reader.ReadBytes(result); return result;
            });
            if (bytes is null) return null;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes); await writer.StoreAsync();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"图标加载失败：{path} · {ex.Message}"); return null; }
    }
}
