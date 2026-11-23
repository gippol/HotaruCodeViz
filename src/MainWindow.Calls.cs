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

    private void InitCalls()
    {
        CallTree.CallClicked += index => JumpToCall(index, atEnd: false);
        CallTree.CallRightClicked += ShowCallMenu;
    }

    private void ShowCallMenu(int index)
    {
        string label = CallLabel(index);
        var start = new MenuItem { Header = $"{label} の最初のステップへ戻る" };
        var end = new MenuItem { Header = $"{label} が戻る直前のステップへ" };
        start.Click += (_, _) => JumpToCall(index, atEnd: false);
        end.Click += (_, _) => JumpToCall(index, atEnd: true);
        start.IsEnabled = end.IsEnabled = CanTimeTravel;
        new ContextMenu { Items = { start, end }, PlacementTarget = CallTree, IsOpen = true };
    }

    /// <summary>呼び出しの見出し（「Fib(3)」など）。</summary>
    private string CallLabel(int index)
    {
        if (_calls is not { } log) return "?";
        lock (log.Lock)
        {
            if (index < 0 || index >= log.Nodes.Count) return "?";
            var n = log.Nodes[index];
            var name = _callNames.TryGetValue(n.MethodId, out var s) ? s : "?";
            var label = $"{name}({string.Join(", ", n.Args ?? [])})";
            return label.Length <= 30 ? label : label[..29] + "…";
        }
    }

    /// <summary>
    /// 呼び出しの木の箱から、その呼び出しの最初のステップ（atEnd なら戻る直前のステップ）へ巻き戻す。
    /// 呼ばれた時点・戻った時点のステップ数は CallLog に記録してあるので、記録（ステップ数の順）を二分探索して探す。
    /// </summary>
    private void JumpToCall(int index, bool atEnd)
    {
        if (_calls is not { } log) return;
        if (_history is not { Count: > 0 } history)
        {
            StatusText.Text = "巻き戻しの記録がないので戻れません（「記録」をオンにして実行してください）";
            return;
        }
        if (!CanTimeTravel)
        {
            StatusText.Text = "呼び出しの木から戻るのは、一時停止中か実行が終わったあとに使えます";
            return;
        }

        long enter, exit;
        string? result;
        CallState state;
        lock (log.Lock)
        {
            if (index < 0 || index >= log.Nodes.Count) return;
            var n = log.Nodes[index];
            (enter, exit, result, state) = (n.EnterStep, n.ExitStep, n.Result, n.State);
        }
        string label = CallLabel(index);

        // 呼び出しの中のステップは enter < step <= exit
        int target;
        if (!atEnd)
        {
            target = FirstHistoryIndexAfter(history, enter);
            if (target >= history.Count || (exit >= 0 && history.Get(target).Step > exit))
            {
                StatusText.Text = $"🌳 {label} の中で記録したステップがありません";
                return;
            }
            if (target == 0 && history.Get(0).Step > enter + 1)
            {
                StatusText.Text = $"🌳 {label} の最初のステップは、古い記録として破棄されています（記録は直近 {history.Capacity:N0} ステップまで）";
                return;
            }
        }
        else
        {
            if (exit < 0)
            {
                StatusText.Text = $"🌳 {label} はまだ戻っていません";
                return;
            }
            target = FirstHistoryIndexAfter(history, exit) - 1;
            if (target < 0 || history.Get(target).Step <= enter)
            {
                StatusText.Text = history.Dropped > 0
                    ? $"🌳 {label} の中のステップは、古い記録として破棄されています（記録は直近 {history.Capacity:N0} ステップまで）"
                    : $"🌳 {label} の中で記録したステップがありません";
                return;
            }
            // return F(x) のように最後に別の呼び出しをしたときは、最後のステップがその中になるので、
            // この呼び出し自身のステップまでさかのぼる（式の値の記録などがあれば見つかる）
            for (int q = target, limit = Math.Max(0, target - ReplaySearchLimit); q >= limit; q--)
            {
                long qStep = history.Get(q).Step;
                if (qStep <= enter) break;
                if (FrameAt(qStep) == index)
                {
                    target = q;
                    break;
                }
            }
        }

        MoveTimeline(target); // ValueChanged → ShowHistory（ステータスもここで書き換わる）
        var ev = history.Get(target);
        int line = _map.TryGetValue(ev.Id, out var info) ? LineOf(info.Node) + 1 : 0;
        StatusText.Text = atEnd
            ? $"🌳 {label} が戻る直前　ステップ {ev.Step:N0}　行 {line}　" +
              (state == CallState.Threw ? "（このあと例外で抜けます）" : result is null ? "" : $"（→ {result} を返します）")
            : $"🌳 {label} の最初のステップ　ステップ {ev.Step:N0}　行 {line}（右クリックで「戻る直前へ」も選べます）";
    }

    /// <summary>記録の中で、ステップ数が step より大きい最初の位置（なければ Count）。記録はステップ数の順に並んでいる。</summary>
    private static int FirstHistoryIndexAfter(ExecutionHistory history, long step)
    {
        int lo = 0, hi = history.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (history.Get(mid).Step <= step) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

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
        ShowFlowTab();
    }
}
