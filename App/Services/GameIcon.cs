using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AstralStation.Services;

public sealed class GameIcon : Grid
{
    private readonly ImageBrush brush = new() { Stretch = Stretch.UniformToFill };
    public ImageSource? Source { get => brush.ImageSource; set => brush.ImageSource = value; }
    public GameIcon()
    {
        Width = Height = 60;
        CornerRadius = new CornerRadius(8);
        Background = brush;
        VerticalAlignment = VerticalAlignment.Center;
    }
}

