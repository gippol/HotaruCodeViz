using System.Collections;
using System.Runtime.CompilerServices;

namespace HotaruCodeViz;

public enum CellKind { Number, Bool, Char }

/// <summary>
/// 配列・List・文字列の中身を、絵にするためにコピーしたもの（作ったあとは変更しない）。
/// 1次元なら Rows = 1。2次元はジャグ配列などで行の長さが違うと、足りないところは NaN。
/// </summary>
public sealed class ArrayShot
{
    public required bool Is2D { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }

    /// <summary>コピーしきれなかった分も含めた本当の大きさ。</summary>
    public required int TotalRows { get; init; }
    public required int TotalCols { get; init; }

    public required CellKind Kind { get; init; }

    /// <summary>Rows × Cols。数・bool（1/0）・文字（文字コード）。ないマスは NaN。</summary>
    public required double[] Values { get; init; }

    public bool Truncated => Rows < TotalRows || Cols < TotalCols;

    public double this[int r, int c] => Values[r * Cols + c];

    public bool SameContent(ArrayShot o) =>
        Is2D == o.Is2D && Rows == o.Rows && Cols == o.Cols && Kind == o.Kind &&
        TotalRows == o.TotalRows && TotalCols == o.TotalCols && Values.AsSpan().SequenceEqual(o.Values);
}

/// <summary>ウォッチしている値から、絵にできるもの（配列・List・文字列）のコピーを作る。</summary>
public static class ArraySnapshot
{
    public const int Max1D = 300;
    public const int MaxRows = 40;
    public const int MaxCols = 40;

    // 中身が変わっていなければ前のコピーを使い回す（巻き戻しの記録が増えないように）
    private static readonly ConditionalWeakTable<object, ArrayShot> Last = new();

    public static ArrayShot?[]? TakeAll(object?[] values)
    {
        ArrayShot?[]? shots = null;
        for (int i = 0; i < values.Length; i++)
        {
            ArrayShot? shot;
            try { shot = Take(values[i]); }
            catch { shot = null; } // 変わったコレクションでも実行は止めない
            if (shot is null) continue;
            shots ??= new ArrayShot?[values.Length];
            shots[i] = shot;
        }
        return shots;
    }

    public static ArrayShot? Take(object? value)
    {
        if (value is null) return null;
        var shot = value switch
        {
            string s => FromString(s),
            Array { Rank: 2 } a => From2D(a),
            Array { Rank: 1 } a => FromList(a, ElementTypeOf(a.GetType())),
            IList list => FromList(list, ElementTypeOf(list.GetType())),
            _ => null,
        };
        if (shot is null) return null;

        if (Last.TryGetValue(value, out var previous) && previous.SameContent(shot)) return previous;
        Last.AddOrUpdate(value, shot);
        return shot;
    }

    private static Type? ElementTypeOf(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        foreach (var it in t.GetInterfaces())
            if (it.IsGenericType && it.GetGenericTypeDefinition() == typeof(IList<>))
                return it.GetGenericArguments()[0];
        return null;
    }

    private static CellKind? KindOf(Type? t)
    {
        if (t is null) return null;
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(bool)) return CellKind.Bool;
        if (t == typeof(char)) return CellKind.Char;
        if (t.IsPrimitive || t == typeof(decimal)) return CellKind.Number; // int, long, double, byte …
        return null;
    }

    private static double ToValue(object? v) => v switch
    {
        null => double.NaN,
        bool b => b ? 1 : 0,
        char c => c,
        IConvertible conv => conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
        _ => double.NaN,
    };

    private static ArrayShot? FromString(string s)
    {
        if (s.Length == 0) return null;
        int n = Math.Min(s.Length, Max1D);
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = s[i];
        return new ArrayShot
        {
            Is2D = false, Rows = 1, Cols = n, TotalRows = 1, TotalCols = s.Length,
            Kind = CellKind.Char, Values = values,
        };
    }

    private static ArrayShot? From2D(Array a)
    {
        var kind = KindOf(a.GetType().GetElementType());
        if (kind is null) return null;
        int rows = a.GetLength(0), cols = a.GetLength(1);
        int r = Math.Min(rows, MaxRows), c = Math.Min(cols, MaxCols);
        if (r == 0 || c == 0) return null;
        int lo0 = a.GetLowerBound(0), lo1 = a.GetLowerBound(1);
        var values = new double[r * c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                values[i * c + j] = ToValue(a.GetValue(lo0 + i, lo1 + j));
        return new ArrayShot
        {
            Is2D = true, Rows = r, Cols = c, TotalRows = rows, TotalCols = cols, Kind = kind.Value, Values = values,
        };
    }

    /// <summary>1次元（int[]・List&lt;int&gt; など）か、行の並び（int[][]・List&lt;List&lt;int&gt;&gt;・string[]）。</summary>
    private static ArrayShot? FromList(IList list, Type? elementType)
    {
        if (list.Count == 0) return null;

        // 行の並び → 2次元
        if (elementType == typeof(string) || (elementType is not null && ElementTypeOf(elementType) is { } inner && KindOf(inner) is not null))
            return FromRows(list, elementType);

        var kind = KindOf(elementType);
        if (kind is null) return null;
        int n = Math.Min(list.Count, Max1D);
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = ToValue(list[i]);
        return new ArrayShot
        {
            Is2D = false, Rows = 1, Cols = n, TotalRows = 1, TotalCols = list.Count, Kind = kind.Value, Values = values,
        };
    }

    private static ArrayShot? FromRows(IList rows, Type elementType)
    {
        var kind = elementType == typeof(string) ? CellKind.Char : KindOf(ElementTypeOf(elementType));
        if (kind is null) return null;

        int r = Math.Min(rows.Count, MaxRows);
        int totalCols = 0;
        for (int i = 0; i < rows.Count; i++)
            totalCols = Math.Max(totalCols, rows[i] switch { string s => s.Length, IList l => l.Count, _ => 0 });
        int c = Math.Min(totalCols, MaxCols);
        if (c == 0) return null;

        var values = new double[r * c];
        Array.Fill(values, double.NaN);
        for (int i = 0; i < r; i++)
        {
            switch (rows[i])
            {
                case string s:
                    for (int j = 0; j < Math.Min(s.Length, c); j++) values[i * c + j] = s[j];
                    break;
                case IList l:
                    for (int j = 0; j < Math.Min(l.Count, c); j++) values[i * c + j] = ToValue(l[j]);
                    break;
            }
        }
        return new ArrayShot
        {
            Is2D = true, Rows = r, Cols = c, TotalRows = rows.Count, TotalCols = totalCols, Kind = kind.Value, Values = values,
        };
    }
}
