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
    private readonly Grid content = new() { ColumnSpacing = 20, RowSpacing = 16 };
    private readonly Grid days = new() { RowSpacing = 4, ColumnSpacing = 4 };
    private readonly TextBlock monthTitle = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock dateTitle = new() { FontSize = 18 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView records = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button previous = new() { Content = "上一月" }, next = new() { Content = "下一月" };
    private readonly Dictionary<string, Button> dateButtons = [];
    private List<string> available = [];
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string? selected;
    private int revision = -1;
    private DateTime lastDay;
    private bool interacting;
    public HistoryView(LaunchSession session, Dialogs dialogs)
    {
        this.session = session; this.dialogs = dialogs;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowSpacing = 12;
        Children.Add(notice); Children.Add(content); SetRow(content, 1);
        content.ColumnDefinitions.Add(new() { Width = new(340) }); content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var calendar = new StackPanel { Spacing = 12 }; var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var today = new Button { Content = "今天" }; foreach (var element in new UIElement[] { previous, next, today }) heading.Children.Add(element);
        calendar.Children.Add(monthTitle); calendar.Children.Add(heading); calendar.Children.Add(days); content.Children.Add(calendar);
        var detail = new Grid { RowSpacing = 12 }; detail.RowDefinitions.Add(new() { Height = GridLength.Auto }); detail.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); detail.Children.Add(dateTitle); detail.Children.Add(records); SetRow(records, 1); content.Children.Add(detail); SetColumn(detail, 1); SetRowSpan(detail, 2);
        for (int i = 0; i < 7; i++) days.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++) days.RowDefinitions.Add(new() { Height = GridLength.Auto });
        previous.Click += (_, _) => MoveMonth(-1); next.Click += (_, _) => MoveMonth(1); today.Click += (_, _) => { month = new(DateTime.Today.Year, DateTime.Today.Month, 1); selected = null; Draw(); };
        SizeChanged += (_, _) => { bool wide = ActualWidth >= 680; content.ColumnDefinitions[0].Width = wide ? new(340) : new(1, GridUnitType.Star); content.ColumnDefinitions[1].Width = wide ? new(1, GridUnitType.Star) : new(0); SetColumn(detail, wide ? 1 : 0); SetRow(detail, wide ? 0 : 1); SetRowSpan(detail, wide ? 2 : 1); };
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
    public void RefreshIfNeeded(bool force = false)
    {
        if (interacting || (!force && revision == session.Store.Revision && lastDay == DateTime.Today)) return;
        try { var result = session.Store.HistoryDates(); available = result.Dates.Where(d => string.CompareOrdinal(d, DateTime.Today.ToString("yyyy-MM-dd")) <= 0).ToList(); notice.Text = result.Errors.Count == 0 ? "记录按启动时的每日重置时间归档；跨日退出仍保留在原周期。" : "以下日期无法读取，其他日期仍可使用：\n" + string.Join("\n", result.Errors); revision = session.Store.Revision; lastDay = DateTime.Today; Draw(); }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    private List<DateTime> Months() => available.Select(d => DateTime.ParseExact(d[..7] + "-01", "yyyy-MM-dd", null)).Append(new(DateTime.Today.Year, DateTime.Today.Month, 1)).Distinct().Order().ToList();
    private void MoveMonth(int direction)
    {
        var choices = Months(); DateTime target = direction < 0 ? choices.LastOrDefault(m => m < month) : choices.FirstOrDefault(m => m > month);
        if (target == default) return; month = target; selected = null; Draw();
    }
    private void Draw()
    {
        var dates = available.Where(d => d.StartsWith(month.ToString("yyyy-MM"))).ToList();
        if (selected is null || !dates.Contains(selected)) selected = dates.LastOrDefault();
        monthTitle.Text = month.ToString("yyyy年 M月"); previous.IsEnabled = Months().Any(m => m < month); next.IsEnabled = Months().Any(m => m > month);
        days.Children.Clear(); dateButtons.Clear();
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (int i = 0; i < 7; i++) { var label = new TextBlock { Text = labels[i], HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 8, 0, 8) }; days.Children.Add(label); SetColumn(label, i); }
        int offset = ((int)month.DayOfWeek + 6) % 7;
        for (int i = 1; i <= DateTime.DaysInMonth(month.Year, month.Month); i++)
        {
            string key = month.AddDays(i - 1).ToString("yyyy-MM-dd"); bool enabled = dates.Contains(key);
            var button = new Button { Content = i.ToString(), Width = 36, Height = 36, Padding = new(0), CornerRadius = new(18), IsEnabled = enabled, IsTabStop = key == selected, HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new(enabled ? 1 : 0) };
            if (enabled) button.BorderBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            if (key == selected) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += (_, _) => Select(key); button.KeyDown += DateKey;
            button.Tag = key; dateButtons[key] = button; days.Children.Add(button); SetColumn(button, (offset + i - 1) % 7); SetRow(button, 1 + (offset + i - 1) / 7);
        }
        RenderRecords();
    }
    private void Select(string key)
    {
        if (selected == key) return;
        if (selected is not null && dateButtons.TryGetValue(selected, out var old)) { old.Style = (Style)Application.Current.Resources["GhostButtonStyle"]; old.BorderThickness = new(1); old.BorderBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]; old.IsTabStop = false; }
        selected = key;
        if (dateButtons.TryGetValue(key, out var button)) { button.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; button.IsTabStop = true; }
        RenderRecords();
    }
    private void DateKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown) { MoveMonth(e.Key == VirtualKey.PageUp ? -1 : 1); e.Handled = true; }
        else if (e.Key == VirtualKey.Home) { month = new(DateTime.Today.Year, DateTime.Today.Month, 1); selected = null; Draw(); e.Handled = true; }
        else if (selected is not null && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            int step = e.Key == VirtualKey.Left ? -1 : e.Key == VirtualKey.Right ? 1 : e.Key == VirtualKey.Up ? -7 : 7;
            DateTime target = DateTime.ParseExact(selected, "yyyy-MM-dd", null).AddDays(step);
            string? key = step < 0 ? available.LastOrDefault(d => string.CompareOrdinal(d, target.ToString("yyyy-MM-dd")) <= 0) : available.FirstOrDefault(d => string.CompareOrdinal(d, target.ToString("yyyy-MM-dd")) >= 0);
            if (key is not null) { var date = DateTime.ParseExact(key, "yyyy-MM-dd", null); if (month.Month != date.Month || month.Year != date.Year) { month = new(date.Year, date.Month, 1); selected = key; Draw(); } else Select(key); }
            e.Handled = true;
        }
        if (e.Handled && selected is not null && dateButtons.TryGetValue(selected, out var focus)) focus.Focus(FocusState.Keyboard);
    }
    private void RenderRecords()
    {
        string? selectedId = (records.SelectedItem as ListViewItem)?.Tag is LaunchRecord old ? old.Id : null;
        records.Items.Clear(); dateTitle.Text = selected is null ? "本月暂无记录" : selected + " · 运行记录";
        if (selected is null) return;
        try
        {
            foreach (var record in session.Store.Records(selected))
            {
                var panel = new StackPanel { Spacing = 6, Margin = new(4, 8, 4, 8) };
                var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var icon = new Image { Width = 28, Height = 28 }; heading.Children.Add(icon); heading.Children.Add(new TextBlock { Text = record.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(heading);
                panel.Children.Add(new TextBlock { Text = record.Path, FontSize = 12, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(new TextBlock { Text = record.Summary, FontSize = 12 });
                var item = new ListViewItem { Content = panel, Tag = record, HorizontalContentAlignment = HorizontalAlignment.Stretch }; records.Items.Add(item);
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
