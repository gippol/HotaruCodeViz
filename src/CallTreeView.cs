using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HotaruCodeViz;

/// <summary>
/// 呼び出しの木。上が呼んだ側、下が呼ばれた側。
///   今実行中の呼び出し … 濃い黄色（その上の「今いる道筋」は薄い黄色）
///   戻った呼び出し     … 白（→ 戻り値）、例外で抜けたものは赤
///   ×N                 … 同じ引数での呼び出しが N 回目（メモ化していない再帰で増える）
/// 呼び出しが多いときは、今いる道筋とその兄弟だけを描いて、ほかは「… 他 N 回」「+N」にまとめる。
/// 箱をクリックすると CallClicked、右クリックすると CallRightClicked（どちらも CallLog.Nodes の位置）。
/// </summary>
public sealed class CallTreeView : FrameworkElement
{
    /// <summary>この数までは木を全部描く。</summary>
    public const int FullLimit = 300;
    private const int KeepFirst = 2, KeepLast = 9;
    /// <summary>呼び出しが多いとき、今いる道筋のほかに描く箱の数のめやす（上の段から）。</summary>
    private const int ExtraBudget = 120, ExtraLevels = 4;

    private const double MinBoxW = 66, MaxBoxW = 150, BoxH = 40, HGap = 8, VGap = 28, Pad = 16;

    private sealed class DNode
    {
        public int Index = -1;                 // -1 = 「… 他 N 回」
        public string Label = "";
        public string Result = "";
        public bool Running, OnPath, Current, Threw;
        public int Repeat, Collapsed, Skipped;
        public List<DNode> Children = new();
        public double X, Y, Width, BoxW;       // Width = 部分木の幅、BoxW = 箱の幅
    }

    private List<DNode> _roots = new();
    private Size _size;
    private string? _message;

    /// <summary>今実行中の呼び出しの箱（スクロールして見せる用）。</summary>
    public Rect? CurrentRect { get; private set; }

    private static readonly Typeface Face = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush CurrentFill = Palette.Freeze(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush PathFill = Palette.Freeze(Color.FromRgb(0xFF, 0xF3, 0xC4));
    private static readonly Brush ThrewFill = Palette.Freeze(Color.FromRgb(0xFF, 0xEB, 0xEE));
    private static readonly Brush TextBrush = Palette.Freeze(Color.FromRgb(0x26, 0x32, 0x38));
    private static readonly Brush SubBrush = Palette.Freeze(Color.FromRgb(0x90, 0x90, 0x90));
    private static readonly Brush ResultBrush = Palette.Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush RepeatBrush = Palette.Freeze(Color.FromRgb(0x8E, 0x24, 0xAA));
    private static readonly Pen CurrentPen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xF5, 0x7C, 0x00)), 2.5));
    private static readonly Pen PathPen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xF9, 0xA8, 0x25)), 1.5));
    private static readonly Pen DonePen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xB0, 0xBE, 0xC5)), 1));
    private static readonly Pen ThrewPen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xE5, 0x39, 0x35)), 1.5));
    private static readonly Pen EdgePen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xCF, 0xD8, 0xDC)), 1));
    private static readonly Pen PathEdgePen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xF9, 0xA8, 0x25)), 2.5));
    private static readonly Pen EllipsisPen = Frozen(new Pen(Palette.Freeze(Color.FromRgb(0xB0, 0xB0, 0xB0)), 1) { DashStyle = DashStyles.Dash });

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    public void ShowMessage(string? message)
    {
        _roots = new();
        _message = message;
        CurrentRect = null;
        _size = new Size(0, 0);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// ステップ step を表示しているときの木を作り直す。
    /// names は Tracer.Enter の ID → メソッド名。戻り値は（今の呼び出し, 呼ばれた回数, 今の深さ）。
    /// </summary>
    public (int Current, int Visible, int Depth) Update(CallLog log, long step, IReadOnlyDictionary<int, string> names)
    {
        _message = null;
        int current, visible, depth = 0;
        lock (log.Lock)
        {
            var nodes = log.Nodes;
            current = log.CurrentAt(step, out visible);

            var path = new HashSet<int>();
            for (int c = current; c >= 0; c = nodes[c].Parent)
            {
                path.Add(c);
                depth++;
            }
            bool full = visible <= FullLimit;
            int budget = ExtraBudget;

            DNode Make(int index, int level)
            {
                var n = nodes[index];
                bool finished = n.FinishedAt(step);
                var d = new DNode
                {
                    Index = index,
                    Label = Shorten($"{(names.TryGetValue(n.MethodId, out var name) ? name : "?")}({string.Join(", ", n.Args ?? [])})", 19),
                    Running = !finished,
                    OnPath = path.Contains(index),
                    Current = index == current,
                    Threw = finished && n.State == CallState.Threw,
                    Repeat = n.Repeat,
                };
                d.Result = !finished ? "実行中…" : d.Threw ? "💥 例外" : n.Result is null ? "✓" : "→ " + Shorten(n.Result, 15);

                var kids = VisibleChildren(n.Children, visible);
                // 多いときは、今いる道筋と、上のほうの段（箱の数の上限まで）だけ広げる
                bool expand = full || d.OnPath || (level < ExtraLevels && budget > 0 && kids.Count > 0);
                if (expand)
                {
                    if (!full && !d.OnPath) budget -= Math.Min(kids.Count, KeepFirst + KeepLast + 1);
                    d.Children = Select(kids, level + 1);
                }
                else d.Collapsed = kids.Count;
                return d;
            }

            List<DNode> Select(List<int> kids, int level)
            {
                var result = new List<DNode>();
                if (full || kids.Count <= KeepFirst + KeepLast + 1)
                {
                    foreach (var k in kids) result.Add(Make(k, level));
                    return result;
                }
                foreach (var k in kids.Take(KeepFirst)) result.Add(Make(k, level));
                int skipped = kids.Count - KeepFirst - KeepLast;
                result.Add(new DNode { Skipped = skipped, Label = $"… 他 {skipped:N0} 回" });
                foreach (var k in kids.Skip(kids.Count - KeepLast)) result.Add(Make(k, level));
                return result;
            }

            var roots = new List<int>();
            for (int i = 0; i < visible; i++)
                if (nodes[i].Parent < 0) roots.Add(i);
            _roots = Select(roots, 0);
        }

        Layout();
        InvalidateMeasure();
        InvalidateVisual();
        return (current, visible, depth);
    }

    private static List<int> VisibleChildren(List<int>? children, int visible)
    {
        var list = new List<int>();
        if (children is null) return list;
        foreach (var c in children)
        {
            if (c >= visible) break; // 子は呼ばれた順に並んでいる
            list.Add(c);
        }
        return list;
    }

    private static int Columns(string s) => s.Sum(c => c < 0x2E80 ? 1 : 2);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ======================= レイアウト =======================

    private void Layout()
    {
        CurrentRect = null;
        double x = Pad, maxDepth = 0;
        foreach (var r in _roots)
        {
            Measure(r);
            Place(r, x, 0, ref maxDepth);
            x += r.Width;
        }
        _size = new Size(x + Pad, Pad * 2 + (maxDepth + 1) * (BoxH + VGap));
    }

    private static void Measure(DNode n)
    {
        double sum = 0;
        foreach (var c in n.Children)
        {
            Measure(c);
            sum += c.Width;
        }
        // 箱の幅は文字の長さに合わせる（Consolas 12px ≒ 1文字 7px、日本語は 2文字分）
        int chars = Math.Max(Columns(n.Label), Columns(n.Result));
        n.BoxW = Math.Clamp(chars * 7 + 16, MinBoxW, MaxBoxW);
        n.Width = Math.Max(n.BoxW + HGap, sum);
    }

    private void Place(DNode n, double left, int depth, ref double maxDepth)
    {
        n.X = left + n.Width / 2;
        n.Y = Pad + depth * (BoxH + VGap);
        maxDepth = Math.Max(maxDepth, depth);
        if (n.Current) CurrentRect = new Rect(n.X - n.BoxW / 2 - 40, n.Y - 20, n.BoxW + 80, BoxH + 60);

        double x = left + (n.Width - n.Children.Sum(c => c.Width)) / 2;
        foreach (var c in n.Children)
        {
            Place(c, x, depth + 1, ref maxDepth);
            x += c.Width;
        }
    }

    protected override Size MeasureOverride(Size available) => _size;

    // ======================= 描画 =======================

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(ActualWidth, _size.Width), Math.Max(ActualHeight, _size.Height)));
        if (_roots.Count == 0)
        {
            Text(dc, _message ?? "メソッドの呼び出しがあると、ここに呼び出しの木を描きます（「変数ウォッチ」をオンにして実行）。",
                Pad, Pad, 13, SubBrush);
            return;
        }
        foreach (var r in _roots) DrawEdges(dc, r);
        foreach (var r in _roots) DrawNodes(dc, r);
    }

    private static void DrawEdges(DrawingContext dc, DNode n)
    {
        foreach (var c in n.Children)
        {
            var pen = n.OnPath && c.OnPath ? PathEdgePen : EdgePen;
            dc.DrawLine(pen, new Point(n.X, n.Y + BoxH), new Point(c.X, c.Y));
            DrawEdges(dc, c);
        }
    }

    private void DrawNodes(DrawingContext dc, DNode n)
    {
        var rect = new Rect(n.X - n.BoxW / 2, n.Y, n.BoxW, BoxH);
        if (n.Index < 0)
        {
            dc.DrawRoundedRectangle(Brushes.White, EllipsisPen, rect, 6, 6);
            Text(dc, n.Label, rect.X, rect.Y + 12, 12, SubBrush, center: rect.Width);
            return;
        }

        var (fill, pen) = n.Current ? (CurrentFill, CurrentPen)
            : n.OnPath ? (PathFill, PathPen)
            : n.Threw ? (ThrewFill, ThrewPen)
            : (Brushes.White, DonePen);
        dc.DrawRoundedRectangle(fill, pen, rect, 6, 6);
        Text(dc, n.Label, rect.X, rect.Y + 4, 12, TextBrush, bold: n.OnPath, center: rect.Width);
        Text(dc, n.Result, rect.X, rect.Y + 21, 11, n.Running ? SubBrush : n.Threw ? ThrewPen.Brush : ResultBrush, center: rect.Width);

        if (n.Repeat > 1)
        {
            var badge = new Rect(rect.Right - 14, rect.Y - 9, 30, 16);
            dc.DrawRoundedRectangle(RepeatBrush, null, badge, 8, 8);
            Text(dc, $"×{n.Repeat}", badge.X, badge.Y + 1, 10, Brushes.White, bold: true, center: badge.Width);
        }
        if (n.Collapsed > 0)
            Text(dc, $"+{n.Collapsed:N0}", rect.X, rect.Bottom + 2, 10, SubBrush, center: rect.Width);

        foreach (var c in n.Children) DrawNodes(dc, c);
    }

    // ======================= クリック =======================

    /// <summary>呼び出しの箱を左クリックした（CallLog.Nodes の位置）。</summary>
    public event Action<int>? CallClicked;

    /// <summary>呼び出しの箱を右クリックした（CallLog.Nodes の位置）。</summary>
    public event Action<int>? CallRightClicked;

    /// <summary>点 p にある呼び出しの箱（CallLog.Nodes の位置）。なければ -1（「… 他 N 回」も -1）。</summary>
    public int CallAt(Point p)
    {
        static int Find(List<DNode> list, Point p)
        {
            foreach (var n in list)
            {
                if (n.Index >= 0 && new Rect(n.X - n.BoxW / 2, n.Y, n.BoxW, BoxH).Contains(p)) return n.Index;
                int c = Find(n.Children, p);
                if (c >= 0) return c;
            }
            return -1;
        }
        return Find(_roots, p);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = CallAt(e.GetPosition(this)) >= 0 ? Cursors.Hand : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        int index = CallAt(e.GetPosition(this));
        if (index < 0) return;
        e.Handled = true;
        CallClicked?.Invoke(index);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        int index = CallAt(e.GetPosition(this));
        if (index < 0) return;
        e.Handled = true;
        CallRightClicked?.Invoke(index);
    }

    private void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool bold = false, double center = 0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (center > 0) x += (center - ft.Width) / 2;
        dc.DrawText(ft, new Point(x, y));
    }
}
