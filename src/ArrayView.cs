using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace HotaruCodeViz;

/// <summary>
/// 配列タブ：変数ウォッチの配列・List・文字列を絵で描く。
///   数の1次元   … 棒グラフ（添字っぽい変数 i, j, lo, hi … の位置に ▲）
///   bool・文字  … マス目の列（入りきらなければ折り返す）
///   2次元       … マス目（数は色の濃さ、bool は緑、文字は # を黒く）
/// 前回から値が変わったところはオレンジで光らせる。
/// </summary>
public sealed class ArrayView : FrameworkElement
{
    private sealed class Item
    {
        public required string Name;
        public required string Type;
        public required ArrayShot Shot;
        /// <summary>マスごとに「何回前の変化で変わったか」（0 = 最新の変化）。null なら比べられない。</summary>
        public int[]? Age;

        /// <summary>最後に値が変わったときの Update の通し番号（しばらく変わらなければ光を消す）。</summary>
        public long ChangedAt = -100;

        /// <summary>0 = 光らせない、1 = 1つ前の変化（薄く）、2 = 最新の変化（濃く）。</summary>
        public int Level(int index, long now) =>
            Age is null || now - ChangedAt > FadeAfter ? 0 : Age[index] switch { 0 => 2, 1 => 1, _ => 0 };
    }

    /// <summary>最後の変化からこれだけ文が進んだら、オレンジの光を消す。</summary>
    private const int FadeAfter = 8;

    private long _updates;

    private sealed record Pointer(string Name, int Index, Brush Brush);

    private readonly List<Item> _items = new();
    private readonly Dictionary<string, Item> _previous = new();   // 変数名 → 前回の表示（変化の検出用）
    private Dictionary<string, long> _ints = new();               // 今の int 変数の値（▲ の位置用）
    private string? _message;

    // ---- 見た目 ----
    private const double Pad = 12, TitleH = 24, Gap = 18;
    private const double AxisW = 34;

    /// <summary>棒グラフの高さ。配列が複数あるときは低くして、1画面に収まりやすくする。</summary>
    private double BarAreaH => _items.Count > 1 ? 105 : 150;
    private static readonly Typeface Face = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush TextBrush = Palette.Freeze(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush SubTextBrush = Palette.Freeze(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush BarBrush = Palette.Freeze(Color.FromRgb(0x90, 0xA4, 0xAE));
    private static readonly Brush ChangedBrush = Palette.Freeze(Color.FromRgb(0xFF, 0xA7, 0x26));
    private static readonly Brush InfBrush = Palette.Freeze(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush TrueBrush = Palette.Freeze(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly Brush FalseBrush = Palette.Freeze(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush WallBrush = Palette.Freeze(Color.FromRgb(0x45, 0x5A, 0x64));
    private static readonly Pen GridPen = new Pen(Palette.Freeze(Color.FromRgb(0xCF, 0xD8, 0xDC)), 1).Frozen();
    private static readonly Pen AxisPen = new Pen(Palette.Freeze(Color.FromRgb(0xB0, 0xB0, 0xB0)), 1).Frozen();
    private static readonly Pen ChangedPen = new Pen(Palette.Freeze(Color.FromRgb(0xEF, 0x6C, 0x00)), 2).Frozen();

    private static readonly Brush[] PointerBrushes =
    {
        Palette.Freeze(Color.FromRgb(0xE5, 0x39, 0x35)), Palette.Freeze(Color.FromRgb(0x1E, 0x88, 0xE5)),
        Palette.Freeze(Color.FromRgb(0x43, 0xA0, 0x47)), Palette.Freeze(Color.FromRgb(0x8E, 0x24, 0xAA)),
        Palette.Freeze(Color.FromRgb(0xF4, 0x51, 0x1E)), Palette.Freeze(Color.FromRgb(0x00, 0x89, 0x7B)),
    };

    /// <summary>1次元で ▲ を付ける「添字っぽい」変数名。</summary>
    private static readonly Regex IndexName = new(
        @"^([ijklmpqr]|[ijk]\d|lo|hi|mid|left|right|low|high|idx|pos|start|end|cur|ptr|ni|nj)$|(Index|Idx|Pos)$",
        RegexOptions.CultureInvariant);

    /// <summary>2次元で「行, 列」として組にする変数名。</summary>
    private static readonly (string Row, string Col)[] CellPairs =
    {
        ("i", "j"), ("y", "x"), ("ny", "nx"), ("r", "c"), ("row", "col"), ("i", "c"), ("i", "w"), ("cy", "cx"), ("ni", "nj"),
    };

    // INF としてよく使われる値は「∞」と表示して、色や棒の高さの計算から外す
    private static readonly HashSet<double> InfValues = new()
    {
        int.MaxValue, int.MinValue, int.MaxValue / 2, 1e9, 1 << 30, 0x3f3f3f3f, 2e9,
        long.MaxValue, long.MinValue, long.MaxValue / 2, 1e18, (double)(1L << 60), (double)(1L << 62),
        double.MaxValue, double.PositiveInfinity, double.NegativeInfinity,
    };

    private static bool IsInf(double v) => InfValues.Contains(v) || double.IsInfinity(v);

    public bool HasItems => _items.Count > 0;

    // ======================= データ =======================

    /// <summary>ステートメントのウォッチ値で描き直す。shots が null なら絵は前のまま（早送り中の記録など）。</summary>
    public void Update(string[] names, string[] types, string[]? texts, ArrayShot?[]? shots)
    {
        _ints = new Dictionary<string, long>();
        if (texts is not null)
            for (int i = 0; i < names.Length && i < texts.Length; i++)
                if (types[i] is "int" or "long" or "short" or "byte" or "uint" or "ulong" or "ushort" or "sbyte"
                    && long.TryParse(texts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    _ints[names[i]] = v;

        if (shots is null)
        {
            _message = _items.Count == 0 ? null : "（この時点の配列の記録はありません — 早送り中に通ったステップです）";
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        _message = null;
        _items.Clear();
        _updates++;
        // 2次元（DP の表や迷路）が主役のことが多いので先に並べる
        var order = Enumerable.Range(0, Math.Min(names.Length, shots.Length))
            .OrderBy(i => shots[i] is { Is2D: true } ? 0 : 1).ThenBy(i => i);
        foreach (int i in order)
        {
            if (shots[i] is not { } shot) continue;
            // 2個以下の数の配列は、入力を読むためだけのもの（int[] nm = ReadLine()…）が多いので描かない
            if (!shot.Is2D && shot.Kind == CellKind.Number && shot.TotalCols <= 2) continue;
            var item = new Item { Name = names[i], Type = types[i], Shot = shot };
            if (_previous.TryGetValue(names[i], out var prev))
            {
                if (ReferenceEquals(prev.Shot, shot))
                {
                    // 変わっていない → 光り方もそのまま（時間がたてば Level で消える）
                    item.Age = prev.Age;
                    item.ChangedAt = prev.ChangedAt;
                }
                else if (SameShape(prev.Shot, shot))
                {
                    item.Age = NextAge(prev, shot);
                    item.ChangedAt = _updates;
                }
            }
            _items.Add(item);
            _previous[names[i]] = item;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Clear(string? message = null)
    {
        _items.Clear();
        _previous.Clear();
        _ints.Clear();
        _message = message;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void ShowMessage(string? message)
    {
        _message = message;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static bool SameShape(ArrayShot a, ArrayShot b) => a.Rows == b.Rows && a.Cols == b.Cols && a.Is2D == b.Is2D;

    /// <summary>
    /// 変化があったら、前の年齢を1つ進めて、今回変わったマスを 0 にする。
    /// （交換は2つの文で行うので、最新と1つ前の変化を両方光らせると「入れ替わった2つ」が見える）
    /// </summary>
    private static int[] NextAge(Item prev, ArrayShot shot)
    {
        var age = new int[shot.Values.Length];
        for (int i = 0; i < age.Length; i++)
        {
            age[i] = prev.Age is null ? 99 : Math.Min(99, prev.Age[i] + 1);
            if (!prev.Shot.Values[i].Equals(shot.Values[i])) age[i] = 0;
        }
        return age;
    }

    private static readonly Brush RecentBrush = Palette.Freeze(Color.FromRgb(0xFF, 0xE0, 0xB2));

    // ======================= レイアウト =======================

    private double ContentWidth => Math.Max(200, (double.IsNaN(Width) || ActualWidth == 0 ? 600 : ActualWidth) - Pad * 2);

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 600 : available.Width;
        return new Size(0, TotalHeight(Math.Max(200, width - Pad * 2)));
    }

    private double TotalHeight(double w)
    {
        double y = Pad + (_message is null ? 0 : 26);
        if (_items.Count == 0) return y + 60;
        foreach (var item in _items) y += TitleH + ItemHeight(item, w) + Gap;
        return y;
    }

    private double ItemHeight(Item item, double w)
    {
        var s = item.Shot;
        if (s.Is2D) return GridLayout(s, w).Cell * s.Rows + GridLayout(s, w).Header;
        if (s.Kind == CellKind.Number) return BarAreaH + 18 + PointersFor(item).GroupBy(p => p.Index).Select(g => g.Count()).DefaultIfEmpty(0).Max() * 15 + 4;
        var (cell, perRow) = StripLayout(s, w);
        return Math.Ceiling(s.Cols / (double)perRow) * (cell + 16);
    }

    private static (double Cell, double Header) GridLayout(ArrayShot s, double w)
    {
        double cell = Math.Clamp((w - 30) / s.Cols, 8, 34);
        double header = cell >= 14 ? 16 : 0;
        return (cell, header);
    }

    private static (double Cell, int PerRow) StripLayout(ArrayShot s, double w)
    {
        double cell = 26;
        int perRow = Math.Max(1, (int)((w - 30) / cell));
        if (s.Cols > perRow * 6) // 行が多くなりすぎるなら小さく
        {
            cell = 18;
            perRow = Math.Max(1, (int)((w - 30) / cell));
        }
        return (cell, Math.Min(perRow, s.Cols));
    }

    private List<Pointer> PointersFor(Item item)
    {
        var list = new List<Pointer>();
        if (item.Shot.Is2D) return list;
        int color = 0;
        foreach (var (name, value) in _ints)
        {
            if (!IndexName.IsMatch(name)) continue;
            var brush = PointerBrushes[color++ % PointerBrushes.Length];
            if (value >= 0 && value < item.Shot.Cols) list.Add(new Pointer(name, (int)value, brush));
        }
        return list;
    }

    // ======================= 描画 =======================

    protected override void OnRender(DrawingContext dc)
    {
        double w = ContentWidth;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double y = Pad;

        if (_message is not null)
        {
            DrawText(dc, _message, Pad, y, 12, SubTextBrush);
            y += 26;
        }
        if (_items.Count == 0)
        {
            DrawText(dc, "配列・List・文字列の変数があると、ここに絵で表示します（「変数ウォッチ」をオンにして実行）。", Pad, y, 13, SubTextBrush);
            DrawText(dc, "📚 図鑑からサンプルを選ぶと、ソートや DP の動きが見られます。", Pad, y + 22, 13, SubTextBrush);
            return;
        }

        foreach (var item in _items)
        {
            var s = item.Shot;
            string size = s.Is2D ? $"{s.TotalRows}×{s.TotalCols}" : $"{s.TotalCols} 個";
            string cut = s.Truncated ? $"（先頭の {(s.Is2D ? $"{s.Rows}×{s.Cols}" : s.Cols.ToString())} だけ表示）" : "";
            var title = DrawText(dc, item.Name, Pad, y + 2, 14, TextBrush, bold: true);
            DrawText(dc, $"{item.Type}　{size}{cut}", Pad + title.Width + 10, y + 5, 11, SubTextBrush);
            y += TitleH;

            double h = ItemHeight(item, w);
            if (s.Is2D) DrawGrid(dc, item, Pad, y, w);
            else if (s.Kind == CellKind.Number) DrawBars(dc, item, Pad, y, w);
            else DrawStrip(dc, item, Pad, y, w);
            y += h + Gap;
        }
    }

    // ---- 棒グラフ ----
    private void DrawBars(DrawingContext dc, Item item, double x0, double top, double w)
    {
        var s = item.Shot;
        int n = s.Cols;
        double plotX = x0 + AxisW;
        double slot = Math.Min(44, (w - AxisW) / n);
        double barW = Math.Max(1, slot * (slot >= 6 ? 0.78 : 1));

        double min = 0, max = 0;
        foreach (var v in s.Values)
        {
            if (double.IsNaN(v) || IsInf(v)) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        if (max == min) max = min + 1;
        double scale = (BarAreaH - 16) / (max - min);
        double baseY = top + 14 + max * scale; // 値 0 の高さ

        // 軸と目盛り
        dc.DrawLine(AxisPen, new Point(plotX - 2, baseY), new Point(plotX + slot * n, baseY));
        DrawText(dc, Format(max), x0, top + 6, 9, SubTextBrush, alignRight: AxisW - 4);
        if (min < 0) DrawText(dc, Format(min), x0, top + BarAreaH - 12, 9, SubTextBrush, alignRight: AxisW - 4);

        var pointers = PointersFor(item);
        var pointed = pointers.Select(p => p.Index).ToHashSet();

        for (int i = 0; i < n; i++)
        {
            double v = s.Values[i];
            double cx = plotX + slot * i + (slot - barW) / 2;
            int level = item.Level(i, _updates);
            bool changed = level == 2;
            if (double.IsNaN(v)) continue;

            Rect rect;
            if (IsInf(v))
            {
                rect = new Rect(cx, top + 14, barW, BarAreaH - 16);
                dc.DrawRectangle(InfBrush, null, rect);
                if (slot >= 12) DrawText(dc, "∞", cx, top, 10, SubTextBrush, center: barW);
            }
            else
            {
                double y1 = baseY - v * scale;
                rect = new Rect(cx, Math.Min(y1, baseY), barW, Math.Max(1, Math.Abs(baseY - y1)));
                var fill = changed ? ChangedBrush
                    : level == 1 ? Palette.Freeze(Color.FromRgb(0xFF, 0xCC, 0x80))
                    : pointed.Contains(i) ? Lighten(pointers.First(p => p.Index == i).Brush)
                    : BarBrush;
                dc.DrawRectangle(fill, null, rect);
                if (slot >= 20 && s.Cols <= 60)
                    DrawText(dc, Format(v), cx, v >= 0 ? rect.Top - 13 : rect.Bottom + 1, 10, TextBrush, center: barW);
            }
            if (changed) dc.DrawRectangle(null, ChangedPen, rect);

            // 添字
            if (slot >= 14 || i % 10 == 0)
                DrawText(dc, i.ToString(), plotX + slot * i, top + BarAreaH + 2, 9, SubTextBrush, center: slot);
        }

        // ▲ と変数名（同じ位置に複数あれば下に積む）
        double py = top + BarAreaH + 16;
        foreach (var group in pointers.GroupBy(p => p.Index))
        {
            int k = 0;
            foreach (var p in group)
            {
                double cx = plotX + slot * p.Index + slot / 2;
                DrawText(dc, "▲" + p.Name, cx - 5, py + k * 15, 11, p.Brush, bold: true);
                k++;
            }
        }
    }

    // ---- マス目の列（bool・文字） ----
    private void DrawStrip(DrawingContext dc, Item item, double x0, double top, double w)
    {
        var s = item.Shot;
        var (cell, perRow) = StripLayout(s, w);
        var pointers = PointersFor(item);
        double left = x0 + 30;

        for (int i = 0; i < s.Cols; i++)
        {
            int row = i / perRow, col = i % perRow;
            double x = left + col * cell, y = top + row * (cell + 16);
            if (col == 0) DrawText(dc, i.ToString(), x0, y + cell / 2 - 7, 9, SubTextBrush, alignRight: 26);

            var rect = new Rect(x, y, cell - 2, cell - 2);
            DrawCell(dc, s.Kind, s.Values[i], rect, item.Level(i, _updates), 0, 1);

            var here = pointers.Where(p => p.Index == i).ToList();
            for (int k = 0; k < here.Count; k++)
            {
                dc.DrawRectangle(null, new Pen(here[k].Brush, 2.5), Rect.Inflate(rect, 1 + k * 2, 1 + k * 2));
            }
            if (here.Count > 0)
                DrawText(dc, string.Join(",", here.Select(p => p.Name)), x, y + cell - 1, 9, here[0].Brush, bold: true);
        }
    }

    // ---- 2次元のマス目 ----
    private void DrawGrid(DrawingContext dc, Item item, double x0, double top, double w)
    {
        var s = item.Shot;
        var (cell, header) = GridLayout(s, w);
        double left = x0 + 24, gridTop = top + header;

        double min = double.MaxValue, max = double.MinValue;
        if (s.Kind == CellKind.Number)
            foreach (var v in s.Values)
                if (!double.IsNaN(v) && !IsInf(v)) { min = Math.Min(min, v); max = Math.Max(max, v); }

        // 行・列の番号
        if (header > 0)
            for (int c = 0; c < s.Cols; c++)
                DrawText(dc, c.ToString(), left + c * cell, top + 1, 9, SubTextBrush, center: cell);
        for (int r = 0; r < s.Rows; r++)
            if (cell >= 12 || r % 5 == 0)
                DrawText(dc, r.ToString(), x0, gridTop + r * cell + cell / 2 - 6, 9, SubTextBrush, alignRight: 20);

        for (int r = 0; r < s.Rows; r++)
            for (int c = 0; c < s.Cols; c++)
            {
                double v = s[r, c];
                if (double.IsNaN(v)) continue;
                var rect = new Rect(left + c * cell, gridTop + r * cell, cell, cell);
                DrawCell(dc, s.Kind, v, rect, item.Level(r * s.Cols + c, _updates), min, max);
            }

        // 「行, 列」の組になっている変数のマスを枠で囲む
        int color = 0;
        foreach (var (rowName, colName) in CellPairs)
        {
            if (!_ints.TryGetValue(rowName, out var r) || !_ints.TryGetValue(colName, out var c)) continue;
            var brush = PointerBrushes[color++ % PointerBrushes.Length];
            if (r < 0 || r >= s.Rows || c < 0 || c >= s.Cols) continue;
            var rect = new Rect(left + c * cell, gridTop + r * cell, cell, cell);
            dc.DrawRectangle(null, new Pen(brush, 3), Rect.Inflate(rect, 1, 1));
            DrawText(dc, $"[{rowName},{colName}]", left + s.Cols * cell + 6, gridTop + r * cell + cell / 2 - 7, 11, brush, bold: true);
        }
    }

    /// <summary>マス1つ。数は min〜max の色の濃さ、bool は緑／灰、文字は # を壁色に。</summary>
    private void DrawCell(DrawingContext dc, CellKind kind, double v, Rect rect, int level, double min, double max)
    {
        bool changed = level == 2;
        Brush fill;
        string text;
        Brush textBrush = TextBrush;
        switch (kind)
        {
            case CellKind.Bool:
                fill = v != 0 ? TrueBrush : FalseBrush;
                text = v != 0 ? "T" : "";
                textBrush = Brushes.White;
                break;
            case CellKind.Char:
                char ch = (char)v;
                fill = ch switch
                {
                    '#' => WallBrush,
                    '.' or ' ' => Brushes.White,
                    _ => CharBrush(ch),
                };
                if (ch == '#') textBrush = Brushes.White;
                text = ch == ' ' ? "␣" : ch.ToString();
                break;
            default:
                if (IsInf(v))
                {
                    fill = InfBrush;
                    text = "∞";
                }
                else
                {
                    double t = max > min ? (v - min) / (max - min) : 0.5;
                    fill = HeatBrush(t);
                    if (t > 0.6) textBrush = Brushes.White;
                    text = Format(v);
                }
                break;
        }

        dc.DrawRectangle(changed ? ChangedBrush : level == 1 ? RecentBrush : fill, GridPen, rect);
        if (changed) dc.DrawRectangle(null, ChangedPen, Rect.Inflate(rect, -1, -1));
        if (level > 0) textBrush = TextBrush;

        if (rect.Width >= 16 && text.Length > 0)
        {
            double size = Math.Min(12, rect.Width * 0.42);
            if (text.Length > 3) size = Math.Min(size, rect.Width / text.Length * 1.6);
            if (size >= 6.5)
                DrawText(dc, text, rect.X, rect.Y + rect.Height / 2 - size * 0.7, size, textBrush, center: rect.Width);
        }
    }

    private static readonly Dictionary<int, Brush> HeatCache = new();

    private static Brush HeatBrush(double t)
    {
        int key = (int)Math.Round(Math.Clamp(t, 0, 1) * 40);
        if (HeatCache.TryGetValue(key, out var b)) return b;
        double k = key / 40.0;
        // 白 → 青
        var color = Color.FromRgb((byte)(255 - k * 225), (byte)(255 - k * 160), (byte)(255 - k * 30));
        return HeatCache[key] = Palette.Freeze(color);
    }

    private static readonly Dictionary<char, Brush> CharCache = new();

    private static Brush CharBrush(char ch)
    {
        if (CharCache.TryGetValue(ch, out var b)) return b;
        Color color = ch switch
        {
            'S' or 's' => Color.FromRgb(0xA5, 0xD6, 0xA7),
            'G' or 'g' => Color.FromRgb(0xEF, 0x9A, 0x9A),
            _ => FromHue(ch * 47 % 360),
        };
        return CharCache[ch] = Palette.Freeze(color);
    }

    private static Color FromHue(double hue)
    {
        // 淡い色（彩度低め・明度高め）
        double c = 0.35, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = 0.6;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Brush Lighten(Brush brush)
    {
        if (brush is not SolidColorBrush sb) return brush;
        var c = sb.Color;
        return Palette.Freeze(Color.FromRgb((byte)(c.R + (255 - c.R) * 0.35), (byte)(c.G + (255 - c.G) * 0.35), (byte)(c.B + (255 - c.B) * 0.35)));
    }

    private static string Format(double v) =>
        IsInf(v) ? "∞" : Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);

    private FormattedText DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush,
        bool bold = false, double center = 0, double alignRight = 0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (center > 0) x += (center - ft.Width) / 2;
        else if (alignRight > 0) x += alignRight - ft.Width;
        dc.DrawText(ft, new Point(x, y));
        return ft;
    }
}

internal static class PenExtensions
{
    public static Pen Frozen(this Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
