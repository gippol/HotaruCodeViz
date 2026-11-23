using System.Collections.Concurrent;

namespace HotaruCodeViz;

/// <summary>ヒットカウントの決め方。</summary>
public enum HitCountMode
{
    /// <summary>ちょうど N 回目だけ止まる。</summary>
    Equal,

    /// <summary>N の倍数回目ごとに止まる。</summary>
    Multiple,

    /// <summary>N 回目以降は毎回止まる。</summary>
    AtLeast,
}

/// <summary>ブレークポイントのヒットカウント（「N 回目に止まる」）。Count は 1 以上。</summary>
public sealed record HitCountRule(HitCountMode Mode, long Count)
{
    public bool Matches(long hit) => Mode switch
    {
        HitCountMode.Equal => hit == Count,
        HitCountMode.Multiple => hit % Count == 0,
        _ => hit >= Count,
    };

    public string Describe() => Mode switch
    {
        HitCountMode.Equal => $"{Count:N0} 回目",
        HitCountMode.Multiple => $"{Count:N0} の倍数回目",
        _ => $"{Count:N0} 回目以降",
    };
}

/// <summary>最後に止まったブレークポイント（ステップ・行・その行で何回目か）。</summary>
public sealed record BreakpointStop(long Step, int Line, long Hit);

/// <summary>
/// ブレークポイントの行ごとに「止まるはずだった回数」（条件付きなら条件が true になった回数）を数えて、
/// ヒットカウントのルールに合うときだけ止める。
/// 表（ID → 行、行 → ルール）は UI スレッドで差し替え、ShouldStop は実行スレッドから呼ぶ。
/// ルールはコンパイルが要らないので、実行中に変えてもすぐ効く（それまでの回数はそのまま）。
/// </summary>
public sealed class HitCounter
{
    private sealed record Table(int[] LineOfId, IReadOnlyDictionary<int, HitCountRule> Rules);

    private volatile Table _table = new([], new Dictionary<int, HitCountRule>());
    private readonly ConcurrentDictionary<int, long> _counts = new();
    private volatile BreakpointStop? _lastStop;

    /// <summary>lineOfId は計装ポイント ID → ブレークポイントの行（ブレークポイントでない ID は -1）。</summary>
    public void Update(int[] lineOfId, IReadOnlyDictionary<int, HitCountRule> rules) => _table = new Table(lineOfId, rules);

    /// <summary>ブレークポイントに来たとき（条件付きなら条件が true のとき）に呼ぶ。止まるなら true。</summary>
    public bool ShouldStop(int id, long step)
    {
        var table = _table;
        if ((uint)id >= (uint)table.LineOfId.Length || table.LineOfId[id] is var line && line < 0) return true;

        long hit = _counts.AddOrUpdate(line, 1, (_, c) => c + 1);
        if (table.Rules.TryGetValue(line, out var rule) && !rule.Matches(hit)) return false;
        _lastStop = new BreakpointStop(step, line, hit);
        return true;
    }

    /// <summary>その行のブレークポイントに来た回数（今の実行で）。</summary>
    public long CountOf(int line) => _counts.TryGetValue(line, out var c) ? c : 0;

    public BreakpointStop? LastStop => _lastStop;
}
