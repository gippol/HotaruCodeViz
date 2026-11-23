using System.Diagnostics;

namespace HotaruCodeViz;

/// <summary>停止ボタンで実行を打ち切るための例外。</summary>
public sealed class ExecutionStoppedException : Exception
{
}

/// <summary>計算量の測定で、ステップ数が上限を超えたときに投げる。</summary>
public sealed class StepLimitExceededException : Exception
{
}

/// <summary>
/// 実行時の通知（巻き戻し用の記録にも使う）。
/// Value は条件式や式の値、Vars は変数ウォッチの値（どちらも文字列化済み）、Step は通し番号。
/// </summary>
/// Shots は配列タブ用の配列のコピー（Vars と同じ並び、絵にできないものは null）。Memory はメモリタブ用のスタックとヒープのコピー。
public sealed record HitEvent(int Id, string? Value, string[]? Vars, long Step, int Depth = 0, ArrayShot?[]? Shots = null,
    MemoryShot? Memory = null);

/// <summary>巻き戻し用に、直近のステップを決まった数だけ記録しておくリングバッファ。</summary>
public sealed class ExecutionHistory(int capacity)
{
    private readonly int[] _ids = new int[capacity];
    private readonly string?[] _values = new string?[capacity];
    private readonly string[]?[] _vars = new string[]?[capacity];
    private readonly long[] _steps = new long[capacity];
    private readonly ArrayShot?[]?[] _shots = new ArrayShot?[]?[capacity];
    private readonly MemoryShot?[] _memory = new MemoryShot?[capacity];
    private readonly object _lock = new();
    private long _total;

    public int Capacity => capacity;

    public void Add(int id, string? value, string[]? vars, long step, ArrayShot?[]? shots = null, MemoryShot? memory = null)
    {
        lock (_lock)
        {
            int i = (int)(_total % capacity);
            _ids[i] = id;
            _values[i] = value;
            _vars[i] = vars;
            _steps[i] = step;
            _shots[i] = shots;
            _memory[i] = memory;
            _total++;
        }
    }

    /// <summary>残っている記録の数。</summary>
    public int Count
    {
        get { lock (_lock) return (int)Math.Min(_total, capacity); }
    }

    /// <summary>容量を超えて捨てた古い記録の数。</summary>
    public long Dropped
    {
        get { lock (_lock) return Math.Max(0, _total - capacity); }
    }

    /// <summary>index = 0 が残っている中でいちばん古い記録。</summary>
    public HitEvent Get(int index)
    {
        lock (_lock)
        {
            long abs = Math.Max(0, _total - capacity) + index;
            int i = (int)(abs % capacity);
            return new HitEvent(_ids[i], _values[i], _vars[i], _steps[i], Shots: _shots[i], Memory: _memory[i]);
        }
    }
}

/// <summary>
/// 計装されたユーザーコードから呼ばれるフック。
///   Tracer.Hit(id)              … ステートメントの直前
///   Tracer.Hit(id, new[]{...})  … ステートメントの直前 + 変数ウォッチ
///   Tracer.Cond(id, 条件)       … if / while / for / do の条件
///   Tracer.Expr(id, 式)         … 式の値
///
/// 実行モード
///   通常     … 1歩ごとに UI に通知して DelayMs 待つ
///   間引き   … DelayMs が小さいときは、UI への通知を 1 秒に 30 回までにする
///   早送り   … UI に通知せず全速で走り、ブレークポイントで止まる
/// </summary>
public static class Tracer
{
    /// <summary>待ち時間がこれ未満なら、画面の更新を間引く。</summary>
    public const int ThrottleBelowMs = 30;

    public static Action<HitEvent>? OnHit;

    public static volatile int DelayMs = 400;

    /// <summary>計装ポイント ID ごとの「ここで止まる」フラグ。実行中に差し替えてよい。</summary>
    public static volatile bool[]? Breakpoints;

    /// <summary>
    /// 計装ポイント ID ごとの「条件付き」フラグ。true の ID は、直前の BreakIf で条件が true になったときだけ止まる。
    /// Breakpoints と同じ長さで、一緒に差し替える。
    /// </summary>
    public static volatile bool[]? ConditionalBreakpoints;

    /// <summary>条件付きブレークポイントの条件で例外が出たとき（ID・メッセージ）。ID ごとに 1 回だけ呼ぶ。</summary>
    public static Action<int, string>? OnConditionError;

    private static int _conditionMetId = -1;
    private static readonly HashSet<int> _reportedConditionErrors = new();

    /// <summary>計装ポイント ID ごとの実行回数（ヒートマップ用）。null なら数えない。</summary>
    public static volatile long[]? Counts;

    /// <summary>これを超えたら StepLimitExceededException で打ち切る。</summary>
    public static long MaxSteps = long.MaxValue;

    /// <summary>巻き戻し用の記録。null なら記録しない。</summary>
    public static volatile ExecutionHistory? History;

    /// <summary>呼び出しの木の記録。null なら記録しない（Enter / Exit は何もしない）。</summary>
    public static volatile CallLog? Calls;

    /// <summary>Enter が「何もしなかった」ときに返す印（値の文字列化中など）。</summary>
    public const int NoCall = int.MinValue;

    /// <summary>true なら、UI に知らせるステップで配列のコピー（HitEvent.Shots）を取る（配列タブで使う）。</summary>
    public static volatile bool CaptureArrays = true;

    /// <summary>true なら、UI に知らせるステップで呼び出しの深さ（HitEvent.Depth）を測る（音で使う）。</summary>
    public static volatile bool MeasureDepth;

    private static int _baseDepth = int.MaxValue;

    /// <summary>true なら、UI に知らせる文のステップでスタックとヒープのコピー（HitEvent.Memory）を取る（メモリタブで使う）。</summary>
    public static volatile bool CaptureMemory;

    // ---- メモリタブ用のスタックフレーム ----
    // Enter で積んで Exit で降ろす。各フレームには、そのフレームで最後に記録した変数の値（Hit の vars）を覚えておく。
    // 外側のフレームは内側を呼んでいる間止まっているので、最後の値がそのまま「今の値」になる。
    private sealed class Frame
    {
        public int MethodId = -1;
        public int Call = NoCall;
        public int HitId = -1;
        public object?[]? Vars;
    }

    private static List<Frame> _frames = NewFrames();
    private static int _frameThread = -1; // フレームを記録するスレッド（ユーザーがスレッドを作っても、最初のスレッドだけを追う）

    // いちばん下は、トップレベルのステートメントなど Enter のないコードのためのフレーム
    private static List<Frame> NewFrames() => [new Frame()];

    private static volatile bool _paused;
    private static volatile bool _stop;
    private static volatile bool _fast;
    private static int _stepTokens;
    private static long _steps;
    private static long _lastNotify;

    // 値の文字列化中にユーザーの ToString() などが呼ばれても通知しないためのフラグ
    [ThreadStatic] private static bool _busy;

    public static bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public static bool FastForward
    {
        get => _fast;
        set => _fast = value;
    }

    /// <summary>実行したステップ数（早送り・間引きで画面に出なかった分も含む）。</summary>
    public static long StepCount => Interlocked.Read(ref _steps);

    public static void Reset()
    {
        _paused = false;
        _stop = false;
        _fast = false;
        _stepTokens = 0;
        _steps = 0;
        _lastNotify = 0;
        Breakpoints = null;
        ConditionalBreakpoints = null;
        _conditionMetId = -1;
        lock (_reportedConditionErrors) _reportedConditionErrors.Clear();
        Counts = null;
        MaxSteps = long.MaxValue;
        History = null;
        Calls = null;
        _baseDepth = int.MaxValue;
        CaptureMemory = false;
        _frames = NewFrames();
        _frameThread = -1;
        MemorySnapshot.Reset();
    }

    public static void RequestStop() => _stop = true;

    public static void Step() => Interlocked.Exchange(ref _stepTokens, 1);

    // ======================= ユーザーコードから呼ばれる =======================

    public static void Hit(int id)
    {
        if (_busy) return;
        // 変数ウォッチのない文（コンパイルの都合で外したものも含む）では、フレームの変数は前のままにしておく
        bool notify = Begin(id, out long step);
        var memory = notify && CaptureMemory ? SnapshotMemory() : null;
        History?.Add(id, null, null, step, memory: memory);
        if (notify) Notify(new HitEvent(id, null, null, step, Memory: memory), 1.0);
    }

    public static void Hit(int id, object?[] vars)
    {
        if (_busy) return;
        SetFrameVars(id, vars);
        bool notify = Begin(id, out long step);
        var history = History;
        if (!notify && history is null) return; // 誰も見ないなら文字列化しない
        var texts = FormatAll(vars);
        // 配列・メモリのコピーは画面に出すステップでだけ取る（早送り中の記録には付けない。メモリを食いすぎないように）
        var shots = notify && CaptureArrays ? SnapshotAll(vars) : null;
        var memory = notify && CaptureMemory ? SnapshotMemory() : null;
        history?.Add(id, null, texts, step, shots, memory);
        if (notify) Notify(new HitEvent(id, null, texts, step, Shots: shots, Memory: memory), 1.0);
    }

    private static bool OwnsFrames()
    {
        int me = Environment.CurrentManagedThreadId;
        if (_frameThread == me) return true;
        if (_frameThread != -1) return false;
        _frameThread = me;
        return true;
    }

    private static void SetFrameVars(int id, object?[] vars)
    {
        if (!CaptureMemory || !OwnsFrames()) return;
        var top = _frames[^1];
        top.HitId = id;
        top.Vars = vars;
    }

    private static MemoryShot? SnapshotMemory()
    {
        if (!OwnsFrames()) return null;
        _busy = true;
        try
        {
            var frames = new List<(int, int, object?[]?)>(_frames.Count);
            foreach (var f in _frames)
                if (f.Vars is not null || f.MethodId >= 0) frames.Add((f.MethodId, f.HitId, f.Vars));
            return MemorySnapshot.Take(frames);
        }
        catch
        {
            return null; // 表示のための処理で、ユーザーのプログラムを止めない
        }
        finally
        {
            _busy = false;
        }
    }

    private static ArrayShot?[]? SnapshotAll(object?[] values)
    {
        _busy = true;
        try { return ArraySnapshot.TakeAll(values); }
        finally { _busy = false; }
    }

    public static bool Cond(int id, bool value)
    {
        if (_busy) return value;
        bool notify = Begin(id, out long step);
        var text = value ? "true" : "false";
        History?.Add(id, text, null, step);
        if (notify) Notify(new HitEvent(id, text, null, step), 1.0);
        return value;
    }

    // ---- 呼び出しの木（メソッドの入口・出口）。ステップには数えない ----

    /// <summary>メソッドの入口。戻り値（呼び出しの番号）を Return / Fail / Exit に渡す。</summary>
    public static int Enter(int methodId, object?[]? args)
    {
        var calls = Calls;
        if (calls is null || _busy) return NoCall;
        // 記録しきれないほど呼ばれたら、引数の文字列化も省く
        var texts = args is null || calls.Full ? null : FormatAll(args);
        // 「同じ引数で何回目か」は、引数が数・文字列などの単純な値のときだけ数える（オブジェクトは表示が同じでも別物のことがある）
        bool simple = args is null || args.All(a => a is null or string or decimal or Enum || a.GetType().IsPrimitive);
        int call = calls.Push(methodId, texts, Interlocked.Read(ref _steps), simple);
        if (CaptureMemory && OwnsFrames()) _frames.Add(new Frame { MethodId = methodId, Call = call, Vars = args ?? [] });
        return call;
    }

    /// <summary>return の値を記録して、そのまま返す。</summary>
    public static T Return<T>(int call, T value)
    {
        if (call >= 0 && Calls is { } calls) calls.SetResult(call, FormatOne(value));
        return value;
    }

    /// <summary>例外で抜けるときに呼ばれる（例外フィルター）。いつも false を返すので、例外は捕まえない。</summary>
    public static bool Fail(int call)
    {
        if (call >= 0) Calls?.MarkThrew(call);
        return false;
    }

    /// <summary>メソッドの出口（finally から必ず呼ばれる）。</summary>
    public static void Exit(int call)
    {
        if (call == NoCall) return;
        Calls?.Pop(call, Interlocked.Read(ref _steps));
        if (CaptureMemory && OwnsFrames())
        {
            // ふつうはいちばん上。記録しなかった呼び出し（Hidden）は同じ番号が並ぶが、上から探せば正しく降ろせる
            for (int i = _frames.Count - 1; i >= 1; i--)
            {
                if (_frames[i].Call != call) continue;
                _frames.RemoveRange(i, _frames.Count - i);
                break;
            }
        }
    }

    /// <summary>ループなどの条件 + 条件付きブレークポイント（条件式の値を出したあとに判定する）。</summary>
    public static bool Cond(int id, bool value, Func<bool> breakCondition)
    {
        BreakIf(id, breakCondition);
        return Cond(id, value);
    }

    /// <summary>
    /// 条件付きブレークポイントの判定。直後の Hit / Cond（同じ ID）で止まるかどうかを決める。
    /// そのブレークポイントが今オンのときだけ条件を評価する。
    /// </summary>
    public static void BreakIf(int id, Func<bool> condition)
    {
        if (_busy) return;
        var bp = Breakpoints;
        var conditional = ConditionalBreakpoints;
        if (bp is null || conditional is null || (uint)id >= (uint)bp.Length || (uint)id >= (uint)conditional.Length) return;
        if (!bp[id] || !conditional[id]) return;

        bool met;
        _busy = true; // 条件の中でユーザーのメソッドを呼んでも、ステップとして数えない
        try
        {
            met = condition();
        }
        catch (Exception ex)
        {
            met = false;
            bool first;
            lock (_reportedConditionErrors) first = _reportedConditionErrors.Add(id);
            if (first) OnConditionError?.Invoke(id, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
        if (met) _conditionMetId = id;
    }

    public static T Expr<T>(int id, T value)
    {
        if (_busy) return value;
        bool notify = Begin(id, out long step);
        var history = History;
        if (!notify && history is null) return value;
        var text = FormatOne(value);
        history?.Add(id, text, null, step);
        if (notify) Notify(new HitEvent(id, text, null, step), 0.5);
        return value;
    }

    // ======================= 内部 =======================

    /// <summary>ステップを数えて、UI に通知すべきかを決める。</summary>
    private static bool Begin(int id, out long step)
    {
        if (_stop) throw new ExecutionStoppedException();
        step = Interlocked.Increment(ref _steps);
        if (step > MaxSteps) throw new StepLimitExceededException();

        var counts = Counts;
        if (counts is not null && (uint)id < (uint)counts.Length) counts[id]++;

        int conditionMet = _conditionMetId;
        _conditionMetId = -1;

        var bp = Breakpoints;
        if (bp is not null && (uint)id < (uint)bp.Length && bp[id])
        {
            var conditional = ConditionalBreakpoints;
            bool needsCondition = conditional is not null && (uint)id < (uint)conditional.Length && conditional[id];
            if (!needsCondition || conditionMet == id)
            {
                _fast = false;
                _paused = true;
                return true;
            }
        }
        if (_paused) return true;
        if (_fast) return false;

        if (DelayMs < ThrottleBelowMs)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastNotify < Stopwatch.Frequency / 30) return false;
            _lastNotify = now;
        }
        return true;
    }

    private static void Notify(HitEvent ev, double delayFactor)
    {
        if (MeasureDepth && Calls is { } calls)
        {
            // 呼び出しの木を記録していれば、その深さを使う（Main = 0）
            ev = ev with { Depth = Math.Max(0, calls.Depth - 1) };
        }
        else if (MeasureDepth)
        {
            // スタックの段数から、いちばん浅かったときとの差を「呼び出しの深さ」とする
            int frames = new StackTrace(false).FrameCount;
            if (frames < _baseDepth) _baseDepth = frames;
            ev = ev with { Depth = frames - _baseDepth };
        }
        OnHit?.Invoke(ev);

        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (_stop) throw new ExecutionStoppedException();
            if (_paused)
            {
                if (Interlocked.Exchange(ref _stepTokens, 0) == 1) break;
                Thread.Sleep(10);
                continue;
            }
            if (_fast || sw.ElapsedMilliseconds >= DelayMs * delayFactor) break;
            Thread.Sleep(1);
        }
    }

    private static string FormatOne(object? value)
    {
        _busy = true;
        try { return ValueFormatter.Format(value); }
        finally { _busy = false; }
    }

    private static string[] FormatAll(object?[] values)
    {
        _busy = true;
        try { return values.Select(ValueFormatter.Format).ToArray(); }
        finally { _busy = false; }
    }
}
