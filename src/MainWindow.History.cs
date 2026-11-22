using System.Windows;
using System.Windows.Input;

namespace HotaruCodeViz;

/// <summary>
/// 巻き戻し（タイムトラベル）。
/// 実行中に記録した HitEvent を使って、過去のステップの画面を組み立て直す。
/// （プログラムの状態そのものは戻せないので、「過去を見る」だけ。続きは最新の位置からしか実行できない）
/// </summary>
public partial class MainWindow
{
    private const int HistoryCapacity = 100_000;

    /// <summary>過去のステップを組み立てるとき、さかのぼって探す最大の歩数。</summary>
    private const int ReplaySearchLimit = 5_000;

    private ExecutionHistory? _history;
    private bool _viewingHistory;
    private bool _syncingTimeline;

    private void StartHistory(bool enabled)
    {
        _history = enabled ? new ExecutionHistory(HistoryCapacity) : null;
        Tracer.History = _history;
        _viewingHistory = false;
        UpdateTimeline();
    }

    private void ClearHistory()
    {
        _history = null;
        _viewingHistory = false;
        UpdateTimeline();
    }

    /// <summary>今「過去を見てよい」状態か（一時停止中か、実行が終わったあと）。</summary>
    private bool CanTimeTravel =>
        _history is { Count: > 0 } && !_measuring && !_building && (!_running || Tracer.Paused);

    private void UpdateTimeline()
    {
        if (TimelineSlider is null) return;
        int count = _history?.Count ?? 0;
        bool can = CanTimeTravel;

        _syncingTimeline = true;
        TimelineSlider.Maximum = Math.Max(0, count - 1);
        if (!_viewingHistory) TimelineSlider.Value = Math.Max(0, count - 1);
        _syncingTimeline = false;

        TimelineSlider.IsEnabled = can;
        HistFirstButton.IsEnabled = HistBackButton.IsEnabled = can;
        HistLatestButton.IsEnabled = can && _viewingHistory;
        HistForwardButton.IsEnabled = can && (_viewingHistory || (_running && Tracer.Paused));

        if (_history is null)
        {
            HistoryText.Text = RecordCheck?.IsChecked == true ? "実行すると、ここで過去のステップに戻れます" : "記録はオフです";
            return;
        }
        if (count == 0)
        {
            HistoryText.Text = "まだ記録がありません";
            return;
        }

        int index = (int)TimelineSlider.Value;
        long step = _history.Get(Math.Clamp(index, 0, count - 1)).Step;
        long dropped = _history.Dropped;
        string state = _viewingHistory ? "過去を表示中" : _running && !Tracer.Paused ? "記録中" : "最新";
        HistoryText.Text = $"{state}：ステップ {step:N0}（記録 {count:N0}{(dropped > 0 ? $"、古い {dropped:N0} は破棄" : "")}）";
    }

    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingTimeline || !CanTimeTravel) return;
        ShowHistory((int)e.NewValue);
    }

    private void MoveTimeline(int index)
    {
        if (!CanTimeTravel) return;
        TimelineSlider.Value = Math.Clamp(index, 0, (int)TimelineSlider.Maximum); // ValueChanged → ShowHistory
    }

    private void HistFirst_Click(object sender, RoutedEventArgs e) => MoveTimeline(0);

    private void HistBack_Click(object sender, RoutedEventArgs e) => MoveTimeline((int)TimelineSlider.Value - 1);

    private void HistLatest_Click(object sender, RoutedEventArgs e) => MoveTimeline((int)TimelineSlider.Maximum);

    private void HistForward_Click(object sender, RoutedEventArgs e)
    {
        if (!CanTimeTravel) return;
        if (_viewingHistory)
        {
            MoveTimeline((int)TimelineSlider.Value + 1);
        }
        else if (_running && Tracer.Paused)
        {
            Tracer.Step(); // 最新にいるなら、実際に1歩進める
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F7)
        {
            HistBack_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F8)
        {
            HistForward_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>記録の index 番目のステップの画面を組み立てる。</summary>
    private void ShowHistory(int index)
    {
        if (_history is not { } history) return;
        int count = history.Count;
        if (count == 0) return;
        index = Math.Clamp(index, 0, count - 1);
        _viewingHistory = index < count - 1;

        // 1. その時点で実行中だった文（index 以前で最後の「文」のステップ）を探す
        int s = index;
        int limit = Math.Max(0, index - ReplaySearchLimit);
        while (s > limit && !IsKind(history.Get(s), HitKind.Statement)) s--;

        // 2. 画面の状態をリセット
        foreach (var node in _values.Keys.ToList()) SetValue(node, null);
        foreach (var item in _trail) item.ClearValue(BackgroundProperty);
        _trail.Clear();
        _graph.ResetTrail();
        _watch.Clear();
        _prevWatch.Clear();

        // 3. 変数ウォッチの「変化した」表示のために、同じ関数の1つ前の文の値を先に入れておく
        var statement = history.Get(s);
        if (_map.TryGetValue(statement.Id, out var sInfo) && sInfo.Kind == HitKind.Statement)
        {
            for (int p = s - 1; p >= Math.Max(0, s - ReplaySearchLimit); p--)
            {
                var prev = history.Get(p);
                if (_map.TryGetValue(prev.Id, out var pInfo) && pInfo.Kind == HitKind.Statement && pInfo.Scope == sInfo.Scope)
                {
                    UpdateWatch(pInfo, prev.Vars);
                    break;
                }
            }
        }

        // 4. その文から index までを再生する（光の尾のために少し前からにする）
        int start = Math.Max(0, Math.Min(s, index - 4));
        for (int j = start; j <= index; j++)
            Highlight(history.Get(j), live: false);

        // 1歩ずつ戻る・進むときも、そのステップの音を鳴らす
        var shown = history.Get(index);
        if (_map.TryGetValue(shown.Id, out var shownInfo)) PlaySound(shownInfo, shown);

        UpdateTimeline();
    }

    private bool IsKind(HitEvent ev, HitKind kind) =>
        _map.TryGetValue(ev.Id, out var info) && info.Kind == kind;
}
