using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AstralStation.Services;

// Measure the displayed text in logical pixels so resizing and DPI changes preserve both ends.
public sealed class PathLabel : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(PathLabel), new PropertyMetadata("", Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private readonly TextBlock label = new() { FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock measure = new() { FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    public PathLabel()
    {
        MinWidth = 0; Children.Add(label);
        SizeChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((PathLabel)sender).Update();
    private void Update()
    {
        string full = Text ?? "";
        ToolTipService.SetToolTip(this, full);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, full);
        double width = ActualWidth;
        bool Fits(string text) { measure.Text = text; measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return measure.DesiredSize.Width <= width; }
        if (width <= 0) { label.Text = ""; return; }
        if (Fits(full)) { label.Text = full; return; }
        // Give the filename end more room than the directory prefix.
        string Shorten(int count) { int head = Math.Max(1, count / 3); return full[..head] + "…" + full[^(count - head)..]; }
        int low = 2, high = full.Length - 1, best = 0;
        while (low <= high) { int middle = (low + high) / 2; if (Fits(Shorten(middle))) { best = middle; low = middle + 1; } else high = middle - 1; }
        label.Text = best > 0 ? Shorten(best) : "…";
    }
}
