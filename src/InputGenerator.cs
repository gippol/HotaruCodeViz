using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace HotaruCodeViz;

/// <summary>
/// 計算量の測定用に、テンプレートから標準入力を作る。
///
///   {N}               … N の値（{N*2} や {N/2+1} のような計算もできる）
///   {nums C LO HI}    … LO〜HI のランダムな整数を C 個、空白区切りで
///   {seq C}           … 1 2 3 … C
///   {rows C K LO HI}  … LO〜HI のランダムな整数 K 個の行を C 行
///   {chars C SET}     … SET の文字からランダムに C 文字（例: {chars N .#}）
///   {grid H W SET}    … SET の文字からなる H 行 W 列のマス目
///
/// C / K / LO / HI / H / W には N を使った式も書ける。
/// </summary>
public static partial class InputGenerator
{
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();

    public static string Generate(string template, long n, Random rng)
    {
        var text = template.Replace("\r\n", "\n");
        return Placeholder().Replace(text, m => Expand(m.Groups[1].Value.Trim(), n, rng));
    }

    private static string Expand(string body, long n, Random rng)
    {
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "nums" when parts.Length == 4:
                return Join(' ', Eval(parts[1], n), _ => Rand(rng, parts[2], parts[3], n).ToString());
            case "seq" when parts.Length == 2:
                return Join(' ', Eval(parts[1], n), i => (i + 1).ToString());
            case "rows" when parts.Length == 5:
                return Join('\n', Eval(parts[1], n), _ => Join(' ', Eval(parts[2], n), _ => Rand(rng, parts[3], parts[4], n).ToString()));
            case "chars" when parts.Length == 3:
                return RandomChars(rng, parts[2], Eval(parts[1], n));
            case "grid" when parts.Length == 4:
                return Join('\n', Eval(parts[1], n), _ => RandomChars(rng, parts[3], Eval(parts[2], n)));
            default:
                return Eval(body, n).ToString();
        }
    }

    private static long Rand(Random rng, string lo, string hi, long n) => rng.NextInt64(Eval(lo, n), Eval(hi, n) + 1);

    private static string RandomChars(Random rng, string set, long count)
    {
        var sb = new StringBuilder((int)count);
        for (long i = 0; i < count; i++) sb.Append(set[rng.Next(set.Length)]);
        return sb.ToString();
    }

    private static string Join(char separator, long count, Func<long, string> item)
    {
        var sb = new StringBuilder();
        for (long i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(item(i));
        }
        return sb.ToString();
    }

    /// <summary>「N*2」「N/2+1」「100」のような式を計算する。</summary>
    private static long Eval(string expr, long n)
    {
        var replaced = Regex.Replace(expr, @"\bN\b", n.ToString());
        if (long.TryParse(replaced, out var v)) return v;
        var result = new DataTable().Compute(replaced, null);
        return Convert.ToInt64(Math.Floor(Convert.ToDouble(result)));
    }
}
