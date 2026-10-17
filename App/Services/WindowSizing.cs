using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AstralStation.Services;

// Sizes are physical pixels. The XAML Viewbox scales one 960×540 design uniformly.
public sealed class WindowSizing : IDisposable
{
    public static readonly int[] Heights = [540, 810, 1080, 1620];
    private readonly AppWindow window;
    private readonly DispatcherQueue dispatcher;
    private bool queued, applying, disposed;
    private int requested;
    public int Height { get; private set; }
    public event Action? Changed;
    public WindowSizing(AppWindow window, DispatcherQueue dispatcher, int requested)
    {
        this.window = window; this.dispatcher = dispatcher; this.requested = requested;
        var presenter = (OverlappedPresenter)window.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        window.Changed += WindowChanged;
        Apply(true);
    }
    private DisplayArea Display => DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
    public bool Available(int height)
    {
        var display = Display;
        return height <= display.OuterBounds.Height * .75 && height * 16 / 9 <= display.OuterBounds.Width * .75
            && height <= display.WorkArea.Height && height * 16 / 9 <= display.WorkArea.Width;
    }
    public void Select(int height) { requested = height; Apply(true); }
    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (disposed || applying || queued || (!args.DidSizeChange && !args.DidPositionChange)) return;
        queued = true;
        dispatcher.TryEnqueue(() => { queued = false; if (!disposed) Apply(false); });
    }
    private void Apply(bool center)
    {
        if (applying) return;
        applying = true;
        try
        {
            int maximum = Heights.Where(Available).DefaultIfEmpty(540).Max();
            Height = Heights.Contains(requested) ? Math.Min(requested, maximum) : maximum;
            var presenter = (OverlappedPresenter)window.Presenter;
            if (presenter.State == OverlappedPresenterState.Minimized) return;
            if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
            int width = Height * 16 / 9;
            bool resized = window.Size.Width != width || window.Size.Height != Height;
            if (resized) window.Resize(new SizeInt32(width, Height));
            // Ordinary dragging must remain free to cross monitor boundaries.
            if (!center && !resized) { Changed?.Invoke(); return; }
            var work = Display.WorkArea;
            int x = center ? work.X + (work.Width - width) / 2 : Math.Clamp(window.Position.X, work.X, Math.Max(work.X, work.X + work.Width - width));
            int y = center ? work.Y + (work.Height - Height) / 2 : Math.Clamp(window.Position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - Height));
            if (window.Position.X != x || window.Position.Y != y) window.Move(new PointInt32(x, y));
            Changed?.Invoke();
        }
        finally { applying = false; }
    }
    public void Dispose() { disposed = true; window.Changed -= WindowChanged; }
}
