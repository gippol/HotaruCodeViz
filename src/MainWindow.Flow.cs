using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;

namespace HotaruCodeViz;

// 🔀 制御フロータブ：メソッドごとの制御フローグラフ（基本ブロックと分岐・合流・ループ）
public partial class MainWindow
{
    private FlowProgram? _flow;          // 表示しているプログラムのグラフ（実行前は今のコードから作った下見）
    private FlowTracker? _flowTracker;   // 実行中・実行後の回数（下見のときは null）
    private bool _flowStale = true;      // コードが変わったので、下見を作り直す
    private int _flowCurrent = -1;       // 今実行中のブロック（表示しているグラフの中の番号）
    private int[]? _flowLastPath;
    private bool _syncingFlowCombo;

    private bool FlowTabVisible => ViewTabs.SelectedItem == FlowTab;

    private void InitFlow()
    {
        FlowPanel.BlockClicked += block =>
        {
            if (!_running && block.SourceSpan is { } span) SelectInSource(span, Palette.Amber);
        };
    }

    /// <summary>コードが変わった（Parse のあと）。表示しているなら、あとで下見を作る。</summary>
    private void InvalidateFlow()
    {
        if (_running || _building) return;
        _flowStale = true;
        if (FlowTabVisible) Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildPreviewFlow);
    }

    /// <summary>実行前の下見：今のコードから制御フローグラフだけ作る（回数はなし）。</summary>
    private void BuildPreviewFlow()
    {
        if (!_flowStale || _running || _building || _tree is null) return;
        _flowStale = false;
        try
        {
            var tree = _tree;
            var compilation = CodeRunner.CreateCompilation(tree);
            SetFlow(FlowProgram.Build(tree, compilation.GetSemanticModel(tree)), null);
            if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error) && _flow is { Graphs.Length: > 0 })
                FlowInfo.Text = "⚠ コンパイルエラーがあるので、グラフが正しくないかもしれません。\n" + FlowInfo.Text;
        }
        catch (Exception ex)
        {
            SetFlow(null, null);
            FlowPanel.ShowMessage($"制御フローグラフを作れませんでした（{ex.GetType().Name}）");
        }
    }

    /// <summary>実行の開始時（Tracer.Reset のあと）。</summary>
    private void StartFlow(BuildResult result)
    {
        _flowStale = false;
        var tracker = result.Flow is { } flow ? new FlowTracker(flow, result.Map.Count) : null;
        Tracer.Flow = tracker;
        SetFlow(result.Flow, tracker);
    }

    private void SetFlow(FlowProgram? flow, FlowTracker? tracker)
    {
        _flow = flow;
        _flowTracker = tracker;
        _flowCurrent = -1;
        _flowLastPath = null;
        FlowPanel.ClearCache();

        _syncingFlowCombo = true;
        FlowGraphCombo.Items.Clear();
        if (flow is not null)
            foreach (var g in flow.Graphs)
                FlowGraphCombo.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g });
        _syncingFlowCombo = false;

        if (flow is null || flow.Graphs.Length == 0)
        {
            FlowPanel.ShowMessage(flow is null ? "制御フローグラフはまだありません。" : "グラフにできるメソッドがありません。");
            FlowInfo.Text = "";
            return;
        }
        // 最初はプログラムの入口（トップレベルのステートメントか Main）
        var first = flow.Graphs.FirstOrDefault(g => g.Root is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)
                    ?? flow.Graphs.FirstOrDefault(g => g.Name.EndsWith(".Main", StringComparison.Ordinal))
                    ?? flow.Graphs[0];
        SelectFlowGraph(first);
    }

    private void SelectFlowGraph(FlowGraph graph)
    {
        _syncingFlowCombo = true;
        foreach (ComboBoxItem item in FlowGraphCombo.Items)
            if (item.Tag == graph) FlowGraphCombo.SelectedItem = item;
        _syncingFlowCombo = false;
        if (FlowPanel.Graph != graph)
        {
            _flowCurrent = -1;
            _flowLastPath = null;
        }
        RefreshFlow(graph);
    }

    private void FlowGraphCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFlowCombo || FlowGraphCombo.SelectedItem is not ComboBoxItem { Tag: FlowGraph graph }) return;
        // 自分で選んだら、実行中の場所を追うのはやめる（見たいグラフが勝手に変わらないように）
        if (_running) FlowFollowCheck.IsChecked = false;
        _flowCurrent = -1;
        _flowLastPath = null;
        RefreshFlow(graph);
    }

    /// <summary>文・条件のステップを表示するとき（ライブでも巻き戻しでも）。</summary>
    private void UpdateFlow(HitEvent ev, bool live)
    {
        if (_flow is null || !_flow.HitBlocks.TryGetValue(ev.Id, out var at)) return;
        var graph = _flow.Graphs[at.Graph];
        var shown = FlowPanel.Graph ?? graph;
        if (graph != shown && FlowFollowCheck.IsChecked == true) shown = graph;

        if (graph == shown)
        {
            _flowCurrent = at.Block;
            // 来た道筋はライブのときだけ（巻き戻しでは、記録に道筋がない）
            _flowLastPath = live ? _flowTracker?.LastPathInto[graph.Blocks[at.Block].Global] : null;
        }
        else
        {
            _flowCurrent = -1;
            _flowLastPath = null;
        }
        if (!FlowTabVisible) return;
        if (shown != FlowPanel.Graph) SelectFlowGraph(shown);
        else RefreshFlow(shown);
    }

    private void RefreshFlow(FlowGraph? graph = null)
    {
        graph ??= FlowPanel.Graph;
        if (graph is null) return;
        FlowPanel.Show(graph, _flowTracker, _flowCurrent, _flowLastPath);

        int blocks = graph.Blocks.Count(b => b.Kind == FlowBlockKind.Block);
        int unreachable = graph.Blocks.Count(b => !b.Reachable);
        int visited = _flowTracker is { } t ? graph.Blocks.Count(b => b.Kind == FlowBlockKind.Block && t.BlockCounts[b.Global] > 0) : 0;
        var parts = new List<string> { $"{graph.Name}　ブロック {blocks} 個" };
        if (_flowTracker is not null) parts.Add($"通ったブロック {visited} 個");
        if (unreachable > 0) parts.Add($"到達できない {unreachable} 個");
        FlowInfo.Text = string.Join("　", parts)
                        + (_viewingHistory ? "　🕰 過去を表示中（回数は最新までの合計）" : "")
                        + (_flowTracker is null ? "　（実行すると、通ったブロックと辺を数えます）" : "");

        if (FlowPanel.CurrentRect is { } r)
        {
            FlowScroll.UpdateLayout();
            if (r.Left < FlowScroll.HorizontalOffset || r.Right > FlowScroll.HorizontalOffset + FlowScroll.ViewportWidth)
                FlowScroll.ScrollToHorizontalOffset(Math.Max(0, r.Left + r.Width / 2 - FlowScroll.ViewportWidth / 2));
            if (r.Top < FlowScroll.VerticalOffset || r.Bottom > FlowScroll.VerticalOffset + FlowScroll.ViewportHeight)
                FlowScroll.ScrollToVerticalOffset(Math.Max(0, r.Top + r.Height / 2 - FlowScroll.ViewportHeight / 2));
        }
    }

    /// <summary>タブを開いたとき・早送り中の定期更新・実行の終わり。</summary>
    private void ShowFlowTab()
    {
        if (!FlowTabVisible) return;
        if (_flowStale) BuildPreviewFlow();
        RefreshFlow();
    }
}
