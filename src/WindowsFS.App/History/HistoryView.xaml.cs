using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using WindowsFS.App.Controls;
using System.Windows.Documents;

namespace WindowsFS.App.History;

internal record HistoryGroup(long Minute, string Program, string Action, string Purpose, List<HistoryRow> Items)
{
    public string When => DateTimeOffset.FromUnixTimeMilliseconds(Minute).LocalDateTime.ToString("MM-dd HH:mm");
    public int Count => Items.Where(x => x.Path.Length != 0).Select(x => x.Path).Distinct().Count();
    public string Delta => Sum(Items.Select(x => x.Change), true);
    public string Freed => Sum(Items.Select(x => x.Reclaimed), false);
    private static string Sum(IEnumerable<long?> values, bool signed)
    {
        var known = values.OfType<long>().ToArray();
        if (known.Length == 0) return "—";
        var n = known.Sum();
        return (signed && n != 0 ? n > 0 ? "+" : "−" : "") + HistoryRow.Size(n) + (known.Length < values.Count() ? " *" : "");
    }
}
public partial class HistoryView : UserControl
{
    private HistoryRecorder? recorder;
    private Func<string[]> scope = () => [];
    private Action enableTray = () => { };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(4) };
    private List<VolumePoint> samples = [];
    private int hours = 24, revision;
    private long? selected;
    private bool largest, operations, busy, controlsBusy;
    private List<HistoryRow>? displayed;
    private bool displayedLargest, displayedOperations;
    private long first, last, bin;
    private bool pausedByUser;
    public HistoryView()
    {
        InitializeComponent();
        refresh.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += (_, _) => UpdateRefresh();
        Unloaded += (_, _) => { refresh.Stop(); revision++; };
    }
    internal void Connect(HistoryRecorder source, Func<string[]> getRoots, Action showTray)
    {
        recorder = source; scope = getRoots; enableTray = showTray;
        UpdateRefresh();
    }
    private async void UpdateRefresh()
    {
        if (!IsVisible || !IsLoaded || recorder == null) { refresh.Stop(); revision++; return; }
        refresh.Start();
        await RefreshAsync();
    }
    private async void TrackClick(object sender, RoutedEventArgs e)
    {
        if (controlsBusy || recorder == null) return;
        controlsBusy = true;
        TrackButton.IsEnabled = false;
        try
        {
            if (recorder.Active) { pausedByUser = true; await recorder.PauseAsync(); }
            else { recorder.Start(scope(), pausedByUser || recorder.Roots.Length != 0); if (recorder.Active) enableTray(); }
        }
        finally { controlsBusy = false; }
        await RefreshAsync();
    }
    private async void DetailClick(object sender, RoutedEventArgs e)
    {
        if (recorder == null) return;
        bool enable = !recorder.Detailed;
        var operation = recorder.SetDetailAsync(enable);
        await RefreshAsync();
        await operation;
        await RefreshAsync();
    }
    private async void SelectPeriod(object sender, RoutedEventArgs e)
    {
        hours = int.Parse((string)((Button)sender).CommandParameter);
        selected = null; revision++; UpdateChoices();
        await RefreshAsync();
    }
    private async void SelectMode(object sender, RoutedEventArgs e)
    {
        operations = sender == OperationsButton; revision++; UpdateChoices(); await RefreshAsync();
    }
    private async void SelectSort(object sender, RoutedEventArgs e)
    {
        largest = sender == SizeButton; revision++; UpdateChoices(); await RefreshAsync();
    }
    private void UpdateChoices()
    {
        DayButton.Tag = hours == 24; WeekButton.Tag = hours == 168; MonthButton.Tag = hours == 720;
        ChangesButton.Tag = !operations; OperationsButton.Tag = operations;
        RecentButton.Tag = !largest; SizeButton.Tag = largest;
        var visibility = operations ? Visibility.Collapsed : Visibility.Visible;
        SortRail.Visibility = SizeFields.Visibility = PurposeColumn.Visibility = ChangeColumn.Visibility = FreedColumn.Visibility =
            FileChangeColumn.Visibility = FileFreedColumn.Visibility = visibility;
        ResetButton.Visibility = selected.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ResetTime(object sender, RoutedEventArgs e)
    { selected = null; revision++; await RefreshAsync(); }
    private async void PickTime(object sender, MouseButtonEventArgs e)
    {
        if (samples.Count == 0 || Plot.ActualWidth <= 0) return;
        selected = first + (long)((last - first) * Math.Clamp(e.GetPosition(Plot).X / Plot.ActualWidth, 0, 1)); revision++;
        await RefreshAsync();
    }
    internal async Task RefreshAsync()
    {
        if (busy || !refresh.IsEnabled || recorder == null) return;
        busy = true; int version = revision;
        try
        {
            var roots = recorder.Active || recorder.Roots.Length > 0 ? recorder.Roots : scope();
            StateText.Text = recorder.Status;
            ScopeText.Text = "분석 범위  " + (roots.Length == 0 ? "저장된 로컬 분석 없음" : string.Join("  ·  ", roots));
            ScopeText.ToolTip = ScopeText.Text;
            TrackButton.Content = recorder.Active ? "추적 일시 정지" : "추적 시작";
            TrackButton.IsEnabled = !controlsBusy && (recorder.Active || scope().Length > 0);
            DetailButton.IsEnabled = recorder.Active;
            DetailButton.Content = recorder.Detailed ? "프로그램 추적 끄기" : "프로그램 추적";
            UpdateChoices();
            long end = HistoryStore.Now, start = end - hours * 3600000L;
            long from = selected.HasValue ? Math.Max(start, selected.Value - 300000) : start;
            long to = selected.HasValue ? Math.Min(end, selected.Value + 300000) : end;
            bool hourly = to < end - 7 * 86400000L;
            if (from < end - 7 * 86400000L) from -= from % 3600000;
            if (hourly) to = to - to % 3600000 + 3599999;
            var queryOperations = operations; var queryLargest = largest;
            var result = await Task.Run(() =>
            {
                HistoryStore.CheckLocation(false);
                if (!File.Exists(HistoryStore.Database)) return (new List<VolumePoint>(), new List<HistoryRow>(), (0L, 0L, 0L));
                using var db = new HistoryStore(HistoryStore.Database, false);
                var totals = db.Totals(from, to);
                return (db.Samples(start), queryOperations ? db.Operations(from, to) : db.Rows(from, to, queryLargest), totals);
            });
            if (!refresh.IsEnabled || revision != version) return;
            DisplaySamples(result.Item1, start, end);
            DisplayRecords(result.Item2);
            TotalsText.Text = $"관찰된 증가  {HistoryRow.Size(result.Item3.Item1)}     삭제 회수 추정  {HistoryRow.Size(result.Item3.Item2)}";
            if (result.Item3.Item3 > 0) TotalsText.Text += $"  ·  회수량 미확인 {result.Item3.Item3:N0}건";
            var range = selected.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(selected.Value).LocalDateTime.ToString("MM-dd HH:mm") +
                (hourly ? " 시간별 요약" : " 전후 5분") : "전체 기간";
            RangeText.Text = $"{range}  ·  {Groups.Items.Count:N0}개 묶음";
            RangeText.ToolTip = (operations ? "실시간 작업 요청" : "같은 경로·작업 전후 2초에 관찰된 프로그램") + " · 상세 7일, 최대 2,000건";
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        { StateText.Text = recorder.Status + " · 로컬 기록 조회 지연"; }
        finally { busy = false; if (refresh.IsEnabled && revision != version) await RefreshAsync(); }
    }
    internal void DisplayRecords(List<HistoryRow> records)
    {
        if (displayedLargest == largest && displayedOperations == operations && displayed != null && displayed.SequenceEqual(records)) return;
        var activeGroup = displayedOperations == operations ? Groups.SelectedItem as HistoryGroup : null;
        var activeFile = Files.SelectedItem as HistoryRow;
        displayedLargest = largest; displayedOperations = operations; displayed = records;
        var groups = records.GroupBy(x => (Minute: x.Time - x.Time % 60000, x.Program, x.Action, x.Purpose))
            .Select(g => new HistoryGroup(g.Key.Minute, g.Key.Program, g.Key.Action, g.Key.Purpose, g.ToList()));
        var rows = (largest && !operations ? groups.OrderByDescending(g => g.Items.Sum(x => Math.Max(Math.Abs(x.Change ?? 0), x.Reclaimed ?? 0))) :
            groups.OrderByDescending(g => g.Minute)).ToList();
        Groups.ItemsSource = rows;
        EmptyList.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (activeGroup != null)
            Groups.SelectedItem = rows.Find(g => g.Minute == activeGroup.Minute && g.Program == activeGroup.Program && g.Action == activeGroup.Action && g.Purpose == activeGroup.Purpose);
        if (activeFile != null && Groups.SelectedItem is HistoryGroup group)
            Files.SelectedItem = group.Items.Find(x => x == activeFile) ?? group.Items.FirstOrDefault();
    }
    private void GroupSelected(object sender, SelectionChangedEventArgs e)
    {
        var group = Groups.SelectedItem as HistoryGroup;
        DetailArea.Visibility = group == null ? Visibility.Collapsed : Visibility.Visible;
        DetailRow.Height = group == null ? new GridLength(0) : new GridLength(1.5, GridUnitType.Star);
        Files.ItemsSource = group?.Items; Files.SelectedIndex = group == null ? -1 : 0;
    }
    private void PlotResized(object sender, SizeChangedEventArgs e) => Draw();
    internal void DisplaySamples(List<VolumePoint> points, long start, long end)
    {
        samples = points; first = start; last = end;
        bin = Math.Max(30000, (last - first) / 500);
        Draw();
    }
    private void Draw()
    {
        Plot.Children.Clear();
        VolumeText.Inlines.Clear();
        EmptyPlot.Visibility = samples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartLabel.Text = EndLabel.Text = "";
        if (samples.Count == 0 || last <= first || Plot.ActualWidth == 0) return;
        long min = samples.Min(p => p.Free), max = samples.Max(p => p.Free);
        StartLabel.Text = DateTimeOffset.FromUnixTimeMilliseconds(first).LocalDateTime.ToString("MM-dd HH:mm") +
            "    ·    " + HistoryRow.Size(min) + " – " + HistoryRow.Size(max);
        EndLabel.Text = DateTimeOffset.FromUnixTimeMilliseconds(last).LocalDateTime.ToString("MM-dd HH:mm");
        double w = Plot.ActualWidth, h = Math.Max(1, Plot.ActualHeight - 8);
        int index = 0;
        foreach (var group in samples.GroupBy(p => p.Volume).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var brush = SpaceMap.SeriesBrush(index++);
            VolumeText.Inlines.Add(new Run(" ● ") { Foreground = brush });
            VolumeText.Inlines.Add(new Run(group.Key + "  " + HistoryRow.Size(group.Last().Free) + "   "));
            var line = new StreamGeometry();
            using (var drawing = line.Open())
            {
                long previousTime = 0; string? segment = null;
                foreach (var p in group)
                {
                    var pt = new Point(w * (p.Time - first) / (last - first), 4 + h * (max == min ? 0.5 : (max - p.Free) / (double)(max - min)));
                    if (segment != p.Segment || p.Time - previousTime >= Math.Max(90000, bin * 2))
                        drawing.BeginFigure(new Point(pt.X - 0.5, pt.Y), false, false);
                    drawing.LineTo(pt, true, false);
                    previousTime = p.Time; segment = p.Segment;
                }
            }
            line.Freeze();
            Plot.Children.Add(new System.Windows.Shapes.Path { Data = line, Stroke = brush, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false });
        }
        if (selected is long t)
        {
            var x = w * (t - first) / (last - first);
            Plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = Plot.ActualHeight, Stroke = Brushes.White, StrokeDashArray = [3, 3] });
        }
    }
}
