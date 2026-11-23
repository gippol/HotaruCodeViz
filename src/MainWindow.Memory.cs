namespace HotaruCodeViz;

// 🧠 メモリタブ：スタック（フレームごとの変数）とヒープ（オブジェクト）と参照の矢印
public partial class MainWindow
{
    private Dictionary<int, CallTarget> _methodTargets = new();
    private bool _memoryEnabled;

    /// <summary>実行の開始時（Tracer.Reset のあと）。watch がオフなら記録しない。</summary>
    private void StartMemory(BuildResult result, bool watch)
    {
        _methodTargets = result.Methods ?? new();
        _memoryEnabled = watch;
        Tracer.CaptureMemory = watch;
        MemoryPanel.Clear(watch ? null : "「変数ウォッチ」がオフなので、メモリの絵は出ません。");
    }

    /// <summary>文のステップを表示するとき（ライブでも巻き戻しでも）。</summary>
    private void UpdateMemory(HitEvent ev)
    {
        if (!_memoryEnabled) return;
        if (ev.Memory is not { } shot)
        {
            MemoryPanel.ShowNoRecord();
            return;
        }

        var frames = new MemFrame[shot.Frames.Length];
        MemSlot[]? statics = null;
        // static フィールドはどのフレームのウォッチにも入っているので、いちばん内側のものを使って1か所にまとめる
        for (int i = shot.Frames.Length - 1; i >= 0; i--)
        {
            int depth = i == 0 ? 0 : i + shot.SkippedFrames;
            frames[i] = DescribeFrame(shot.Frames[i], depth, out var frameStatics);
            if (statics is null && frameStatics.Count > 0) statics = frameStatics.ToArray();
        }
        MemoryPanel.Update(shot, frames, statics ?? []);
    }

    private MemFrame DescribeFrame(FrameShot f, int depth, out List<MemSlot> statics)
    {
        statics = new List<MemSlot>();
        var target = f.MethodId >= 0 ? _methodTargets.GetValueOrDefault(f.MethodId) : null;
        var slots = new List<MemSlot>();

        if (f.HitId >= 0 && _map.TryGetValue(f.HitId, out var info))
        {
            for (int i = 0; i < f.Values.Length && i < info.VarNames.Length; i++)
            {
                var kind = info.VarKinds is { } kinds && i < kinds.Length ? kinds[i] : VarKind.Local;
                var slot = new MemSlot(kind == VarKind.InstanceField ? "this." + info.VarNames[i] : info.VarNames[i], info.VarTypes[i], f.Values[i]);
                if (kind == VarKind.StaticField) statics.Add(slot);
                else slots.Add(slot);
            }
            string title = target?.Name ?? (string.IsNullOrEmpty(info.Scope) ? "?" : info.Scope);
            return new MemFrame(title, depth, slots.ToArray());
        }

        // まだ中の文を実行していない（呼ばれた直後）：引数だけ
        if (target is not null)
            for (int i = 0; i < f.Values.Length && i < target.ArgNames.Length; i++)
                slots.Add(new MemSlot(target.ArgNames[i], "", f.Values[i]));
        return new MemFrame(target?.Name ?? "?", depth, slots.ToArray());
    }

    /// <summary>巻き戻しで離れたステップへ飛ぶとき：比較の基準を、その少し前の文のメモリにしておく。</summary>
    private void PrimeMemory(ExecutionHistory history, int statementIndex)
    {
        if (!_memoryEnabled) return;
        MemoryPanel.ForgetPrevious();
        for (int p = statementIndex - 1; p >= Math.Max(0, statementIndex - ReplaySearchLimit); p--)
        {
            var prev = history.Get(p);
            if (prev.Memory is null || !IsKind(prev, HitKind.Statement)) continue;
            UpdateMemory(prev);
            break;
        }
    }
}
