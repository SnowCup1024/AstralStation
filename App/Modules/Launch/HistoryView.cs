using AstralStation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AstralStation.Modules.Launch;

public sealed class HistoryView : Grid
{
    private readonly LaunchSession session;
    private readonly Dialogs dialogs;
    private readonly Grid content = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly Grid calendar = new() { ColumnSpacing = 4, RowSpacing = 4 };
    private readonly TextBlock monthTitle = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button previous = new() { Content = new FontIcon { Glyph = "\uE76B", FontSize = 14 }, Padding = new(10) };
    private readonly Button next = new() { Content = new FontIcon { Glyph = "\uE76C", FontSize = 14 }, Padding = new(10) };
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private readonly TextBlock dateTitle = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock empty = new() { Text = "选择有标记的日期，查看当天的运行记录。", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ListView records = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private List<string> available = [];
    private string? selected;
    private int revision = -1;
    private DateTime lastDay;
    private bool interacting;
    public HistoryView(LaunchSession session, Dialogs dialogs)
    {
        this.session = session; this.dialogs = dialogs;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowSpacing = 12;
        Children.Add(notice); Children.Add(content); SetRow(content, 1);
        content.ColumnDefinitions.Add(new() { Width = new(332) });
        content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var left = new StackPanel { Spacing = 14 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(monthTitle); header.Children.Add(previous); header.Children.Add(next);
        SetColumn(previous, 1); SetColumn(next, 2);
        ToolTipService.SetToolTip(previous, "上一月"); ToolTipService.SetToolTip(next, "下一月");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previous, "上一月");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(next, "下一月");
        previous.Click += (_, _) => MoveMonth(-1); next.Click += (_, _) => MoveMonth(1);
        left.Children.Add(header); left.Children.Add(calendar);
        for (int i = 0; i < 7; i++) calendar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var leftCard = new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = left, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(leftCard);
        var detail = new Grid { RowSpacing = 14, MinHeight = 260 };
        detail.RowDefinitions.Add(new() { Height = GridLength.Auto }); detail.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        detail.Children.Add(dateTitle); detail.Children.Add(records); SetRow(records, 1); detail.Children.Add(empty); SetRow(empty, 1);
        var detailCard = new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = detail };
        content.Children.Add(detailCard); SetColumn(detailCard, 1); SetRowSpan(detailCard, 2);
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 720;
            content.ColumnDefinitions[0].Width = wide ? new(332) : new(1, GridUnitType.Star);
            content.ColumnDefinitions[1].Width = wide ? new(1, GridUnitType.Star) : new(0);
            SetColumn(detailCard, wide ? 1 : 0); SetRow(detailCard, wide ? 0 : 1); SetRowSpan(detailCard, wide ? 2 : 1);
        };
        records.RightTapped += RecordMenu;
        records.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { records.SelectedItem = null; e.Handled = true; return; }
            if (records.SelectedItem is not ListViewItem { Tag: LaunchRecord record }) return;
            try
            {
                if (e.Key == VirtualKey.C && LaunchPage.Ctrl) { Dialogs.Copy(LaunchPage.Shift ? record.Path : record.CopyText); e.Handled = true; }
                if (e.Key == VirtualKey.Enter) { DesktopHost.OpenFolder(record.Path); e.Handled = true; }
                if (e.Key == VirtualKey.Application || (e.Key == VirtualKey.F10 && LaunchPage.Shift)) { Menu(records, record); e.Handled = true; }
            }
            catch (Exception ex) { await dialogs.Error(ex.Message); }
        };
    }
    private void RenderMonth()
    {
        monthTitle.Text = month.ToString("yyyy年M月");
        var earliest = available.Count > 0 ? DateTime.ParseExact(available[0][..7] + "-01", "yyyy-MM-dd", null) : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        previous.IsEnabled = month > earliest;
        next.IsEnabled = month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        calendar.Children.Clear(); calendar.RowDefinitions.Clear();
        int offset = ((int)month.DayOfWeek + 6) % 7, days = DateTime.DaysInMonth(month.Year, month.Month);
        int weeks = (offset + days + 6) / 7;
        for (int row = 0; row <= weeks; row++) calendar.RowDefinitions.Add(new() { Height = new(38) });
        string[] weekdays = ["一", "二", "三", "四", "五", "六", "日"];
        for (int i = 0; i < 7; i++) { var label = new TextBlock { Text = weekdays[i], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; calendar.Children.Add(label); SetColumn(label, i); }
        for (int day = 1; day <= days; day++)
        {
            string key = month.AddDays(day - 1).ToString("yyyy-MM-dd");
            bool hasRecord = available.Contains(key);
            var button = new Button { Content = day.ToString(), Padding = new(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsEnabled = hasRecord, CornerRadius = new(6), BorderThickness = new(hasRecord ? 1.5 : 0) };
            button.Background = new SolidColorBrush(key == selected ? Windows.UI.Color.FromArgb(255, 62, 101, 185) : Microsoft.UI.Colors.Transparent);
            if (hasRecord) button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 79, 117, 197));
            if (key == selected) button.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, key);
            button.Click += (_, _) => { selected = key; RenderMonth(); RenderRecords(); };
            int cell = offset + day - 1; SetRow(button, cell / 7 + 1); SetColumn(button, cell % 7); calendar.Children.Add(button);
        }
    }
    public void RefreshIfNeeded(bool force = false)
    {
        if (interacting || (!force && revision == session.Store.Revision && lastDay == DateTime.Today)) return;
        try
        {
            var result = session.Store.HistoryDates();
            available = result.Dates.Where(d => string.CompareOrdinal(d, DateTime.Today.ToString("yyyy-MM-dd")) <= 0).ToList();
            notice.Text = result.Errors.Count == 0 ? "按启动时的每日重置周期归档，跨日退出保留在原日期。" : "部分记录无法读取：\\n" + string.Join("\\n", result.Errors);
            if (selected is null || !available.Contains(selected)) selected = available.LastOrDefault(d => d.StartsWith(month.ToString("yyyy-MM")));
            revision = session.Store.Revision; lastDay = DateTime.Today;
            RenderMonth(); RenderRecords();
        }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    private void MoveMonth(int direction)
    {
        month = month.AddMonths(direction);
        selected = available.LastOrDefault(d => d.StartsWith(month.ToString("yyyy-MM")));
        RenderMonth(); RenderRecords();
    }
    private void RenderRecords()
    {
        string? selectedId = (records.SelectedItem as ListViewItem)?.Tag is LaunchRecord old ? old.Id : null;
        records.Items.Clear(); dateTitle.Text = selected is null ? "运行记录" : selected + " · 运行记录";
        empty.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        if (selected is null) return;
        try
        {
            foreach (var record in session.Store.Records(selected))
            {
                var panel = new StackPanel { Spacing = 4 };
                panel.VerticalAlignment = VerticalAlignment.Center;
                panel.Children.Add(new TextBlock { Text = record.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                panel.Children.Add(new PathLabel { Text = record.Path }); panel.Children.Add(new TextBlock { Text = record.Summary, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = new(60) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var icon = new GameIcon();
                row.Children.Add(icon); row.Children.Add(panel); SetColumn(panel, 1);
                var item = new ListViewItem { Content = new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = row, Margin = new(0, 4, 0, 4) }, Tag = record, HorizontalContentAlignment = HorizontalAlignment.Stretch }; records.Items.Add(item);
                if (!string.IsNullOrEmpty(selectedId) && record.Id == selectedId) records.SelectedItem = item;
                _ = SetIcon(icon, record.Path);
            }
        }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    private static async Task SetIcon(GameIcon image, string path) => image.Source = await LaunchPage.LoadIcon(path);
    private void RecordMenu(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem) element = VisualTreeHelper.GetParent(element);
        if (element is ListViewItem { Tag: LaunchRecord record } item) { records.SelectedItem = item; Menu(item, record); e.Handled = true; }
    }
    private void Menu(FrameworkElement target, LaunchRecord record)
    {
        var menu = new MenuFlyout(); IDisposable? scope = null;
        foreach (var label in new[] { "复制完整记录", "复制路径", "打开所在目录" })
        {
            var item = new MenuFlyoutItem { Text = label }; item.Click += async (_, _) => { try { if (label == "打开所在目录") DesktopHost.OpenFolder(record.Path); else Dialogs.Copy(label == "复制路径" ? record.Path : record.CopyText); } catch (Exception ex) { await dialogs.Error(ex.Message); } }; menu.Items.Add(item);
        }
        menu.Opened += (_, _) => { interacting = true; scope = session.Interaction(); }; menu.Closed += (_, _) => { scope?.Dispose(); interacting = false; }; menu.ShowAt(target);
    }
}
