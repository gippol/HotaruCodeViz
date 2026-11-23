using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace HotaruCodeViz;

/// <summary>
/// 変数やフィールドに入っているもの。
///   Ref ≥ 0 … ヒープのオブジェクト（MemoryShot.Objects の番号）を指す参照
///   Ref = -1 … 値そのもの（int・bool・構造体・文字列など。Text に文字列化した値）
///   Ref = -2 … 参照だけど、オブジェクトが多すぎてたどらなかった（Text に短い説明）
/// </summary>
public readonly record struct MemValue(string? Text, int Ref)
{
    public const int Inline = -1;
    public const int Omitted = -2;

    public bool IsRef => Ref >= 0;
}

public enum HeapKind { Object, Array, List, Dictionary, Collection, Leaf }

public sealed record HeapField(string Name, MemValue Value);

/// <summary>ヒープ上のオブジェクト1つ分のコピー。</summary>
public sealed class HeapObject
{
    /// <summary>実行中ずっと変わらない番号（#1, #2 …）。ステップをまたいで「同じオブジェクトか」を見分ける。</summary>
    public required int Identity { get; init; }
    public required string TypeName { get; init; }
    public required HeapKind Kind { get; init; }
    public required HeapField[] Fields { get; init; }

    /// <summary>入りきらずに省いた要素・フィールドの数。</summary>
    public int Hidden { get; init; }

    /// <summary>「Count = 3」や、中身をたどらないオブジェクト（StringBuilder など）の値。</summary>
    public string? Summary { get; init; }

    /// <summary>変数から何本目の矢印でたどり着いたか（0 = 変数から直接）。絵の列に使う。</summary>
    public required int Level { get; init; }
}

/// <summary>スタックフレーム1つ分。HitId ≥ 0 ならその計装ポイントの変数ウォッチ、-1 なら MethodId の引数。</summary>
public sealed record FrameShot(int MethodId, int HitId, MemValue[] Values);

/// <summary>ある瞬間のスタックとヒープのコピー（作ったあとは変更しない）。</summary>
public sealed class MemoryShot
{
    /// <summary>いちばん外側が先頭。</summary>
    public required FrameShot[] Frames { get; init; }

    /// <summary>深すぎて省いたフレームの数（先頭のフレームのすぐあとを省く）。</summary>
    public required int SkippedFrames { get; init; }

    public required HeapObject[] Objects { get; init; }

    /// <summary>オブジェクトが多すぎて、たどりきれなかった。</summary>
    public required bool Truncated { get; init; }
}

/// <summary>
/// フレームの変数から参照をたどって、ヒープのオブジェクトをコピーする（幅優先）。
/// ユーザーのコードを勝手に動かさないように、たどるのは配列・System のコレクション・ユーザーの型のフィールドだけ。
/// </summary>
public static class MemorySnapshot
{
    public const int MaxObjects = 40;
    public const int MaxElements = 16;
    public const int MaxFields = 16;
    public const int MaxFrames = 10;

    private static ConditionalWeakTable<object, StrongBox<int>> _ids = new();
    private static int _nextId;

    /// <summary>実行の開始時に呼ぶ（#1 から数え直す）。</summary>
    public static void Reset()
    {
        _ids = new ConditionalWeakTable<object, StrongBox<int>>();
        _nextId = 0;
    }

    private static int IdentityOf(object o) =>
        _ids.GetValue(o, _ => new StrongBox<int>(Interlocked.Increment(ref _nextId))).Value;

    /// <param name="frames">いちばん外側が先頭。Vars はそのフレームで最後に記録した変数の値。</param>
    public static MemoryShot Take(IReadOnlyList<(int MethodId, int HitId, object?[]? Vars)> frames)
    {
        // 深い再帰では、いちばん外側と、内側の MaxFrames - 1 個だけ
        var picked = new List<(int MethodId, int HitId, object?[]? Vars)>();
        int skipped = 0;
        if (frames.Count > MaxFrames)
        {
            picked.Add(frames[0]);
            skipped = frames.Count - MaxFrames;
            for (int i = frames.Count - (MaxFrames - 1); i < frames.Count; i++) picked.Add(frames[i]);
        }
        else
        {
            picked.AddRange(frames);
        }

        var walker = new Walker();
        var shots = new FrameShot[picked.Count];
        // 内側のフレーム（今見ているところ）の変数を先にたどる（オブジェクトが多すぎて打ち切るとき、内側を優先したい）
        for (int i = picked.Count - 1; i >= 0; i--)
        {
            var (methodId, hitId, vars) = picked[i];
            var values = vars is null ? [] : vars.Select(v => walker.ValueOf(v, 0)).ToArray();
            shots[i] = new FrameShot(methodId, hitId, values);
        }
        walker.Run();
        return new MemoryShot
        {
            Frames = shots, SkippedFrames = skipped, Objects = walker.Objects.Select(o => o!).ToArray(), Truncated = walker.Truncated,
        };
    }

    private sealed class Walker
    {
        public readonly List<HeapObject?> Objects = new();
        public bool Truncated;
        private readonly Dictionary<object, int> _index = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<(object Obj, int Index, int Level)> _queue = new();

        public MemValue ValueOf(object? v, int level)
        {
            if (v is null) return new MemValue("null", MemValue.Inline);
            if (IsInline(v)) return new MemValue(ValueFormatter.Format(v), MemValue.Inline);
            if (_index.TryGetValue(v, out int i)) return new MemValue(null, i);
            if (Objects.Count >= MaxObjects)
            {
                Truncated = true;
                return new MemValue($"#{IdentityOf(v)} {TypeLabel(v.GetType())}", MemValue.Omitted);
            }
            i = Objects.Count;
            Objects.Add(null);
            _index[v] = i;
            _queue.Enqueue((v, i, level));
            return new MemValue(null, i);
        }

        public void Run()
        {
            while (_queue.TryDequeue(out var item))
            {
                HeapObject built;
                try
                {
                    built = Build(item.Obj, item.Level);
                }
                catch (Exception ex)
                {
                    built = new HeapObject
                    {
                        Identity = IdentityOf(item.Obj), TypeName = TypeLabel(item.Obj.GetType()), Kind = HeapKind.Leaf,
                        Fields = [], Summary = $"<{ex.GetType().Name}>", Level = item.Level,
                    };
                }
                Objects[item.Index] = built;
            }
        }

        private HeapObject Build(object o, int level)
        {
            var type = o.GetType();
            int child = level + 1;
            var fields = new List<HeapField>();
            int hidden = 0;
            string? summary = null;
            HeapKind kind;
            string typeName = TypeLabel(type);

            switch (o)
            {
                case Array { Rank: 1 } a:
                    kind = HeapKind.Array;
                    typeName = ArrayLabel(type, a.Length);
                    for (int i = 0; i < a.Length && i < MaxElements; i++)
                        fields.Add(new HeapField($"[{i}]", ValueOf(a.GetValue(a.GetLowerBound(0) + i), child)));
                    hidden = Math.Max(0, a.Length - MaxElements);
                    break;

                case Array a:
                    kind = HeapKind.Leaf;
                    typeName = ArrayLabel(type, a.Length, a);
                    summary = ValueFormatter.Format(a);
                    break;

                case IDictionary dict when IsSystem(type):
                    kind = HeapKind.Dictionary;
                    summary = $"Count = {dict.Count}";
                    foreach (DictionaryEntry e in dict)
                    {
                        if (fields.Count >= MaxElements) { hidden++; continue; }
                        fields.Add(new HeapField(ValueFormatter.Format(e.Key), ValueOf(e.Value, child)));
                    }
                    break;

                case IList list when IsSystem(type):
                    kind = HeapKind.List;
                    summary = $"Count = {list.Count}";
                    for (int i = 0; i < list.Count && i < MaxElements; i++)
                        fields.Add(new HeapField($"[{i}]", ValueOf(list[i], child)));
                    hidden = Math.Max(0, list.Count - MaxElements);
                    break;

                case IEnumerable seq when IsSystem(type) && IsMaterialized(type):
                    // HashSet・Queue（先頭から）・Stack（上から）・LinkedList など
                    kind = HeapKind.Collection;
                    int n = 0;
                    foreach (var item in seq)
                    {
                        if (n < MaxElements) fields.Add(new HeapField($"[{n}]", ValueOf(item, child)));
                        n++;
                    }
                    hidden = Math.Max(0, n - MaxElements);
                    summary = $"Count = {n}";
                    break;

                default:
                    if (IsSystem(type))
                    {
                        // StringBuilder や Random など：中の仕組み（内部のフィールド）は見せない
                        kind = HeapKind.Leaf;
                        summary = ValueFormatter.Format(o);
                        break;
                    }
                    kind = HeapKind.Object;
                    foreach (var f in InstanceFields(type))
                    {
                        if (fields.Count >= MaxFields) { hidden++; continue; }
                        object? value;
                        try { value = f.GetValue(o); }
                        catch { continue; }
                        fields.Add(new HeapField(FieldLabel(f.Name), ValueOf(value, child)));
                    }
                    break;
            }

            return new HeapObject
            {
                Identity = IdentityOf(o), TypeName = typeName, Kind = kind, Fields = fields.ToArray(),
                Hidden = hidden, Summary = summary, Level = level,
            };
        }
    }

    /// <summary>
    /// 変数の中に直接入るもの（値型）と、文字列。
    /// 文字列は本当はヒープにあるが、変更できない（不変）ので値のように見せて、絵をすっきりさせる。
    /// </summary>
    private static bool IsInline(object v) =>
        v is string or Delegate or Type or MemberInfo || v.GetType().IsValueType;

    private static bool IsSystem(Type t) => t.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal));

    private static bool IsMaterialized(Type t) =>
        typeof(ICollection).IsAssignableFrom(t) || t.GetInterfaces().Any(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(ICollection<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));

    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
        // 基底クラスのフィールドを先に
        var chain = new List<Type>();
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType) chain.Add(t);
        chain.Reverse();
        foreach (var t in chain)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }

    /// <summary>自動プロパティの裏のフィールド &lt;Age&gt;k__BackingField → Age。</summary>
    private static string FieldLabel(string name)
    {
        if (name.StartsWith('<'))
        {
            int end = name.IndexOf('>');
            if (end > 1) return name[1..end];
            if (name.StartsWith("<>4__this", StringComparison.Ordinal)) return "this";
        }
        return name;
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(int)] = "int", [typeof(long)] = "long", [typeof(short)] = "short", [typeof(byte)] = "byte",
        [typeof(uint)] = "uint", [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte",
        [typeof(double)] = "double", [typeof(float)] = "float", [typeof(decimal)] = "decimal", [typeof(bool)] = "bool",
        [typeof(char)] = "char", [typeof(string)] = "string", [typeof(object)] = "object",
    };

    /// <summary>C# での書き方に近い型の名前（List&lt;int&gt;、int[] など）。</summary>
    public static string TypeLabel(Type t)
    {
        if (Keywords.TryGetValue(t, out var k)) return k;
        if (t.IsArray) return TypeLabel(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(t) is { } u) return TypeLabel(u) + "?";
        var name = t.Name;
        if (t.IsGenericType)
        {
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeLabel)) + ">";
        }
        return name;
    }

    /// <summary>int[5]・int[3][]・int[2,3] のように、大きさを入れた配列の名前。</summary>
    private static string ArrayLabel(Type t, int length, Array? multi = null)
    {
        var element = t.GetElementType()!;
        // ジャグ配列は「いちばん内側の要素型」[長さ] + 残りの []
        var suffix = new StringBuilder();
        while (element.IsArray)
        {
            suffix.Append('[').Append(new string(',', element.GetArrayRank() - 1)).Append(']');
            element = element.GetElementType()!;
        }
        string size = multi is null
            ? length.ToString()
            : string.Join(",", Enumerable.Range(0, multi.Rank).Select(multi.GetLength));
        return $"{TypeLabel(element)}[{size}]{suffix}";
    }
}
