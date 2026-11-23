using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HotaruCodeViz;

public sealed record WatchRow(string Name, string Value, string Type, bool Changed);

public partial class MainWindow : Window
{
    private const string Sample = """
        using System;
        using System.Linq;

        class Program
        {
            static void Main()
            {
                // 1行目: 個数 N、2行目: N 個の整数
                int n = int.Parse(Console.ReadLine());
                int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();

                int sum = 0;
                int max = a[0];
                for (int i = 0; i < n; i++)
                {
                    sum += a[i];
                    if (a[i] > max)
                        max = a[i];
                }
                Console.WriteLine($"合計 {sum} / 最大 {max}");
                Console.WriteLine($"{n}! = {Fact(n)}");
            }

            static int Fact(int k)
            {
                if (k <= 1) return 1;
                return k * Fact(k - 1);
            }
        }
        """;

    private const string SampleInput = "5\n3 1 4 1 5";

    private const int TrailLength = 5;

    /// <summary>これより大きい構文木は、待機中に「全体」グラフを自動では描かない。</summary>
    private const int LargeTreeNodes = 1500;

    private enum RunMode { Animate, Fast, Step }

    private static readonly object Placeholder = new();

    private SyntaxTree? _tree;
    private readonly Dictionary<SyntaxNode, TreeViewItem> _items = new();
    private readonly Dictionary<SyntaxNode, TextBlock> _treeValueLabels = new();
    private readonly Dictionary<SyntaxNode, string> _values = new();
    private Dictionary<int, HitInfo> _map = new();
    private readonly List<TreeViewItem> _trail = new();
    private readonly ObservableCollection<WatchRow> _watch = new();
    private readonly Dictionary<string, Dictionary<string, string>> _prevWatch = new();
    private readonly HashSet<int> _breakpoints = new();
    private readonly SyntaxGraph _graph;
    private readonly DispatcherTimer _parseTimer;
    private readonly DispatcherTimer _uiTimer;
    private UiWriter? _writer;
    private SyntaxNode? _lastNode;
    private int _currentLine = -1;
    private bool _running;
    private bool _building;
    private bool _syncingFromSource;
    private bool _syncingZoom;

    public MainWindow()
    {
        InitializeComponent();
        InitRecording();
        InitFlow();
        InitSamples();

        _graph = new SyntaxGraph(GraphScroll);
        _graph.NodeClicked += node => { if (!_running) SelectInSource(node.Span, Palette.Amber); };
        _graph.ZoomChanged += () =>
        {
            _syncingZoom = true;
            ZoomSlider.Value = _graph.Zoom * 100;
            ZoomText.Text = $"{(int)(_graph.Zoom * 100)}%";
            _syncingZoom = false;
        };

        _parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _parseTimer.Tick += (_, _) => { _parseTimer.Stop(); Parse(); };

        // 出力のまとめ書きと、早送り中のステップ数表示
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) =>
        {
            FlushOutput();
            if (!_running) return;
            if (_measuring)
                StatusText.Text = $"📏 測定中… N = {_measuringN:N0}　{Tracer.StepCount:N0} ステップ";
            else if (Tracer.FastForward && !Tracer.Paused)
                StatusText.Text = $"⏩ 早送り中… {Tracer.StepCount:N0} ステップ";
            if (!_measuring) DrawHeatmap();
            if (!_measuring && FlowTabVisible) RefreshFlow();
            UpdateTimeline();
        };
        _uiTimer.Start();

        AstView.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TreeViewItem item) EnsureChildren(item);
        }));

        WatchList.ItemsSource = _watch;
        InitComplexity();
        Tracer.OnHit = OnHit;
        SourceBox.Text = Sample;
        InputBox.Text = SampleInput.Replace("\n", Environment.NewLine);
        _parseTimer.Stop();
        Parse();
        UpdateSpeedText();
        StatusText.Text = "▶ 実行 で開始、⏭ ステップ で1つずつ、⏩ 早送り でブレークポイントまで飛ばします";
    }

    // ================= 構文解析と表示 =================

    private void Parse()
    {
        _tree = CSharpSyntaxTree.ParseText(SourceBox.Text, CodeRunner.ParseOptions);
        _items.Clear();
        _treeValueLabels.Clear();
        _values.Clear();
        _trail.Clear();
        _lastNode = null;
        _currentLine = -1;

        AstView.Items.Clear();
        var rootItem = CreateItem(_tree.GetRoot());
        AstView.Items.Add(rootItem);
        ExpandInitially(rootItem, 0);

        _graph.ClearCache();
        ShowIdleGraph();
        DrawGutterLater();
        InvalidateFlow();
    }

    private void ShowIdleGraph()
    {
        if (_tree is null) return;
        var root = _tree.GetRoot();
        if (FocusRadio.IsChecked == true && root.DescendantNodes().Count() > LargeTreeNodes)
            _graph.ShowMessage("コードが大きいので、全体のグラフは自動では描きません。\n実行すると、実行中の文のグラフが表示されます（「全体」を選ぶと全体を描きます）。");
        else
            _graph.Show(root, false, ValueOf);
    }

    private string? ValueOf(SyntaxNode node) => _values.TryGetValue(node, out var v) ? v : null;

    // ---- ツリー（子ノードは開いたときに作る） ----

    private TreeViewItem CreateItem(SyntaxNodeOrToken nodeOrToken)
    {
        var item = new TreeViewItem();
        if (nodeOrToken.AsNode() is { } node)
        {
            var panel = MakeHeader(node.Kind().ToString(), Palette.Snippet(node), Palette.KindBrush(node), bold: true);
            var valueLabel = new TextBlock { Foreground = Palette.Value, FontWeight = FontWeights.Bold };
            if (_values.TryGetValue(node, out var v)) valueLabel.Text = "  → " + v;
            panel.Children.Add(valueLabel);
            _treeValueLabels[node] = valueLabel;
            item.Header = panel;
            item.Tag = node;
            _items[node] = item;
            if (node.ChildNodesAndTokens().Count > 0)
                item.Items.Add(new TreeViewItem { Header = "…", Tag = Placeholder });
        }
        else
        {
            var token = nodeOrToken.AsToken();
            item.Header = MakeHeader(token.Kind().ToString(), $"'{token.Text}'", Palette.Token, bold: false);
            item.Tag = token;
        }
        return item;
    }

    private void EnsureChildren(TreeViewItem item)
    {
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: var tag } || !ReferenceEquals(tag, Placeholder)) return;
        item.Items.Clear();
        foreach (var child in ((SyntaxNode)item.Tag).ChildNodesAndTokens())
            item.Items.Add(CreateItem(child));
    }

    private void ExpandInitially(TreeViewItem item, int depth)
    {
        if (depth >= 4 || item.Tag is not SyntaxNode) return;
        EnsureChildren(item);
        item.IsExpanded = true;
        foreach (var child in item.Items.OfType<TreeViewItem>())
            ExpandInitially(child, depth + 1);
    }

    /// <summary>そのノードのツリー項目を（必要なら親から順に作って）返す。</summary>
    private TreeViewItem? EnsureItem(SyntaxNode node)
    {
        if (_items.TryGetValue(node, out var found)) return found;
        foreach (var ancestor in node.Ancestors().Reverse())
            if (_items.TryGetValue(ancestor, out var item)) EnsureChildren(item);
        return _items.GetValueOrDefault(node);
    }

    private static StackPanel MakeHeader(string kind, string? text, Brush kindBrush, bool bold)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 0, 4, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = kind,
            Foreground = kindBrush,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (!string.IsNullOrEmpty(text))
            panel.Children.Add(new TextBlock { Text = "  " + text, Foreground = Palette.SnippetText });
        return panel;
    }

    private void SourceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        DrawGutterLater();
        if (_running || _building) return;
        ClearSourceHighlight();
        ClearHeatmap();
        ClearHistory(); // 記録は前のコードの構文木に結びついているので捨てる
        _parseTimer.Stop();
        _parseTimer.Start();
    }

    // ツリーをクリック → ソースの該当箇所を選択
    private void AstView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_running || _syncingFromSource || e.NewValue is not TreeViewItem item) return;
        TextSpan? span = item.Tag switch
        {
            SyntaxNode n => n.Span,
            SyntaxToken t => t.Span,
            _ => null,
        };
        if (span is { } s) SelectInSource(s, Palette.Amber);
    }

    // ソースのカーソル位置 → ツリーの該当トークンを選択
    private void SourceBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_running || _tree is null || _parseTimer.IsEnabled || !SourceBox.IsKeyboardFocused) return;
        var token = _tree.GetRoot().FindToken(Math.Min(SourceBox.CaretIndex, Math.Max(0, SourceBox.Text.Length - 1)));
        if (token.Parent is not { } parent || EnsureItem(parent) is not { } parentItem) return;
        EnsureChildren(parentItem);

        var target = parentItem.Items.OfType<TreeViewItem>()
            .FirstOrDefault(i => i.Tag is SyntaxToken t && t == token) ?? parentItem;

        _syncingFromSource = true;
        try
        {
            ExpandAncestors(target);
            target.IsSelected = true;
            BringIntoViewLater(target);
        }
        finally
        {
            _syncingFromSource = false;
        }
    }

    private void GraphMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_graph is null || _tree is null) return;
        if (_lastNode is not null) ShowGraphFor(_lastNode);
        else ShowIdleGraph();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_graph is null || _syncingZoom) return;
        _graph.Zoom = e.NewValue / 100;
        ZoomText.Text = $"{(int)e.NewValue}%";
    }

    // ================= ブレークポイント =================

    private void Gutter_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        double y = e.GetPosition(Gutter).Y;
        foreach (var (line, rect) in VisibleLines())
        {
            if (y >= rect.Top && y < rect.Bottom)
            {
                ToggleBreakpoint(line);
                break;
            }
        }
    }

    private void SourceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9) return;
        int line = SourceBox.GetLineIndexFromCharacterIndex(SourceBox.CaretIndex);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) EditBreakpointCondition(line);
        else ToggleBreakpoint(line);
        e.Handled = true;
    }

    private void ToggleBreakpoint(int line)
    {
        if (line < 0) return;
        if (!_breakpoints.Remove(line)) _breakpoints.Add(line);
        else _breakpointConditions.Remove(line); // ブレークポイントを外したら条件も消す
        UpdateTracerBreakpoints();
        DrawGutter();
    }

    /// <summary>ブレークポイントの行を、計装ポイント ID ごとのフラグに変換して Tracer に渡す。</summary>
    private void UpdateTracerBreakpoints()
    {
        if (_map.Count == 0 || _breakpoints.Count == 0)
        {
            Tracer.Breakpoints = null;
            Tracer.ConditionalBreakpoints = null;
            return;
        }
        var flags = new bool[_map.Count];
        var conditional = new bool[_map.Count];
        foreach (var (id, info) in _map)
        {
            if (info.Kind == HitKind.Expression) continue;
            int line = LineOf(info.Node);
            if (!_breakpoints.Contains(line)) continue;

            if (_breakpointConditions.TryGetValue(line, out var condition))
            {
                // 条件はコンパイル済みのものしか使えない。実行中に変えた条件は、次の実行まで止まらない
                if (IsConditionCompiled(line, condition) && _compiledConditionIds.ContainsKey(id))
                {
                    flags[id] = true;
                    conditional[id] = true;
                }
            }
            else
            {
                flags[id] = true;
            }
        }
        // ConditionalBreakpoints を先に入れる（Breakpoints だけ新しい瞬間に条件なしで止まらないように）
        Tracer.ConditionalBreakpoints = conditional;
        Tracer.Breakpoints = flags;
    }

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line;

    private IEnumerable<(int Line, Rect Rect)> VisibleLines()
    {
        int first = SourceBox.GetFirstVisibleLineIndex(), last = SourceBox.GetLastVisibleLineIndex();
        if (first < 0 || last < 0) yield break;
        for (int line = first; line <= last; line++)
        {
            int index = SourceBox.GetCharacterIndexFromLineIndex(line);
            if (index < 0) continue; // レイアウトが追いついていない行
            var rect = SourceBox.GetRectFromCharacterIndex(index);
            if (!rect.IsEmpty) yield return (line, rect);
        }
    }

    private void DrawGutterLater() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DrawGutter);

    private static readonly Brush BreakpointBrush = Palette.Freeze(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush CurrentLineBrush = Palette.Freeze(Color.FromArgb(0x90, 0xFF, 0xD5, 0x4F));
    private static readonly Brush LineNumberBrush = Palette.Freeze(Color.FromRgb(0x90, 0x90, 0x90));

    private void DrawGutter()
    {
        if (Gutter is null) return;
        Gutter.Children.Clear();
        foreach (var (line, rect) in VisibleLines())
        {
            if (line == _currentLine)
            {
                var bar = new Rectangle { Width = Gutter.ActualWidth, Height = rect.Height, Fill = CurrentLineBrush };
                Canvas.SetTop(bar, rect.Top);
                Gutter.Children.Add(bar);
            }
            if (_breakpoints.Contains(line))
                DrawBreakpointMark(line, rect);
            var number = new TextBlock
            {
                Text = (line + 1).ToString(), Width = 30, TextAlignment = TextAlignment.Right,
                FontFamily = SourceBox.FontFamily, FontSize = 12, Foreground = LineNumberBrush,
            };
            Canvas.SetLeft(number, 18);
            Canvas.SetTop(number, rect.Top + (rect.Height - 16) / 2);
            Gutter.Children.Add(number);
        }
    }

    // ================= 実行 =================

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            StartRun(RunMode.Animate);
            return;
        }
        Tracer.FastForward = false;
        Tracer.Paused = false;
        UpdateButtons();
    }

    private void Fast_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            StartRun(RunMode.Fast);
            return;
        }
        Tracer.FastForward = true;
        Tracer.Paused = false;
        UpdateButtons();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        Tracer.FastForward = false;
        Tracer.Paused = true;
        UpdateButtons();
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            StartRun(RunMode.Step);
        }
        else if (!Tracer.Paused)
        {
            Tracer.FastForward = false;
            Tracer.Paused = true;
        }
        else
        {
            Tracer.Step();
        }
        UpdateButtons();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _measureCancel = true;
        Tracer.RequestStop();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Tracer.OnHit = null;
        Tracer.RequestStop();
        AbortRecordings();
        DisposeSound();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Tracer.DelayMs = (int)e.NewValue;
        UpdateSpeedText();
    }

    private void UpdateSpeedText()
    {
        if (SpeedText is null) return;
        int ms = (int)SpeedSlider.Value;
        SpeedText.Text = ms < Tracer.ThrottleBelowMs ? $"{ms} ms（間引き）" : $"{ms} ms";
    }

    private async void StartRun(RunMode mode)
    {
        if (_running || _building) return;
        _parseTimer.Stop();
        Parse();
        OutputBox.Clear();
        _watch.Clear();
        _prevWatch.Clear();
        WatchTitle.Text = "変数ウォッチ";

        var tree = _tree!;
        bool watch = WatchCheck.IsChecked == true;
        bool exprs = ExprCheck.IsChecked == true;
        string input = InputBox.Text;

        _building = true;
        UpdateButtons();
        StatusText.Text = "コンパイル中…";
        BuildResult result;
        try
        {
            var conditions = ActiveBreakConditions();
            result = await Task.Run(() => CodeRunner.Build(tree, watch, exprs, conditions, calls: watch));
        }
        catch (Exception ex)
        {
            AppendOutput($"💥 コンパイル処理で例外: {ex}\n");
            return;
        }
        finally
        {
            _building = false;
            UpdateButtons();
        }

        foreach (var note in result.Notes) AppendOutput("ℹ " + note + "\n");
        if (result.Bytes is not { } bytes)
        {
            AppendOutput("❌ コンパイルエラー\n" + string.Join("\n", result.Errors) + "\n");
            StatusText.Text = "コンパイルエラー";
            return;
        }

        _map = result.Map;
        ArrayPanel.Clear(watch ? null : "「変数ウォッチ」がオフなので、配列の絵は出ません。");
        _uiErrorReported = false;
        Tracer.Reset();
        StartCalls(result, watch);
        StartMemory(result, watch);
        StartFlow(result);
        AcceptCompiledConditions(result);
        Tracer.Paused = mode == RunMode.Step;
        Tracer.FastForward = mode == RunMode.Fast;
        Tracer.DelayMs = (int)SpeedSlider.Value;
        UpdateTracerBreakpoints();
        StartHistory(RecordCheck.IsChecked == true);
        BuildHeatIndex();
        _heatCounts = new long[_map.Count];
        Tracer.Counts = _heatCounts;
        _running = true;
        UpdateButtons();
        AppendOutput($"▶ 実行開始（計装ポイント {_map.Count} 個）\n");
        if (mode == RunMode.Fast)
            StatusText.Text = _breakpoints.Count == 0 ? "⏩ 早送り中…（ブレークポイントなし：最後まで走ります）" : "⏩ 早送り中…";

        var writer = new UiWriter();
        _writer = writer;
        Console.SetOut(writer);
        Console.SetIn(new StringReader(input));

        var thread = new Thread(() =>
        {
            string message;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                CodeRunner.Execute(bytes);
                Tracer.Flow?.Finish(); // 最後のブロックから「終了」までの道筋を数える
                message = "✅ 実行終了";
            }
            catch (ExecutionStoppedException)
            {
                message = "⏹ 停止しました";
            }
            catch (Exception ex)
            {
                message = $"💥 例外: {ex.GetType().Name}: {ex.Message}";
            }
            var elapsed = sw.Elapsed;
            Dispatcher.BeginInvoke(() =>
            {
                FlushOutput();
                AppendOutput("\n" + message + "\n");
                StatusText.Text = $"{message}（{Tracer.StepCount:N0} ステップ / {elapsed.TotalSeconds:N1} 秒）";
                _running = false;
                UpdateButtons();
                DrawHeatmap();
                UpdateTimeline();
                FinishCalls();
                ShowFlowTab();
            });
        })
        { IsBackground = true, Name = "UserCode", };
        thread.Start();
    }

    private void UpdateButtons()
    {
        bool busy = _running || _building;
        bool paused = _running && Tracer.Paused;
        bool fast = _running && Tracer.FastForward && !Tracer.Paused;
        bool controllable = !_building && !_measuring; // 測定中は再生の操作をさせない

        RunButton.Content = _running && !_measuring ? "▶ 再開" : "▶ 実行";
        RunButton.IsEnabled = controllable && (!_running || paused || fast);
        FastButton.IsEnabled = controllable && (!_running || !fast);
        PauseButton.IsEnabled = controllable && _running && !paused;
        StepButton.IsEnabled = controllable;
        StopButton.IsEnabled = _running;
        MeasureButton.IsEnabled = !busy;
        UpdateTimeline();
        SourceBox.IsReadOnly = busy;
        SourceBox.Background = busy ? new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF5)) : Brushes.White;
    }

    // ================= ハイライト =================

    // ユーザーコードのスレッドから呼ばれる
    private void OnHit(HitEvent ev)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    Highlight(ev);
                }
                catch (Exception ex)
                {
                    // 表示側のバグで、ユーザーのプログラムを止めない（例外として報告もしない）
                    if (!_uiErrorReported)
                    {
                        _uiErrorReported = true;
                        AppendOutput($"⚠ 表示の処理でエラーが出ました（実行は続けます）: {ex}\n");
                    }
                }
            });
        }
        catch (TaskCanceledException)
        {
            // ウィンドウを閉じている最中
        }
    }

    private bool _uiErrorReported;

    /// <param name="live">false なら巻き戻しの再生（記録から過去の状態を組み立てている）</param>
    private void Highlight(HitEvent ev, bool live = true)
    {
        if (!_map.TryGetValue(ev.Id, out var info)) return;
        var node = info.Node;
        _lastNode = node;
        if (live)
        {
            _viewingHistory = false;
            UpdateButtons();
            PlaySound(info, ev);
        }

        var color = info.Kind switch
        {
            HitKind.Condition => ev.Value == "true" ? Palette.TrueGreen : Palette.FalseRed,
            HitKind.Expression => Palette.ExprPurple,
            _ => Palette.Amber,
        };

        if (info.Kind == HitKind.Statement)
        {
            ClearValuesUnder(node); // この文を新しく実行するので、前回の式の値を消す
            UpdateWatch(info, ev.Vars);
            if (info.VarNames.Length > 0) ArrayPanel.Update(info.VarNames, info.VarTypes, ev.Vars, ev.Shots);
            UpdateMemory(ev);
        }
        else
        {
            SetValue(node, ev.Value);
        }
        if (info.Kind != HitKind.Expression) UpdateFlow(ev, live);
        UpdateCalls(ev.Step);

        int line = LineOf(node);
        bool atBreakpoint = live && Tracer.Paused && info.Kind != HitKind.Expression && _breakpoints.Contains(line);
        string prefix = !live ? "🕰 過去　"
            : atBreakpoint && _breakpointConditions.TryGetValue(line, out var bpCondition) ? $"🔴 条件付きブレークポイント（{bpCondition}）　"
            : atBreakpoint ? "🔴 ブレークポイント　" : "";
        string steps = $"ステップ {ev.Step:N0}　行 {line + 1}　" + (live && Tracer.MeasureDepth && ev.Depth > 0 ? $"深さ {ev.Depth}　" : "");
        StatusText.Text = prefix + steps + info.Kind switch
        {
            HitKind.Statement => $"{node.Kind()}　[{info.Scope}]",
            HitKind.Condition => $"条件 {Palette.Snippet(node)} → {ev.Value}",
            _ => $"式 {Palette.Snippet(node)} → {ev.Value}",
        };

        // ツリー（表示しているときだけ）
        if (ViewTabs.SelectedIndex == 1 && EnsureItem(node) is { } item)
        {
            UpdateTrail(item, color);
            ExpandAncestors(item);
            BringIntoViewLater(item);
        }

        // グラフ
        ShowGraphFor(node);
        _graph.Highlight(node, color);

        // ソース
        _currentLine = line;
        SelectInSource(node.Span, color);
        DrawGutter();
    }

    private void ShowGraphFor(SyntaxNode node)
    {
        bool focus = FocusRadio.IsChecked == true;
        var root = focus ? FocusRoot(node) : _tree!.GetRoot();
        if (_graph.Root != root || _graph.Collapse != focus)
            _graph.Show(root, focus, ValueOf);
    }

    // 「実行中の文だけ」モードで表示する部分木の根
    private SyntaxNode FocusRoot(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(a => a is StatementSyntax and not BlockSyntax)
        ?? node.AncestorsAndSelf().FirstOrDefault(a => a is MemberDeclarationSyntax)
        ?? _tree!.GetRoot();

    private void SetValue(SyntaxNode node, string? value)
    {
        if (value is null) _values.Remove(node);
        else _values[node] = value;
        _graph.SetValue(node, value);
        if (_treeValueLabels.TryGetValue(node, out var label)) label.Text = value is null ? "" : "  → " + value;
    }

    private void ClearValuesUnder(SyntaxNode statement)
    {
        if (_values.Count == 0) return;
        foreach (var d in statement.DescendantNodes())
            if (_values.ContainsKey(d)) SetValue(d, null);
    }

    private void UpdateWatch(HitInfo info, string[]? values)
    {
        WatchTitle.Text = (string.IsNullOrEmpty(info.Scope) ? "変数ウォッチ" : $"変数ウォッチ — {info.Scope}")
                          + (_viewingHistory ? "　🕰 過去の値" : "");
        _watch.Clear();
        if (values is null) return;

        _prevWatch.TryGetValue(info.Scope, out var prev);
        var now = new Dictionary<string, string>();
        for (int i = 0; i < values.Length && i < info.VarNames.Length; i++)
        {
            var name = info.VarNames[i];
            bool changed = prev is not null && (!prev.TryGetValue(name, out var old) || old != values[i]);
            _watch.Add(new WatchRow(name, values[i], info.VarTypes[i], changed));
            now[name] = values[i];
        }
        _prevWatch[info.Scope] = now;
    }

    // 最新のノードを強く、少し前に実行したノードを薄く光らせる
    private void UpdateTrail(TreeViewItem current, Color color)
    {
        _trail.Remove(current);
        _trail.Insert(0, current);
        while (_trail.Count > TrailLength)
        {
            _trail[^1].ClearValue(BackgroundProperty);
            _trail.RemoveAt(_trail.Count - 1);
        }
        for (int i = 0; i < _trail.Count; i++)
            _trail[i].Background = Palette.TrailBrush(i, color);
    }

    private static void ExpandAncestors(TreeViewItem item)
    {
        for (var p = item.Parent as TreeViewItem; p is not null; p = p.Parent as TreeViewItem)
            p.IsExpanded = true;
    }

    private void BringIntoViewLater(TreeViewItem item) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => item.BringIntoView());

    // ================= ソースのハイライト（TextBox の上に重ねて描く） =================

    private TextSpan? _sourceSpan;
    private Color _sourceColor;

    private void SelectInSource(TextSpan span, Color color)
    {
        if (span.End > SourceBox.Text.Length) return;
        _sourceSpan = span;
        _sourceColor = color;

        // 見えていなければスクロール（ScrollChanged で描き直される）
        int first = SourceBox.GetFirstVisibleLineIndex(), last = SourceBox.GetLastVisibleLineIndex();
        int line = SourceBox.GetLineIndexFromCharacterIndex(span.Start);
        if (first < 0 || line < first || line > last - 1) SourceBox.ScrollToLine(Math.Max(0, line - 3));
        DrawSourceHighlight();
    }

    private void ClearSourceHighlight()
    {
        _sourceSpan = null;
        SourceOverlay.Children.Clear();
    }

    private void SourceBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        DrawHeatmap();
        DrawSourceHighlight();
        DrawGutter();
    }

    private void SourceBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawHeatmap();
        DrawSourceHighlight();
        DrawGutter();
    }

    private void DrawSourceHighlight()
    {
        if (SourceOverlay is null) return;
        SourceOverlay.Children.Clear();
        if (_sourceSpan is not { } span || span.End > SourceBox.Text.Length) return;

        var text = SourceBox.Text;
        var fill = new SolidColorBrush(Color.FromArgb(110, _sourceColor.R, _sourceColor.G, _sourceColor.B));
        var stroke = new SolidColorBrush(_sourceColor);
        int startLine = SourceBox.GetLineIndexFromCharacterIndex(span.Start);
        int endLine = SourceBox.GetLineIndexFromCharacterIndex(Math.Max(span.Start, span.End - 1));

        for (int line = startLine; line <= endLine; line++)
        {
            // スクロールした直後はレイアウトが追いついておらず -1 が返ることがある（ScrollChanged で描き直される）
            int lineStart = SourceBox.GetCharacterIndexFromLineIndex(line);
            if (lineStart < 0) continue;
            int lineEnd = Math.Min(text.Length, lineStart + SourceBox.GetLineLength(line));
            while (lineEnd > lineStart && (text[lineEnd - 1] == '\n' || text[lineEnd - 1] == '\r')) lineEnd--;

            int s = Math.Max(span.Start, lineStart), e = Math.Min(span.End, lineEnd);
            while (s < e && char.IsWhiteSpace(text[s])) s++; // 行頭のインデントは塗らない
            if (s >= e) continue;

            var r1 = SourceBox.GetRectFromCharacterIndex(s);
            var r2 = SourceBox.GetRectFromCharacterIndex(e - 1, trailingEdge: true);
            if (r1.IsEmpty || r2.IsEmpty) continue;

            var rect = new Rectangle
            {
                Width = Math.Max(4, r2.X - r1.X + 2),
                Height = r1.Height + 2,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
                RadiusX = 3,
                RadiusY = 3,
            };
            Canvas.SetLeft(rect, r1.X - 1);
            Canvas.SetTop(rect, r1.Y - 1);
            SourceOverlay.Children.Add(rect);
        }
    }

    // ================= Console 出力（まとめて書き出す） =================

    private void AppendOutput(string text)
    {
        OutputBox.AppendText(text);
        OutputBox.ScrollToEnd();
    }

    private void FlushOutput()
    {
        var text = _writer?.Drain();
        if (!string.IsNullOrEmpty(text)) AppendOutput(text);
    }

    private sealed class UiWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_buffer) _buffer.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_buffer) _buffer.Append(buffer, index, count);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_buffer) _buffer.Append(value);
        }

        public string Drain()
        {
            lock (_buffer)
            {
                var s = _buffer.ToString();
                _buffer.Clear();
                return s;
            }
        }
    }
}
