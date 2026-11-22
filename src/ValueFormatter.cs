using System.Collections;
using System.Globalization;
using System.Text;

namespace HotaruCodeViz;

/// <summary>実行中の値を、その瞬間のスナップショットとして文字列にする。</summary>
public static class ValueFormatter
{
    private const int MaxItems = 16;
    private const int MaxLength = 120;

    public static string Format(object? value)
    {
        try
        {
            var s = FormatCore(value, 0);
            return s.Length > MaxLength ? s[..MaxLength] + "…" : s;
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}>";
        }
    }

    private static string FormatCore(object? v, int depth)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return Quote(s);
            case char c: return $"'{Escape(c.ToString())}'";
            case bool b: return b ? "true" : "false";
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString(CultureInfo.InvariantCulture);
            case StringBuilder sb: return Quote(sb.ToString());
            case Enum e: return e.ToString();
            case Delegate: return "<関数>";
            case IFormattable fmt when v.GetType().IsPrimitive: return fmt.ToString(null, CultureInfo.InvariantCulture);
        }

        var type = v.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            var key = type.GetProperty("Key")!.GetValue(v);
            var val = type.GetProperty("Value")!.GetValue(v);
            return $"{FormatCore(key, depth + 1)}: {FormatCore(val, depth + 1)}";
        }

        if (v is Array { Rank: 2 } grid)
        {
            var rows = new List<string>();
            for (int i = 0; i < Math.Min(grid.GetLength(0), MaxItems); i++)
            {
                var cols = new List<string>();
                for (int j = 0; j < Math.Min(grid.GetLength(1), MaxItems); j++)
                    cols.Add(FormatCore(grid.GetValue(i, j), depth + 2));
                rows.Add("[" + string.Join(", ", cols) + "]");
            }
            return "[" + string.Join(", ", rows) + "]";
        }

        // 実体のあるコレクションだけ中身を表示（遅延評価の LINQ を勝手に回さない）
        if (v is IEnumerable seq && IsMaterialized(v))
        {
            if (depth >= 3) return "[…]";
            var items = new List<string>();
            int count = 0;
            foreach (var item in seq)
            {
                if (count >= MaxItems) break; // 全部なめると大きい配列で遅くなる
                items.Add(FormatCore(item, depth + 1));
                count++;
            }
            int total = CountOf(v) ?? count;
            var body = string.Join(", ", items) + (total > count ? $", …(全{total}個)" : "");
            return v is IDictionary ? "{" + body + "}" : "[" + body + "]";
        }

        if (v is IEnumerable) return $"<{ShortTypeName(type)}>";

        return v.ToString() ?? "";
    }

    private static int? CountOf(object v)
    {
        if (v is ICollection c) return c.Count;
        var prop = v.GetType().GetProperty("Count");
        return prop?.GetValue(v) is int n ? n : null;
    }

    private static bool IsMaterialized(object v) =>
        v is ICollection || v.GetType().GetInterfaces().Any(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(ICollection<>) ||
             i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));

    private static string ShortTypeName(Type t) => t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] + "<…>" : t.Name;

    private static string Quote(string s) => "\"" + Escape(s) + "\"";

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
