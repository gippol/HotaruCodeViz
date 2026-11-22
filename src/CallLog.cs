namespace HotaruCodeViz;

public enum CallState { Running, Returned, Threw }

/// <summary>呼び出し1回分。</summary>
public sealed class CallNode
{
    public required int MethodId { get; init; }
    public required int Parent { get; init; }          // 親の呼び出し（-1 ならいちばん外側）
    public required int Depth { get; init; }           // 0 = いちばん外側
    public required string[]? Args { get; init; }      // 引数の値（文字列化済み）
    public required long EnterStep { get; init; }      // 呼ばれた時点のステップ数（中の最初の文は EnterStep + 1 以降）
    public required int Repeat { get; init; }          // 同じメソッド・同じ引数の呼び出しが何回目か（1 = 初めて）
    public long ExitStep { get; set; } = -1;           // 戻った時点のステップ数（-1 = まだ）
    public string? Result { get; set; }
    public CallState State { get; set; } = CallState.Running;
    public List<int>? Children { get; set; }

    /// <summary>ステップ step を表示しているときに、もう呼ばれているか。</summary>
    public bool VisibleAt(long step) => EnterStep < step;

    /// <summary>ステップ step を表示しているときに、もう戻っているか。</summary>
    public bool FinishedAt(long step) => ExitStep >= 0 && ExitStep < step;
}

/// <summary>
/// 呼び出しの木の記録。ユーザーコードのスレッドで書き、UI スレッドで読む（どちらも lock の中で）。
/// 呼ばれた順に Nodes に足していくので、Nodes は EnterStep の順に並ぶ。
/// </summary>
public sealed class CallLog
{
    /// <summary>これより多い呼び出しは木に記録しない（呼び出し履歴の深さだけは数え続ける）。</summary>
    public const int MaxNodes = 100_000;

    /// <summary>Enter が返す「記録しなかった呼び出し」の印。</summary>
    public const int Hidden = -1;

    public readonly object Lock = new();
    public List<CallNode> Nodes { get; } = new();
    public long Overflow { get; private set; }

    private readonly List<int> _stack = new();                 // 今実行中の呼び出し（Hidden も入る）
    private readonly Dictionary<string, int> _repeats = new();

    /// <summary>今の呼び出しの深さ（記録しなかった呼び出しも含む）。</summary>
    public int Depth
    {
        get { lock (Lock) return _stack.Count; }
    }

    public bool Full
    {
        get { lock (Lock) return Nodes.Count >= MaxNodes; }
    }

    /// <param name="countRepeats">同じ引数の呼び出しを数えるか（引数が単純な値のときだけ）</param>
    public int Push(int methodId, string[]? args, long step, bool countRepeats = true)
    {
        lock (Lock)
        {
            if (Nodes.Count >= MaxNodes)
            {
                Overflow++;
                _stack.Add(Hidden);
                return Hidden;
            }

            // 親は、スタックの中でいちばん近い「記録した呼び出し」
            int parent = -1;
            for (int i = _stack.Count - 1; i >= 0; i--)
                if (_stack[i] >= 0) { parent = _stack[i]; break; }

            int seen = 0;
            if (countRepeats && args is not null)
            {
                string key = methodId + "(" + string.Join("\u001f", args) + ")";
                _repeats.TryGetValue(key, out seen);
                _repeats[key] = seen + 1;
            }

            int index = Nodes.Count;
            Nodes.Add(new CallNode
            {
                MethodId = methodId, Parent = parent, Depth = _stack.Count, Args = args, EnterStep = step, Repeat = seen + 1,
            });
            if (parent >= 0) (Nodes[parent].Children ??= new List<int>()).Add(index);
            _stack.Add(index);
            return index;
        }
    }

    public void SetResult(int call, string result)
    {
        lock (Lock)
        {
            if (call >= 0 && call < Nodes.Count) Nodes[call].Result = result;
        }
    }

    public void MarkThrew(int call)
    {
        lock (Lock)
        {
            if (call >= 0 && call < Nodes.Count && Nodes[call].ExitStep < 0) Nodes[call].State = CallState.Threw;
        }
    }

    public void Pop(int call, long step)
    {
        lock (Lock)
        {
            // ふつうはスタックの一番上。食い違っていたら（念のため）そこまで戻す
            int at = _stack.LastIndexOf(call);
            if (at < 0) return;
            for (int i = _stack.Count - 1; i >= at; i--)
            {
                int c = _stack[i];
                if (c >= 0)
                {
                    var node = Nodes[c];
                    node.ExitStep = step;
                    if (node.State == CallState.Running) node.State = CallState.Returned;
                }
            }
            _stack.RemoveRange(at, _stack.Count - at);
        }
    }

    /// <summary>
    /// ステップ step を表示しているときの「今実行中の呼び出し」（いちばん深いもの）。なければ -1。
    /// lock の中で呼ぶこと。
    /// </summary>
    public int CurrentAt(long step, out int visibleCount)
    {
        // EnterStep < step の呼び出しの数（Nodes は EnterStep の順）
        int lo = 0, hi = Nodes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Nodes[mid].EnterStep < step) lo = mid + 1; else hi = mid;
        }
        visibleCount = lo;

        int c = lo - 1;
        while (c >= 0 && Nodes[c].FinishedAt(step)) c = Nodes[c].Parent;
        return c;
    }
}
