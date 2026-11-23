using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HotaruCodeViz;

/// <summary>
/// 巻き戻しの続き：「この変数が最後に変わったステップへ」ジャンプ。
/// 変数ウォッチの行をダブルクリック（または右クリック → メニュー）すると、
/// 今表示しているステップから過去へさかのぼって、その変数の値を変えた文へ移動する。
/// 続けて押すと、さらに 1 つ前の変化へさかのぼる。
/// </summary>
public partial class MainWindow
{
    /// <summary>ローカル変数・引数で、呼び出しの木がないときの「フレーム不明」。</summary>
    private const int UnknownFrame = -2;

    private void WatchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(WatchList, d) is ListViewItem { Content: WatchRow row })
        {
            JumpToLastChange(row.Name);
            e.Handled = true;
        }
    }

    private void WatchLastChange_Click(object sender, RoutedEventArgs e)
    {
        if (WatchList.SelectedItem is WatchRow row) JumpToLastChange(row.Name);
    }

    private void WatchMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { Items: [MenuItem item, ..] }) return;
        bool has = WatchList.SelectedItem is WatchRow;
        item.IsEnabled = has && CanTimeTravel;
        item.Header = has ? $"「{((WatchRow)WatchList.SelectedItem!).Name}」が最後に変わったところへ戻る" : "この変数が最後に変わったところへ戻る";
    }

    /// <summary>今表示しているステップから過去へさかのぼって、変数 name の値を変えた文へ移動する。</summary>
    private void JumpToLastChange(string name)
    {
        if (_history is not { Count: > 0 } history)
        {
            StatusText.Text = RecordCheck?.IsChecked == true
                ? "まだ記録がありません（実行すると使えます）"
                : "巻き戻しの記録がオフなので使えません（「記録」をオンにして実行してください）";
            return;
        }
        if (!CanTimeTravel)
        {
            StatusText.Text = "変数が変わったところへ戻るのは、一時停止中か実行が終わったあとに使えます";
            return;
        }

        int count = history.Count;
        int index = _viewingHistory ? Math.Clamp((int)TimelineSlider.Value, 0, count - 1) : count - 1;

        // 今表示している文（ShowHistory と同じく、index 以前で最後の「文」）
        int s = index;
        while (s > 0 && !IsKind(history.Get(s), HitKind.Statement)) s--;
        var current = history.Get(s);
        if (!_map.TryGetValue(current.Id, out var info) || info.Kind != HitKind.Statement) return;
        int vi = Array.IndexOf(info.VarNames, name);
        if (vi < 0 || current.Vars is null || vi >= current.Vars.Length) return;

        var kind = info.VarKinds is { } kinds && vi < kinds.Length ? kinds[vi] : VarKind.Local;
        bool isField = kind is VarKind.StaticField or VarKind.InstanceField;
        string type = info.VarTypes[vi];
        string value = current.Vars[vi];
        int frame = isField ? UnknownFrame : FrameAt(current.Step);

        // 同じ変数を見られる文か（ローカル変数・引数は同じ関数の同じ呼び出しの中だけ。再帰の別の呼び出しは別の変数）
        bool SameVariableScope(HitInfo other, HitEvent ev) =>
            isField || (other.Scope == info.Scope && FrameAt(ev.Step) == frame);

        // 過去へさかのぼって、値が違う（またはまだ値がない）文 p を探す。
        // newAt は p より後で、最初に今と同じ値になっていた文。変化は p から newAt の間で起きている。
        int newAt = s;
        int changedAt = -1;
        string? oldValue = null;
        for (int p = s - 1; p >= 0; p--)
        {
            var ev = history.Get(p);
            if (!_map.TryGetValue(ev.Id, out var pInfo) || pInfo.Kind != HitKind.Statement) continue;
            if (!SameVariableScope(pInfo, ev)) continue;

            int j = Array.IndexOf(pInfo.VarNames, name);
            if (j >= 0 && pInfo.VarTypes[j] != type) j = -1; // 同じ名前の別の変数（別のクラスのフィールドなど）
            if (j < 0 || ev.Vars is null || j >= ev.Vars.Length)
            {
                if (isField) continue; // フィールドはこの文で見えていないだけ
                changedAt = p;         // ローカル変数がまだ確定していない ＝ ここで宣言・初期化された
                break;
            }
            if (ev.Vars[j] != value)
            {
                oldValue = ev.Vars[j];
                changedAt = p;
                break;
            }
            newAt = p;
        }

        if (changedAt < 0)
        {
            long dropped = history.Dropped;
            StatusText.Text = dropped > 0
                ? $"🔍 記録が残っている範囲では「{name}」は {Short(value)} のまま変わっていません（古い {dropped:N0} ステップは破棄済み）"
                : $"🔍 記録の最初から「{name}」は {Short(value)} のまま変わっていません";
            return;
        }

        // 値を変えたのは、changedAt から newAt の手前までで最後に実行された「同じ変数を見られる文」
        // （ローカル変数なら changedAt そのもの。フィールドなら別の関数の文のこともある）
        int target = changedAt;
        for (int q = newAt - 1; q > changedAt; q--)
        {
            var ev = history.Get(q);
            if (_map.TryGetValue(ev.Id, out var qInfo) && qInfo.Kind == HitKind.Statement && SameVariableScope(qInfo, ev))
            {
                target = q;
                break;
            }
        }

        // for (…; 条件; i++) のループ変数は、文ではなく「更新部分」で変わる。その直後の条件の判定へ移動する
        string? place = null;
        if (!isField && oldValue is not null)
        {
            for (int q = newAt - 1; q > target; q--)
            {
                var ev = history.Get(q);
                if (_map.TryGetValue(ev.Id, out var qInfo) && qInfo.Kind == HitKind.Condition &&
                    qInfo.Node.Parent is ForStatementSyntax loop && FrameAt(ev.Step) == frame &&
                    loop.Incrementors.SelectMany(x => x.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>()
                        .Any(id => id.Identifier.ValueText == name))
                {
                    target = q;
                    place = $"for の更新（{string.Join(", ", loop.Incrementors)}）";
                    break;
                }
            }
        }

        MoveTimeline(target); // ValueChanged → ShowHistory（ステータスもここで書き換わる）
        if (_map.TryGetValue(history.Get(target).Id, out var tInfo))
        {
            int line = LineOf(tInfo.Node);
            string change = place is not null
                ? $"「{name}」は {place} で {Short(oldValue ?? "?")} → {Short(value)} に変わり、いまその直後の条件の判定です（ウォッチは文の前の値なので、まだ {Short(oldValue ?? "?")} のまま）"
                : oldValue is null
                    ? $"「{name}」がここで宣言・初期化されて {Short(value)} になります"
                    : $"「{name}」がこの文で {Short(oldValue)} → {Short(value)} に変わります";
            StatusText.Text = $"🔍 行 {line + 1}　{change}（F8 で進めると変化後が見られます。もう一度押すとさらに前の変化へ）";
        }
    }

    /// <summary>ステップ step のときに実行中だった呼び出し（呼び出しの木がなければ UnknownFrame）。</summary>
    private int FrameAt(long step)
    {
        if (_calls is not { } log) return UnknownFrame;
        lock (log.Lock) return log.CurrentAt(step, out _);
    }

    private static string Short(string value) => value.Length <= 40 ? value : value[..40] + "…";
}
