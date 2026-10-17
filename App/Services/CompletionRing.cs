using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AstralStation.Services;

public sealed class CompletionRing : Grid
{
    private readonly Microsoft.UI.Xaml.Shapes.Path arc = new() { StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Ellipse complete = new() { StrokeThickness = 5, Margin = new(3) };
    public CompletionRing()
    {
        Width = Height = 224;
        var blue = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 153, 229));
        Children.Add(new Ellipse { Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 100, 120, 145)), StrokeThickness = 5, Margin = new(3) });
        arc.Stroke = complete.Stroke = blue;
        Children.Add(arc); Children.Add(complete);
        SetProgress(0);
    }
    public void SetProgress(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        complete.Visibility = fraction >= 1 ? Visibility.Visible : Visibility.Collapsed;
        arc.Visibility = fraction is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
        if (fraction is <= 0 or >= 1) return;
        double angle = fraction * Math.PI * 2, radius = 109;
        var figure = new PathFigure { StartPoint = new Point(112, 3) };
        figure.Segments.Add(new ArcSegment { Point = new Point(112 - radius * Math.Sin(angle), 112 - radius * Math.Cos(angle)), Size = new Size(radius, radius), IsLargeArc = fraction > .5, SweepDirection = SweepDirection.Counterclockwise });
        var geometry = new PathGeometry(); geometry.Figures.Add(figure); arc.Data = geometry;
    }
}
