using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HotaruCodeViz;

public sealed record MeasureRow(string N, string Steps, string PerN, string Time, string Note);

/// <summary>ヒートマップ（行ごとの実行回数）と、計算量の測定。</summary>
public partial class MainWindow
{
    private const long MeasureStepLimit = 300_000_000;

    private const string DefaultTemplate = "{N}\n{nums N 1 100}";

    private long[]? _heatCounts;
    private Dictionary<int, int[]> _heatLines = new();

    private bool _measuring;
    private volatile bool _measureCancel;
    private long _measuringN;
    private readonly ObservableCollection<MeasureRow> _measureRows = new();
    private readonly List<(long N, long Steps, double Seconds)> _measurePoints = new();

    private static readonly Brush NeverRunBrush = Palette.Freeze(Color.FromArgb(70, 0x90, 0x90, 0x90));
    private static readonly Brush HeatLabelBack = Palette.Freeze(Color.FromArgb(200, 255, 255, 255));
    private static readonly Brush HeatLabelText = Palette.Freeze(Color.FromRgb(0x6D, 0x4C, 0x41));

    private void InitComplexity()
    {
        MeasureList.ItemsSource = _measureRows;
        TemplateBox.Text = DefaultTemplate.Replace("\n", Environment.NewLine);
    }

    // ======================= ヒートマップ =======================

    /// <summary>行番号 → その行にある「文と条件」の計装ポイント ID。</summary>
    private void BuildHeatIndex()
    {
        _heatLines = _map
            .Where(kv => kv.Value.Kind != HitKind.Expression)
            .GroupBy(kv => LineOf(kv.Value.Node))
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray());
    }

    /// <summary>行の実行回数 = その行にある文・条件の実行回数の最大値。</summary>
    private static long LineCount(int[] ids, long[] counts)
    {
        long max = 0;
        foreach (var id in ids)
            if (id < counts.Length && counts[id] > max) max = counts[id];
        return max;
    }

    private void ClearHeatmap()
    {
        _heatCounts = null;
        HeatOverlay?.Children.Clear();
    }

    private void HeatmapCheck_Changed(object sender, RoutedEventArgs e) => DrawHeatmap();

    private void DrawHeatmap()
    {
        if (HeatOverlay is null || HeatmapCheck is null) return;
        HeatOverlay.Children.Clear();
        if (HeatmapCheck.IsChecked != true || _heatCounts is not { } counts || _heatLines.Count == 0) return;

        long max = 1;
        foreach (var ids in _heatLines.Values) max = Math.Max(max, LineCount(ids, counts));

        double width = Math.Max(0, SourceBox.ViewportWidth);
        foreach (var (line, rect) in VisibleLines())
        {
            if (!_heatLines.TryGetValue(line, out var ids)) continue;
            long c = LineCount(ids, counts);

            var bar = new Rectangle
            {
                Width = width,
                Height = rect.Height,
                Fill = c == 0 ? NeverRunBrush : new SolidColorBrush(HeatColor(Math.Log(1 + c) / Math.Log(1 + max))),
            };
            Canvas.SetLeft(bar, 2);
            Canvas.SetTop(bar, rect.Top);
            HeatOverlay.Children.Add(bar);

            var label = new TextBlock
            {
                Text = c == 0 ? "未実行" : $"{c:N0} 回",
                FontSize = 11,
                Foreground = HeatLabelText,
                Background = HeatLabelBack,
                Padding = new Thickness(4, 0, 4, 0),
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(0, 2 + width - label.DesiredSize.Width - 2));
            Canvas.SetTop(label, rect.Top + (rect.Height - label.DesiredSize.Height) / 2);
            HeatOverlay.Children.Add(label);
        }
    }

    /// <summary>t = 0 → 薄い黄色、0.5 → オレンジ、1 → 赤。</summary>
    private static Color HeatColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        Color a = Color.FromRgb(255, 241, 118), b = Color.FromRgb(255, 152, 0), c = Color.FromRgb(229, 57, 53);
        var (from, to, u) = t < 0.5 ? (a, b, t / 0.5) : (b, c, (t - 0.5) / 0.5);
        byte Lerp(byte x, byte y) => (byte)(x + (y - x) * u);
        return Color.FromArgb((byte)(60 + 90 * t), Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B));
    }

    // ======================= 計算量の測定 =======================

    private static List<long> ParseNs(string text)
    {
        var result = new List<long>();
        foreach (var part in text.Split(new[] { ',', ' ', '、', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(part.Replace("_", ""), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d) && d >= 1)
                result.Add((long)d);
        }
        return result.Distinct().OrderBy(n => n).ToList();
    }

    private void PreviewInput_Click(object sender, RoutedEventArgs e)
    {
        var ns = ParseNs(NsBox.Text);
        if (ns.Count == 0) return;
        try
        {
            InputBox.Text = InputGenerator.Generate(TemplateBox.Text, ns[0], new Random(12345)).Replace("\n", Environment.NewLine);
        }
        catch (Exception ex)
        {
            EstimateText.Text = $"入力の生成に失敗しました: {ex.Message}";
        }
    }

    private void ClearMeasure_Click(object sender, RoutedEventArgs e)
    {
        _measureRows.Clear();
        _measurePoints.Clear();
        UpdateEstimate();
        DrawChart();
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _building) return;
        var ns = ParseNs(NsBox.Text);
        if (ns.Count == 0)
        {
            EstimateText.Text = "N の値を「10, 100, 1000」のように入力してください";
            return;
        }
        string template = TemplateBox.Text;

        _parseTimer.Stop();
        Parse();
        OutputBox.Clear();
        var tree = _tree!;

        _building = true;
        UpdateButtons();
        StatusText.Text = "コンパイル中…";
        BuildResult result;
        try
        {
            // 測定ではウォッチと式の値は使わない（文と条件だけを数える）
            result = await Task.Run(() => CodeRunner.Build(tree, watch: false, expressions: false));
        }
        finally
        {
            _building = false;
            UpdateButtons();
        }
        if (result.Bytes is not { } bytes)
        {
            AppendOutput("❌ コンパイルエラー\n" + string.Join("\n", result.Errors) + "\n");
            StatusText.Text = "コンパイルエラー";
            return;
        }

        _map = result.Map;
        StartCalls(result, false);
        ClearHistory();
        BuildHeatIndex();
        _measuring = true;
        _measureCancel = false;
        _running = true;
        UpdateButtons();
        AppendOutput($"📏 測定開始（N = {string.Join(", ", ns.Select(n => n.ToString("N0")))}）\n");

        int pointCount = result.Map.Count;
        var thread = new Thread(() =>
        {
            var rng = new Random(12345);
            foreach (var n in ns)
            {
                if (_measureCancel) break;
                string input;
                try
                {
                    input = InputGenerator.Generate(template, n, rng);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendOutput($"❌ N = {n:N0} の入力の生成に失敗: {ex.Message}\n"));
                    break;
                }

                var counts = new long[pointCount];
                Tracer.Reset();
                Tracer.FastForward = true;
                Tracer.Counts = counts;
                Tracer.MaxSteps = MeasureStepLimit;
                _measuringN = n;
                var output = new LimitedWriter(300);
                Console.SetIn(new StringReader(input));
                Console.SetOut(output);

                string note = "";
                bool stop = false;
                var sw = Stopwatch.StartNew();
                try
                {
                    CodeRunner.Execute(bytes);
                }
                catch (StepLimitExceededException)
                {
                    note = $"{MeasureStepLimit:N0} ステップで打ち切り";
                    stop = true;
                }
                catch (ExecutionStoppedException)
                {
                    note = "停止";
                    stop = true;
                }
                catch (Exception ex)
                {
                    note = $"例外: {ex.GetType().Name}";
                }
                var seconds = sw.Elapsed.TotalSeconds;
                long steps = Tracer.StepCount;
                Dispatcher.Invoke(() => AddMeasurement(n, steps, seconds, note, counts, output.ToString()));
                if (stop) break;
            }
            Dispatcher.BeginInvoke(() =>
            {
                _measuring = false;
                _running = false;
                UpdateButtons();
                AppendOutput("📏 測定終了\n");
                StatusText.Text = "📏 測定終了（ヒートマップは最後の N の実行回数です）";
            });
        })
        { IsBackground = true, Name = "Measure" };
        thread.Start();
    }

    private void AddMeasurement(long n, long steps, double seconds, string note, long[] counts, string output)
    {
        bool ok = note == "";
        if (ok)
        {
            _measurePoints.RemoveAll(p => p.N == n);
            _measurePoints.Add((n, steps, seconds));
        }
        _measureRows.Add(new MeasureRow(
            n.ToString("N0"),
            steps.ToString("N0"),
            ok ? (steps / (double)n).ToString("N1") : "",
            $"{seconds:N2} 秒",
            note));

        _heatCounts = counts;
        DrawHeatmap();
        UpdateEstimate();
        DrawChart();

        var firstLine = output.Split('\n')[0].TrimEnd('\r');
        AppendOutput($"  N = {n:N0}: {steps:N0} ステップ / {seconds:N2} 秒　出力: {firstLine}{(output.Contains('\n') ? " …" : "")}{(ok ? "" : "　⚠ " + note)}\n");
    }

    /// <summary>log(ステップ数) と log(N) の傾きから、N の何乗で増えるかを推定する。</summary>
    private bool TryFit(out double slope, out double coef)
    {
        slope = coef = 0;
        var pts = _measurePoints.Where(p => p.N >= 1 && p.Steps > 0).OrderBy(p => p.N).ToList();
        if (pts.Count < 2) return false;
        var xs = pts.Select(p => Math.Log(p.N)).ToArray();
        var ys = pts.Select(p => Math.Log(p.Steps)).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double sxx = xs.Sum(x => (x - mx) * (x - mx));
        if (sxx <= 0) return false;
        slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / sxx;
        coef = Math.Exp(my - slope * mx);
        return true;
    }

    private static string GuessOrder(double slope) => slope switch
    {
        < 0.25 => "O(1)",
        < 0.65 => "O(log N) か O(√N)",
        < 1.12 => "O(N)",
        < 1.4 => "O(N log N)",
        < 1.75 => "O(N√N)",
        < 2.3 => "O(N²)",
        < 2.75 => "O(N^2.5)",
        < 3.4 => "O(N³)",
        _ => "O(N⁴) 以上（または指数時間）",
    };

    private void UpdateEstimate()
    {
        if (_measurePoints.Count == 0)
        {
            EstimateText.Text = "まだ測定結果がありません";
            return;
        }
        if (!TryFit(out var slope, out var coef))
        {
            EstimateText.Text = "N を 2 つ以上にすると、増え方を推定します";
            return;
        }

        var sb = new StringBuilder();
        sb.Append($"増え方 ≈ N^{slope:F2}　→　{GuessOrder(slope)} っぽい");

        // 今の速さ（早送り）での秒数の目安も出す
        double stepsPerSec = _measurePoints.Sum(p => p.Steps) / Math.Max(1e-6, _measurePoints.Sum(p => p.Seconds));
        long maxN = _measurePoints.Max(p => p.N);
        foreach (var target in new long[] { 100_000, 1_000_000 })
        {
            if (target <= maxN) continue;
            double predicted = coef * Math.Pow(target, slope);
            sb.Append($"\nN = {target:N0} なら約 {FormatBig(predicted)} ステップ（このアプリの早送りで約 {FormatSeconds(predicted / stepsPerSec)}）");
        }
        EstimateText.Text = sb.ToString();
    }

    private static string FormatBig(double v) => v switch
    {
        >= 1e12 => $"{v / 1e12:N1} 兆",
        >= 1e8 => $"{v / 1e8:N1} 億",
        >= 1e4 => $"{v / 1e4:N1} 万",
        _ => $"{v:N0}",
    };

    private static string FormatSeconds(double s) => s switch
    {
        >= 86400 => $"{s / 86400:N1} 日",
        >= 3600 => $"{s / 3600:N1} 時間",
        >= 60 => $"{s / 60:N1} 分",
        _ => $"{s:N1} 秒",
    };

    // ======================= グラフ（両対数） =======================

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
        if (w < 120 || h < 100) return;

        var pts = _measurePoints.Where(p => p.N >= 1 && p.Steps > 0).OrderBy(p => p.N).ToList();
        if (pts.Count == 0)
        {
            AddText("測定すると、ここに N とステップ数のグラフ（両対数）が出ます", 12, h / 2 - 10, Palette.SnippetText);
            return;
        }

        const double left = 62, right = 16, top = 16, bottom = 40;
        double x0 = Math.Floor(Math.Log10(pts.Min(p => p.N))), x1 = Math.Ceiling(Math.Log10(pts.Max(p => p.N)));
        double y0 = Math.Floor(Math.Log10(pts.Min(p => p.Steps))), y1 = Math.Ceiling(Math.Log10(pts.Max(p => p.Steps)));
        if (x1 <= x0) x1 = x0 + 1;
        if (y1 <= y0) y1 = y0 + 1;

        double X(double n) => left + (Math.Log10(n) - x0) / (x1 - x0) * (w - left - right);
        double Y(double s) => h - bottom - (Math.Log10(s) - y0) / (y1 - y0) * (h - top - bottom);

        var grid = Palette.Freeze(Color.FromRgb(0xEE, 0xEE, 0xEE));
        var axis = Palette.Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
        for (double k = x0; k <= x1; k++)
        {
            double x = X(Math.Pow(10, k));
            ChartCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = h - bottom, Stroke = grid });
            AddText(FormatTick(k), x - 14, h - bottom + 4, Palette.SnippetText);
        }
        for (double k = y0; k <= y1; k++)
        {
            double y = Y(Math.Pow(10, k));
            ChartCanvas.Children.Add(new Line { X1 = left, X2 = w - right, Y1 = y, Y2 = y, Stroke = grid });
            AddText(FormatTick(k), 4, y - 8, Palette.SnippetText);
        }
        ChartCanvas.Children.Add(new Line { X1 = left, X2 = w - right, Y1 = h - bottom, Y2 = h - bottom, Stroke = axis });
        ChartCanvas.Children.Add(new Line { X1 = left, X2 = left, Y1 = top, Y2 = h - bottom, Stroke = axis });
        AddText("N", w - right - 10, h - 18, axis);
        AddText("ステップ数", left + 4, top - 2, axis);

        // 当てはめた直線
        if (TryFit(out var slope, out var coef))
        {
            double nA = Math.Pow(10, x0), nB = Math.Pow(10, x1);
            ChartCanvas.Children.Add(new Line
            {
                X1 = X(nA), Y1 = Y(coef * Math.Pow(nA, slope)),
                X2 = X(nB), Y2 = Y(coef * Math.Pow(nB, slope)),
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)), StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            });
        }

        // 測定点
        foreach (var p in pts)
        {
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Palette.Statement, ToolTip = $"N = {p.N:N0}\n{p.Steps:N0} ステップ" };
            Canvas.SetLeft(dot, X(p.N) - 5);
            Canvas.SetTop(dot, Y(p.Steps) - 5);
            ChartCanvas.Children.Add(dot);
        }
    }

    private static string FormatTick(double exponent) => exponent switch
    {
        <= 4 => Math.Pow(10, exponent).ToString("N0"),
        _ => $"10^{exponent}",
    };

    private void AddText(string text, double x, double y, Brush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, Foreground = brush };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        ChartCanvas.Children.Add(tb);
    }

    /// <summary>出力の先頭だけを取っておく Writer（測定中は出力を画面に流さない）。</summary>
    private sealed class LimitedWriter(int limit) : TextWriter
    {
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (_sb.Length < limit) _sb.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null || _sb.Length >= limit) return;
            _sb.Append(value.AsSpan(0, Math.Min(value.Length, limit - _sb.Length)));
        }

        public override string ToString() => _sb.ToString();
    }
}
