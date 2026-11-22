using System.Windows;
using System.Windows.Controls;

namespace HotaruCodeViz;

// 再帰の見える化：呼び出しの木（🌳 呼び出しタブ）と呼び出し履歴（変数ウォッチの上の「Main › Fact(5) › …」）
public partial class MainWindow
{
    private CallLog? _calls;
    private Dictionary<int, string> _callNames = new();
    private long _callStep;

    private bool CallTabVisible => ViewTabs.SelectedItem == CallTab;

    /// <summary>実行の開始時。watch がオフなら呼び出しの木も取らない。</summary>
    private void StartCalls(BuildResult result, bool enabled)
    {
        _callNames = (result.Methods ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        _calls = enabled && _callNames.Count > 0 ? new CallLog() : null;
        Tracer.Calls = _calls;
        _callStep = 0;
        CallStackText.Text = "";
        CallStackText.Visibility = Visibility.Collapsed;
        CallTree.ShowMessage(!enabled ? "「変数ウォッチ」がオフなので、呼び出しの木は記録しません。"
            : _calls is null ? "呼び出しの木に入れられるメソッドがありません。" : "実行するとここに呼び出しの木を描きます。");
    }

    /// <summary>ステップ step を表示しているときの呼び出し履歴・木を更新する（ライブでも巻き戻しでも）。</summary>
    private void UpdateCalls(long step)
    {
        if (_calls is not { } log) return;
        _callStep = step;

        // 呼び出し履歴（今いる道筋）
        var path = new List<string>();
        int total;
        lock (log.Lock)
        {
            int c = log.CurrentAt(step, out total);
            for (; c >= 0; c = log.Nodes[c].Parent)
            {
                var n = log.Nodes[c];
                var name = _callNames.TryGetValue(n.MethodId, out var s) ? s : "?";
                path.Add(n.Args is { Length: > 0 } a ? $"{name}({string.Join(", ", a)})" : name);
            }
        }
        path.Reverse();
        const int keep = 6;
        if (path.Count > keep) path = path.Take(2).Append($"…{path.Count - keep + 2}段…").Concat(path.Skip(path.Count - (keep - 2))).ToList();
        CallStackText.Text = path.Count == 0 ? "" : "📞 " + string.Join(" › ", path);
        CallStackText.Visibility = path.Count <= 1 ? Visibility.Collapsed : Visibility.Visible; // Main だけなら出さない

        if (CallTabVisible) RefreshCallTree();
    }

    private void RefreshCallTree()
    {
        if (_calls is not { } log) return;
        var (_, visible, depth) = CallTree.Update(log, _callStep, _callNames);
        long overflow;
        lock (log.Lock) overflow = log.Overflow;
        CallInfo.Text = $"呼び出し {visible:N0} 回　今の深さ {depth}"
                        + (visible > CallTreeView.FullLimit ? "　（多いので、上のほうの段と今いる道筋だけ描いています。+N は描いていない呼び出しの数）" : "")
                        + (overflow > 0 ? $"　※ {CallLog.MaxNodes:N0} 回を超えた分（{overflow:N0} 回）は記録していません" : "");

        // 今の呼び出しが見えるようにスクロール
        if (CallTree.CurrentRect is { } r)
        {
            CallScroll.UpdateLayout(); // 木の大きさが変わったのをスクロールの範囲に反映させてから
            if (r.Left < CallScroll.HorizontalOffset || r.Right > CallScroll.HorizontalOffset + CallScroll.ViewportWidth)
                CallScroll.ScrollToHorizontalOffset(Math.Max(0, r.Left + r.Width / 2 - CallScroll.ViewportWidth / 2));
            if (r.Top < CallScroll.VerticalOffset || r.Bottom > CallScroll.VerticalOffset + CallScroll.ViewportHeight)
                CallScroll.ScrollToVerticalOffset(Math.Max(0, r.Top + r.Height / 2 - CallScroll.ViewportHeight / 2));
        }
    }

    /// <summary>実行が終わったとき：最後の状態を描く。</summary>
    private void FinishCalls()
    {
        if (_calls is null) return;
        UpdateCalls(Tracer.StepCount + 1);
    }

    private void ViewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != ViewTabs) return; // 中の ListView などの SelectionChanged も上がってくる
        if (CallTabVisible) RefreshCallTree();
    }
}
