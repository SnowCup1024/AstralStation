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
    private readonly CalendarView calendar = new()
    {
        SelectionMode = CalendarViewSelectionMode.Single,
        FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday,
        IsOutOfScopeEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        Height = 320,
        Language = "zh-CN"
    };
    private readonly TextBlock dateTitle = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock empty = new() { Text = "选择有标记的日期，查看当天的运行记录。", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ListView records = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly HashSet<CalendarViewDayItem> realized = [];
    private readonly Dictionary<string, int> counts = [];
    private List<string> available = [];
    private string? selected;
    private int revision = -1;
    private DateTime lastDay;
    private bool interacting, updating;
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
        left.Children.Add(new TextBlock { Text = "游玩日历", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        left.Children.Add(calendar);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var previous = new Button { Content = new FontIcon { Glyph = "\uE76B", FontSize = 14 } };
        var next = new Button { Content = new FontIcon { Glyph = "\uE76C", FontSize = 14 } };
        var today = new Button { Content = "回到本月" };
        ToolTipService.SetToolTip(previous, "上一个有记录的月份"); ToolTipService.SetToolTip(next, "下一个有记录的月份");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previous, "上一个有记录的月份");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(next, "下一个有记录的月份");
        previous.Click += (_, _) => MoveRecordMonth(-1); next.Click += (_, _) => MoveRecordMonth(1);
        today.Click += (_, _) => calendar.SetDisplayDate(DateTimeOffset.Now);
        actions.Children.Add(previous); actions.Children.Add(today); actions.Children.Add(next); left.Children.Add(actions);
        left.Children.Add(new TextBlock { Text = "日期下方的色条表示当天有运行记录。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
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
        calendar.CalendarViewDayItemChanging += (_, args) => { if (args.InRecycleQueue) realized.Remove(args.Item); else { realized.Add(args.Item); Decorate(args.Item); } };
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (updating) return;
            selected = calendar.SelectedDates.Count > 0 ? calendar.SelectedDates[0].ToString("yyyy-MM-dd") : null;
            if (selected is not null && !available.Contains(selected)) { updating = true; calendar.SelectedDates.Clear(); updating = false; selected = null; }
            RenderRecords();
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
    private void Decorate(CalendarViewDayItem item)
    {
        string key = item.Date.ToString("yyyy-MM-dd"); int count = counts.GetValueOrDefault(key);
        // Disabled dates remain quiet instead of using CalendarView's blackout strike-through.
        item.IsBlackout = false;
        item.IsEnabled = count > 0;
        item.SetDensityColors(Enumerable.Repeat(Windows.UI.Color.FromArgb(255, 93, 105, 180), Math.Min(count, 5)));
    }
    public void RefreshIfNeeded(bool force = false)
    {
        if (interacting || (!force && revision == session.Store.Revision && lastDay == DateTime.Today)) return;
        try
        {
            var result = session.Store.HistoryDates();
            available = result.Dates.Where(d => string.CompareOrdinal(d, DateTime.Today.ToString("yyyy-MM-dd")) <= 0).ToList();
            counts.Clear(); foreach (var date in available) counts[date] = session.Store.Records(date).Count;
            notice.Text = result.Errors.Count == 0 ? "按启动时的每日重置周期归档，跨日退出保留在原日期。" : "部分记录无法读取：\n" + string.Join("\n", result.Errors);
            updating = true;
            calendar.MinDate = new DateTimeOffset(available.Count > 0 ? DateTime.ParseExact(available[0][..7] + "-01", "yyyy-MM-dd", null) : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
            calendar.MaxDate = new DateTimeOffset(new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)));
            if (selected is null || !available.Contains(selected)) selected = available.LastOrDefault(d => d.StartsWith(DateTime.Today.ToString("yyyy-MM")));
            calendar.SelectedDates.Clear();
            foreach (var item in realized) Decorate(item);
            if (selected is not null) calendar.SelectedDates.Add(new DateTimeOffset(DateTime.ParseExact(selected, "yyyy-MM-dd", null)));
            if (revision == -1) calendar.SetDisplayDate(DateTimeOffset.Now);
            revision = session.Store.Revision; lastDay = DateTime.Today;
            RenderRecords();
        }
        catch (Exception ex) { notice.Text = ex.Message; }
        finally { updating = false; }
    }
    private void MoveRecordMonth(int direction)
    {
        var baseline = selected is null ? DateTime.Today.ToString("yyyy-MM") : selected[..7];
        var months = available.Select(d => d[..7]).Distinct().Order().ToList();
        var target = direction < 0 ? months.LastOrDefault(m => string.CompareOrdinal(m, baseline) < 0) : months.FirstOrDefault(m => string.CompareOrdinal(m, baseline) > 0);
        if (target is null) return;
        selected = available.Last(d => d.StartsWith(target));
        updating = true; calendar.SelectedDates.Clear(); calendar.SelectedDates.Add(new DateTimeOffset(DateTime.ParseExact(selected, "yyyy-MM-dd", null))); updating = false;
        calendar.SetDisplayDate(calendar.SelectedDates[0]); RenderRecords();
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
                var panel = new StackPanel { Spacing = 6, Margin = new(4, 8, 4, 8) };
                panel.VerticalAlignment = VerticalAlignment.Center;
                panel.Children.Add(new TextBlock { Text = record.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                panel.Children.Add(new PathLabel { Text = record.Path }); panel.Children.Add(new TextBlock { Text = record.Summary, FontSize = 12 });
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = new(80) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var icon = new Image { Width = 80, Height = 80, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(icon); row.Children.Add(panel); SetColumn(panel, 1);
                var item = new ListViewItem { Content = new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = row, Margin = new(0, 4, 0, 4) }, Tag = record, HorizontalContentAlignment = HorizontalAlignment.Stretch }; records.Items.Add(item);
                if (!string.IsNullOrEmpty(selectedId) && record.Id == selectedId) records.SelectedItem = item;
                _ = SetIcon(icon, record.Path);
            }
        }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    private static async Task SetIcon(Image image, string path) => image.Source = await LaunchPage.LoadIcon(path);
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
