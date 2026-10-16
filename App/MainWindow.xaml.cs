using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace AstralStation;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Func<FrameworkElement>> routes = new();
    private readonly Dictionary<string, FrameworkElement> pages = new();
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AppWindow.Resize(new SizeInt32(1120, 760));
        routes["launch"] = () => Intro("启动管理", "把每日启动安排在一起", "启动队列、任务库和运行记录将统一收纳在这个模块中。");
        routes["settings"] = Settings;
        routes["about"] = () => Intro("关于星间驿站", "Alpha 1.0", "属于自己的游戏工具集合。\n\nC# · WinUI\n仅提供简体中文界面。");
        Root.ActualThemeChanged += (_, _) => Caption();
        Caption();
        Navigation.SelectedItem = Navigation.MenuItems[1];
    }
    private void Caption()
    {
        var title = AppWindow.TitleBar;
        title.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        title.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string key } || !routes.TryGetValue(key, out var factory)) return;
        if (!pages.TryGetValue(key, out var page)) pages[key] = page = factory();
        PageHost.Content = page;
        PageHost.ContentTransitions = new TransitionCollection { new EntranceThemeTransition() };
    }
    private static StackPanel Intro(string title, string subtitle, string body)
    {
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        content.Children.Add(new TextBlock { Text = body });
        panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = content });
        return panel;
    }
    private FrameworkElement Settings()
    {
        var panel = Intro("设置", "外观", "选择适合自己的界面主题。");
        var theme = new ComboBox { Header = "应用主题", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = 0, MinWidth = 240 };
        theme.SelectionChanged += (_, _) => Root.RequestedTheme = (ElementTheme)theme.SelectedIndex;
        panel.Children.Add(theme);
        return panel;
    }
}
