using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Path = System.IO.Path;

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
public partial class HistoryWindow : Window
{
    private readonly HistoryRecorder recorder;
    private readonly Func<string[]> scope;
    private readonly Action enableTray;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(4) };
    private List<VolumePoint> samples = [];
    private int hours = 24, revision;
    private long? selected;
    private bool largest, operations, busy, closed, controlsBusy;
    private long first, last, bin;
    private bool pausedByUser;
    internal HistoryWindow(HistoryRecorder recorder, Func<string[]> scope, Action enableTray)
    {
        this.recorder = recorder; this.scope = scope; this.enableTray = enableTray;
        InitializeComponent();
        refresh.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { closed = true; refresh.Stop(); };
        refresh.Start();
    }
    private async void TrackClick(object sender, RoutedEventArgs e)
    {
        if (controlsBusy) return;
        controlsBusy = true;
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
        bool enable = !recorder.Detailed;
        var operation = recorder.SetDetailAsync(enable);
        await RefreshAsync();
        await operation;
        if (!closed) await RefreshAsync();
    }
    private async void CyclePeriod(object sender, RoutedEventArgs e)
    {
        hours = hours == 24 ? 168 : hours == 168 ? 720 : 24; selected = null; revision++;
        await RefreshAsync();
    }
    private async void CycleMode(object sender, RoutedEventArgs e)
    { operations = !operations; revision++; await RefreshAsync(); }
    private async void ToggleSort(object sender, RoutedEventArgs e)
    { largest = !largest; revision++; await RefreshAsync(); }
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
        if (busy || closed) return;
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
            PeriodButton.Content = hours == 24 ? "최근 24시간" : hours == 168 ? "최근 7일" : "최근 30일";
            ModeButton.Content = operations ? "프로그램 작업" : "파일 변화";
            SortButton.Content = largest ? "용량순" : "최근순"; SortButton.IsEnabled = !operations;
            ResetButton.Visibility = selected.HasValue ? Visibility.Visible : Visibility.Collapsed;
            last = HistoryStore.Now; first = last - hours * 3600000L;
            bin = Math.Max(30000, (last - first) / 500);
            long from = selected.HasValue ? Math.Max(first, selected.Value - 300000) : first;
            long to = selected.HasValue ? Math.Min(last, selected.Value + 300000) : last;
            bool hourly = to < last - 7 * 86400000L;
            if (from < last - 7 * 86400000L) from -= from % 3600000;
            if (hourly) to = to - to % 3600000 + 3599999;
            var queryOperations = operations; var queryLargest = largest;
            var result = await Task.Run(() =>
            {
                HistoryStore.CheckLocation(false);
                if (!File.Exists(HistoryStore.Database)) return (new List<VolumePoint>(), new List<HistoryRow>(), (0L, 0L, 0L));
                using var db = new HistoryStore(HistoryStore.Database, false);
                var totals = db.Totals(from, to);
                return (db.Samples(first), queryOperations ? db.Operations(from, to) : db.Rows(from, to, queryLargest), totals);
            });
            if (closed || revision != version) return;
            samples = result.Item1;
            var activeGroup = Groups.SelectedItem as HistoryGroup;
            var activeFile = Files.SelectedItem as HistoryRow;
            var groups = result.Item2.GroupBy(x => (Minute: x.Time - x.Time % 60000, x.Program, x.Action, x.Purpose))
                .Select(g => new HistoryGroup(g.Key.Minute, g.Key.Program, g.Key.Action, g.Key.Purpose, g.ToList()));
            var rows = (queryLargest && !queryOperations ? groups.OrderByDescending(g => g.Items.Sum(x => Math.Max(Math.Abs(x.Change ?? 0), x.Reclaimed ?? 0))) :
                groups.OrderByDescending(g => g.Minute)).ToList();
            Groups.ItemsSource = rows;
            if (activeGroup != null)
                Groups.SelectedItem = rows.Find(g => g.Minute == activeGroup.Minute && g.Program == activeGroup.Program && g.Action == activeGroup.Action && g.Purpose == activeGroup.Purpose);
            if (activeFile != null && Groups.SelectedItem is HistoryGroup group) Files.SelectedItem = group.Items.Find(x => x == activeFile) ?? group.Items.FirstOrDefault();
            TotalsText.Text = $"관찰된 증가  {HistoryRow.Size(result.Item3.Item1)}     삭제 회수 추정  {HistoryRow.Size(result.Item3.Item2)}";
            if (result.Item3.Item3 > 0) TotalsText.Text += $"  ·  회수량 미확인 {result.Item3.Item3:N0}건";
            RangeText.Text = (selected.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(selected.Value).LocalDateTime.ToString("MM-dd HH:mm") + (hourly ? " 시간별 요약" : " 전후 5분") : "전체 기간") +
                (operations ? "  ·  실시간 작업 요청" : "  ·  같은 경로·작업 전후 2초에 관찰된 프로그램") +
                "  ·  상세 7일, 최대 2,000건";
            Draw();
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        { StateText.Text = recorder.Status + " · 로컬 기록 조회 지연"; }
        finally { busy = false; if (!closed && revision != version) await RefreshAsync(); }
    }
    private void GroupSelected(object sender, SelectionChangedEventArgs e)
    {
        var group = Groups.SelectedItem as HistoryGroup;
        DetailArea.Visibility = group == null ? Visibility.Collapsed : Visibility.Visible;
        Files.ItemsSource = group?.Items; Files.SelectedIndex = group == null ? -1 : 0;
    }
    private void FileSelected(object sender, SelectionChangedEventArgs e) => FileText.Text = (Files.SelectedItem as HistoryRow)?.Details;
    private void PlotResized(object sender, SizeChangedEventArgs e) => Draw();
    private void Draw()
    {
        Plot.Children.Clear();
        VolumeText.Text = samples.Count == 0 ? "측정 기록 없음" : string.Join("     ", samples.GroupBy(p => p.Volume).Select(g => g.Key + "  " + HistoryRow.Size(g.Last().Free)));
        if (samples.Count == 0 || last <= first || Plot.ActualWidth == 0) return;
        long min = samples.Min(p => p.Free), max = samples.Max(p => p.Free);
        StartLabel.Text = DateTimeOffset.FromUnixTimeMilliseconds(first).LocalDateTime.ToString("MM-dd HH:mm") +
            "    ·    " + HistoryRow.Size(min) + " – " + HistoryRow.Size(max);
        EndLabel.Text = DateTimeOffset.FromUnixTimeMilliseconds(last).LocalDateTime.ToString("MM-dd HH:mm");
        double w = Plot.ActualWidth, h = Math.Max(1, Plot.ActualHeight - 8);
        Brush[] colors = [Brushes.Plum, Brushes.SkyBlue, Brushes.MediumAquamarine, Brushes.SandyBrown]; int index = 0;
        foreach (var group in samples.GroupBy(p => p.Volume))
        {
            var brush = colors[index++ % colors.Length];
            Point? previous = null; long previousTime = 0; string? segment = null;
            foreach (var p in group)
            {
                var pt = new Point(w * (p.Time - first) / (last - first), 4 + h * (max == min ? 0.5 : (max - p.Free) / (double)(max - min)));
                if (previous is Point a && segment == p.Segment && p.Time - previousTime < Math.Max(90000, bin * 2))
                    Plot.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = pt.X, Y2 = pt.Y, Stroke = brush, StrokeThickness = 2 });
                else { var dot = new Ellipse { Width = 4, Height = 4, Fill = brush }; Canvas.SetLeft(dot, pt.X - 2); Canvas.SetTop(dot, pt.Y - 2); Plot.Children.Add(dot); }
                previous = pt; previousTime = p.Time; segment = p.Segment;
            }
        }
        if (selected is long t)
        {
            var x = w * (t - first) / (last - first);
            Plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = Plot.ActualHeight, Stroke = Brushes.White, StrokeDashArray = [3, 3] });
        }
    }
}
