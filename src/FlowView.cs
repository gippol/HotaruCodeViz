using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HotaruCodeViz;

/// <summary>
/// 🔀 制御フロータブ：メソッド 1 つ分の制御フローグラフを描く。
///   ・箱 = 基本ブロック（途中で分かれも合流もしない処理のまとまり）、◆ = 最後の分岐
///   ・矢印 = 次に進むブロック（true / false は条件で分かれる先）。ループで戻る矢印は左側を回る
///   ・今実行中のブロックは黄色、通ったブロックと辺は青（太いほど多く通った）、×N は通った回数
///   ・どこからも来られないブロック（到達できないコード）は灰色の点線
/// </summary>
public sealed class FlowView : FrameworkElement
{
    private const double Pad = 16, LayerGap = 46, ColGap = 30, HeaderH = 20, LineH = 17, LaneGap = 12;
    private const double FontSize = 12.5, SmallSize = 11;
    private const double MinBoxW = 120, MaxBoxW = 420;
    private const int MaxLines = 8;

    private static readonly Typeface Face = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Brush TextBrush = Palette.Freeze(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush SubTextBrush = Palette.Freeze(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush BranchBrush = Palette.Freeze(Color.FromRgb(0x6A, 0x1B, 0x9A));
    private static readonly Brush CountBrush = Palette.Freeze(Color.FromRgb(0x15, 0x65, 0xC0));
    private static readonly Brush TrueBrush = Palette.Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush FalseBrush = Palette.Freeze(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush HeaderBack = Palette.Freeze(Color.FromRgb(0xEC, 0xEF, 0xF1));
    private static readonly Brush VisitedHeader = Palette.Freeze(Color.FromRgb(0xE3, 0xF2, 0xFD));
    private static readonly Brush CurrentHeader = Palette.Freeze(Color.FromRgb(0xFF, 0xE0, 0x82));
    private static readonly Brush CurrentBack = Palette.Freeze(Color.FromRgb(0xFF, 0xF8, 0xE1));
    private static readonly Brush GhostBack = Palette.Freeze(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private static readonly Brush TerminalBack = Palette.Freeze(Color.FromRgb(0xCF, 0xD8, 0xDC));
    private static readonly Brush TerminalVisited = Palette.Freeze(Color.FromRgb(0xBB, 0xDE, 0xFB));
    private static readonly Brush RegionBrush = Palette.Freeze(Color.FromRgb(0x5D, 0x40, 0x37));
    private static readonly Brush RegionBack = Palette.Freeze(Color.FromRgb(0xEF, 0xEB, 0xE9));
    private static readonly Brush LabelBack = Palette.Freeze(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    private static readonly Color EdgeGray = Color.FromRgb(0xB0, 0xBE, 0xC5);
    private static readonly Color EdgeBlue = Color.FromRgb(0x1E, 0x88, 0xE5);
    private static readonly Color EdgeAmber = Color.FromRgb(0xFF, 0x8F, 0x00);

    private static readonly Pen BoxPen = new Pen(Palette.Freeze(Color.FromRgb(0xB0, 0xBE, 0xC5)), 1).Frozen();
    private static readonly Pen VisitedPen = new Pen(Palette.Freeze(Color.FromRgb(0x64, 0xB5, 0xF6)), 1.2).Frozen();
    private static readonly Pen CurrentPen = new Pen(Palette.Freeze(Color.FromRgb(0xF5, 0x7C, 0x00)), 2.5).Frozen();
    private static readonly Pen GhostPen = new Pen(Palette.Freeze(Color.FromRgb(0xBD, 0xBD, 0xBD)), 1) { DashStyle = DashStyles.Dash }.Frozen();
    private static readonly Pen RowPen = new Pen(Palette.Freeze(Color.FromRgb(0xEC, 0xEF, 0xF1)), 1).Frozen();

    private FlowGraph? _graph;
    private FlowTracker? _tracker;
    private int _current = -1;
    private int[]? _lastPath;
    private string? _message = "コードを入力すると、ここにメソッドごとの制御フローグラフを描きます。";
    private Layout? _layout;
    private readonly Dictionary<FlowGraph, Layout> _cache = new();

    /// <summary>箱をクリックした（そのブロックのソースを選ぶのに使う）。</summary>
    public event Action<FlowBlock>? BlockClicked;

    public FlowGraph? Graph => _graph;

    /// <summary>今実行中のブロックの位置（スクロールに使う）。</summary>
    public Rect? CurrentRect => _layout is { } l && _current >= 0 && _current < l.Nodes.Length ? l.Nodes[_current].Rect : null;

    // ======================= データ =======================

    /// <param name="current">今実行中のブロック（グラフの中の番号、-1 = なし）</param>
    /// <param name="lastPath">今のブロックへ来た道筋（全体の辺の番号）。ライブのときだけ</param>
    public void Show(FlowGraph graph, FlowTracker? tracker, int current, int[]? lastPath)
    {
        bool relayout = graph != _graph;
        _graph = graph;
        _tracker = tracker;
        _current = current;
        _lastPath = lastPath;
        _message = null;
        if (relayout)
        {
            _layout = null;
            InvalidateMeasure();
        }
        InvalidateVisual();
    }

    public void ShowMessage(string message)
    {
        _graph = null;
        _layout = null;
        _message = message;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>グラフの形が変わった（コードが変わった）：作った絵を捨てる。</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _layout = null;
    }

    private Layout CurrentLayout()
    {
        if (_layout is not null) return _layout;
        if (_graph is null) return _layout = new Layout { Nodes = [], Routes = [] };
        if (!_cache.TryGetValue(_graph, out var l)) _cache[_graph] = l = BuildLayout(_graph);
        return _layout = l;
    }

    // ======================= 並べ方 =======================

    private sealed class Node
    {
        public required FlowBlock Block;
        public required string Header;
        public required string[] Lines;
        public int HiddenLines;
        public Rect Rect;
        public bool Terminal => Block.Kind != FlowBlockKind.Block;
    }

    private sealed class Route
    {
        public required FlowEdge Edge;
        public required Geometry Path;
        public required Geometry Head;
        public string? Label;            // true / false
        public Point LabelAt;
        public bool LabelLeft;           // ラベルを点の左に書く
    }

    private sealed class Layout
    {
        public required Node[] Nodes;
        public required Route[] Routes;
        public Size Size;
    }

    private Node MakeNode(FlowBlock b)
    {
        string header = b.Kind switch
        {
            FlowBlockKind.Entry => "▶ 開始",
            FlowBlockKind.Exit => "■ 終了",
            _ => $"B{b.Index}" + (b.Reachable ? "" : "　到達できない"),
        };
        var lines = b.Lines.ToList();
        int hidden = 0;
        if (lines.Count > MaxLines)
        {
            hidden = lines.Count - (MaxLines - 1);
            lines = lines.Take(MaxLines - 1).ToList();
        }
        if (b.Kind == FlowBlockKind.Block && lines.Count == 0 && b.Branch is null && hidden == 0)
            lines.Add(b.Throws ? "（例外を投げる）" : "（なにもしない）");
        var node = new Node { Block = b, Header = header, Lines = lines.ToArray(), HiddenLines = hidden };

        double w, h;
        if (node.Terminal)
        {
            w = TextWidth(header, FontSize, LabelFace) + 28;
            h = 26;
        }
        else
        {
            // 見出し：B3 [catch] ×123（回数の分は先に空けておく）
            double head = TextWidth(header, FontSize, BoldFace) + 16 + (b.Region is null ? 0 : TextWidth(b.Region, SmallSize, Face) + 14) + 56;
            double body = node.Lines.Select(l => TextWidth(l, FontSize, Face)).DefaultIfEmpty(0).Max() + 16;
            if (b.Branch is not null) body = Math.Max(body, TextWidth("◆ " + b.Branch, FontSize, BoldFace) + 16);
            w = Math.Clamp(Math.Max(head, body), MinBoxW, MaxBoxW);
            h = HeaderH + 4 + (node.Lines.Length + (b.Branch is null ? 0 : 1) + (hidden > 0 ? 1 : 0)) * LineH + 4;
        }
        node.Rect = new Rect(0, 0, w, h);
        return node;
    }

    private Layout BuildLayout(FlowGraph g)
    {
        var nodes = g.Blocks.Select(MakeNode).ToArray();
        int layers = g.LayerCount;
        var byLayer = Enumerable.Range(0, layers).Select(l => nodes.Where(n => n.Block.Layer == l).ToList()).ToList();

        // ---- 縦：段ごとの高さ ----
        var layerTop = new double[layers];
        double y = Pad;
        for (int l = 0; l < layers; l++)
        {
            layerTop[l] = y;
            double h = byLayer[l].Select(n => n.Rect.Height).DefaultIfEmpty(0).Max();
            y += h + LayerGap;
        }

        // ---- 横：前から来る辺の元の真下をめざして、重ならないように並べる ----
        var center = new double[nodes.Length];
        for (int l = 0; l < layers; l++)
        {
            var items = byLayer[l].Select(n =>
            {
                var from = n.Block.In.Select(e => g.Edges[e]).Where(e => !e.Back && nodes[e.From].Block.Layer < l)
                    .Select(e => center[e.From]).ToList();
                double desired = from.Count > 0 ? from.Average() : n.Block.Index > 0 ? center[n.Block.Index - 1] : 0;
                return (Node: n, Desired: desired);
            }).OrderBy(x => x.Desired).ThenBy(x => x.Node.Block.Index).ToList();

            var left = new double[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                left[i] = items[i].Desired - items[i].Node.Rect.Width / 2;
                if (i > 0) left[i] = Math.Max(left[i], left[i - 1] + items[i - 1].Node.Rect.Width + ColGap);
            }
            // 全体を、めざした位置からのずれが平均 0 になるように戻す
            double shift = items.Count == 0 ? 0 : items.Select((x, i) => left[i] - (x.Desired - x.Node.Rect.Width / 2)).Average();
            for (int i = 0; i < items.Count; i++)
            {
                var n = items[i].Node;
                n.Rect = new Rect(left[i] - shift, layerTop[l], n.Rect.Width, n.Rect.Height);
                center[n.Block.Index] = n.Rect.Left + n.Rect.Width / 2;
            }
        }

        // ---- 辺の道筋（点の列）----
        var paths = new List<(FlowEdge Edge, List<Point> Points, bool Curve)>();

        // 出口・入口の位置：前へ進む辺は箱の下辺・上辺に、相手の位置の順に散らす
        // 前へ進む辺のうち、間の段の箱にぶつかるもの（右側の通り道を回る）
        var blocked = new HashSet<int>();
        foreach (var e in g.Edges.Where(e => !e.Back))
        {
            int lo = nodes[e.From].Block.Layer + 1, hi = nodes[e.To].Block.Layer - 1;
            double x0 = Math.Min(center[e.From], center[e.To]) - 8, x1 = Math.Max(center[e.From], center[e.To]) + 8;
            if (hi >= lo && nodes.Any(n => n.Block.Layer >= lo && n.Block.Layer <= hi && n.Rect.Right > x0 && n.Rect.Left < x1))
                blocked.Add(e.Index);
        }

        // 右側を回る辺は、箱のいちばん右から出て、いちばん右に入る（ほかの辺と交差しないように）
        var outPort = new Dictionary<int, Point>();
        var inPort = new Dictionary<int, Point>();
        foreach (var n in nodes)
        {
            var outs = n.Block.Out.Select(e => g.Edges[e]).Where(e => !e.Back)
                .OrderBy(e => blocked.Contains(e.Index)).ThenBy(e => center[e.To]).ToList();
            for (int i = 0; i < outs.Count; i++)
                outPort[outs[i].Index] = new Point(n.Rect.Left + n.Rect.Width * (i + 1) / (outs.Count + 1), n.Rect.Bottom);
            var ins = n.Block.In.Select(e => g.Edges[e]).Where(e => !e.Back)
                .OrderBy(e => blocked.Contains(e.Index)).ThenBy(e => center[e.From]).ToList();
            for (int i = 0; i < ins.Count; i++)
            {
                double t = ins.Count == 1 ? 0.5 : 0.3 + 0.4 * i / (ins.Count - 1);
                inPort[ins[i].Index] = new Point(n.Rect.Left + n.Rect.Width * t, n.Rect.Top);
            }
        }

        // 戻る辺（ループ）：左側の通り道。内側のループほど箱の近く
        var leftLanes = new List<(int From, int To, double X)>();
        foreach (var e in g.Edges.Where(e => e.Back).OrderBy(e => nodes[e.From].Block.Layer - nodes[e.To].Block.Layer))
        {
            var s = nodes[e.From];
            var t = nodes[e.To];
            int lo = t.Block.Layer, hi = s.Block.Layer;
            double minLeft = nodes.Where(n => n.Block.Layer >= lo && n.Block.Layer <= hi).Min(n => n.Rect.Left);
            int k = leftLanes.Count(x => x.From <= hi && x.To >= lo);
            double lane = Math.Min(minLeft, leftLanes.Where(x => x.From <= hi && x.To >= lo).Select(x => x.X).DefaultIfEmpty(minLeft).Min()) - 18 - (k > 0 ? LaneGap : 0);
            leftLanes.Add((lo, hi, lane));

            int sameOut = s.Block.Out.Count(i => g.Edges[i].Back && g.Edges[i].Index < e.Index);
            int sameIn = t.Block.In.Count(i => g.Edges[i].Back && g.Edges[i].Index < e.Index);
            var start = new Point(s.Rect.Left, s.Rect.Bottom - 8 - sameOut * 6);
            var end = new Point(t.Rect.Left, t.Rect.Top + HeaderH / 2 + sameIn * 6);
            if (s == t) end.Y = Math.Min(end.Y, start.Y - 10);
            paths.Add((e, [start, new Point(lane, start.Y), new Point(lane, end.Y), end], false));
        }

        // 前へ進む辺：となりの段なら曲線、間の段の箱にぶつかるなら右側の通り道
        var rightLanes = new List<(int From, int To, double X)>();
        foreach (var e in g.Edges.Where(e => !e.Back))
        {
            var s = nodes[e.From];
            var t = nodes[e.To];
            var start = outPort[e.Index];
            var end = inPort[e.Index];
            int lo = s.Block.Layer + 1, hi = t.Block.Layer - 1;
            if (!blocked.Contains(e.Index))
            {
                paths.Add((e, [start, end], true));
                continue;
            }
            double maxRight = nodes.Where(n => n.Block.Layer >= lo && n.Block.Layer <= hi).Max(n => n.Rect.Right);
            double lane = Math.Max(maxRight, rightLanes.Where(x => x.From <= hi && x.To >= lo).Select(x => x.X).DefaultIfEmpty(maxRight).Max()) + 18;
            rightLanes.Add((lo, hi, lane));
            double y0 = start.Y + 12, y1 = end.Y - 12;
            paths.Add((e, [start, new Point(start.X, y0), new Point(lane, y0), new Point(lane, y1), new Point(end.X, y1), end], false));
        }

        // ---- 全体を左上に寄せる ----
        double minX = nodes.Select(n => n.Rect.Left).Concat(paths.SelectMany(p => p.Points).Select(p => p.X)).DefaultIfEmpty(0).Min();
        double maxX = nodes.Select(n => n.Rect.Right).Concat(paths.SelectMany(p => p.Points).Select(p => p.X)).DefaultIfEmpty(0).Max();
        double dx = Pad + 34 - minX; // 左に true / false の文字が出る分を空ける
        foreach (var n in nodes) n.Rect.Offset(dx, 0);

        var routes = new List<Route>();
        foreach (var (e, raw, curve) in paths)
        {
            var pts = raw.Select(p => new Point(p.X + dx, p.Y)).ToList();
            var (path, head, dir) = curve ? Curve(pts[0], pts[^1]) : Polyline(pts);
            var route = new Route { Edge = e, Path = path, Head = head };
            if (e.Kind != FlowEdgeKind.Always || e.Back)
            {
                route.Label = e.Kind switch { FlowEdgeKind.WhenTrue => "true", FlowEdgeKind.WhenFalse => "false", _ => "" };
                if (e.Back)
                {
                    route.LabelAt = new Point(pts[0].X - 4, pts[0].Y - 15);
                    route.LabelLeft = true;
                }
                else
                {
                    var s = nodes[e.From].Rect;
                    bool toLeft = pts[0].X < s.Left + s.Width / 2 - 1;
                    route.LabelAt = new Point(pts[0].X + (toLeft ? -4 : 4), pts[0].Y + 2);
                    route.LabelLeft = toLeft;
                }
            }
            routes.Add(route);
        }

        double width = maxX + dx + Pad + 40;
        double height = nodes.Select(n => n.Rect.Bottom).DefaultIfEmpty(0).Max() + Pad;
        return new Layout { Nodes = nodes, Routes = routes.ToArray(), Size = new Size(width, height) };
    }

    private static (Geometry Path, Geometry Head, Vector Dir) Curve(Point from, Point to)
    {
        var dir = new Vector(0, 1);
        var end = to - dir * 6;
        double dy = Math.Max(12, (end.Y - from.Y) / 2);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(from, false, false);
            c.BezierTo(new Point(from.X, from.Y + dy), new Point(end.X, end.Y - dy), end, true, true);
        }
        geo.Freeze();
        return (geo, Head(to, dir), dir);
    }

    /// <summary>角を丸めた折れ線。</summary>
    private static (Geometry Path, Geometry Head, Vector Dir) Polyline(List<Point> pts)
    {
        var lastDir = pts[^1] - pts[^2];
        if (lastDir.Length > 0) lastDir.Normalize();
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var a = pts[i] - pts[i - 1];
                var b = pts[i + 1] - pts[i];
                double r = Math.Min(6, Math.Min(a.Length, b.Length) / 2);
                if (a.Length > 0) a.Normalize();
                if (b.Length > 0) b.Normalize();
                c.LineTo(pts[i] - a * r, true, true);
                c.QuadraticBezierTo(pts[i], pts[i] + b * r, true, true);
            }
            c.LineTo(pts[^1] - lastDir * 6, true, true);
        }
        geo.Freeze();
        return (geo, Head(pts[^1], lastDir), lastDir);
    }

    private static Geometry Head(Point tip, Vector dir)
    {
        var normal = new Vector(-dir.Y, dir.X);
        var head = new StreamGeometry();
        using (var c = head.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(tip - dir * 8 + normal * 4, true, false);
            c.LineTo(tip - dir * 8 - normal * 4, true, false);
        }
        head.Freeze();
        return head;
    }

    protected override Size MeasureOverride(Size available)
    {
        var layout = CurrentLayout();
        return _graph is null ? new Size(0, 80) : layout.Size;
    }

    // ======================= 描画 =======================

    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string text, double size, Brush brush, Typeface face) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, Dip);

    private double TextWidth(string text, double size, Typeface face) => Text(text, size, TextBrush, face).WidthIncludingTrailingWhitespace;

    private long BlockCount(FlowBlock b) => _tracker is { } t && b.Global < t.BlockCounts.Length ? t.BlockCounts[b.Global] : 0;

    private long EdgeCount(FlowEdge e) => _tracker is { } t && e.Global < t.EdgeCounts.Length ? t.EdgeCounts[e.Global] : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var layout = CurrentLayout();
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(ActualWidth, layout.Size.Width), Math.Max(ActualHeight, layout.Size.Height)));
        if (_graph is null)
        {
            dc.DrawText(Text(_message ?? "", 13, SubTextBrush, LabelFace), new Point(Pad, Pad));
            return;
        }

        // 辺：通っていない → 通った（多いほど太い）→ 今のブロックへ来た道筋 の順に重ねる
        var last = _lastPath is null ? new HashSet<int>() : _lastPath.ToHashSet();
        long maxCount = layout.Routes.Select(r => EdgeCount(r.Edge)).DefaultIfEmpty(0).Max();
        foreach (var r in layout.Routes.OrderBy(r => last.Contains(r.Edge.Global) ? 2 : EdgeCount(r.Edge) > 0 ? 1 : 0))
        {
            long count = EdgeCount(r.Edge);
            Color color;
            double width;
            if (last.Contains(r.Edge.Global))
            {
                color = EdgeAmber;
                width = 3.2;
            }
            else if (count > 0)
            {
                color = EdgeBlue;
                width = 1.6 + 3.0 * Math.Log(1 + count) / Math.Log(2 + maxCount);
            }
            else
            {
                color = EdgeGray;
                width = 1.2;
            }
            var brush = Palette.Freeze(color);
            dc.DrawGeometry(null, new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat }, r.Path);
            dc.DrawGeometry(brush, null, r.Head);
        }

        foreach (var n in layout.Nodes) DrawNode(dc, n);

        // ラベル（true / false と、通った回数）
        foreach (var r in layout.Routes)
        {
            long count = EdgeCount(r.Edge);
            string label = (r.Label ?? "") + (count > 0 && r.Label is not null ? (r.Label.Length > 0 ? " " : "") + "×" + count.ToString("N0") : "");
            if (label.Length == 0) continue;
            var brush = r.Edge.Kind switch { FlowEdgeKind.WhenTrue => TrueBrush, FlowEdgeKind.WhenFalse => FalseBrush, _ => CountBrush };
            var t = Text(label, SmallSize, brush, LabelFace);
            var at = r.LabelLeft ? new Point(r.LabelAt.X - t.Width, r.LabelAt.Y) : r.LabelAt;
            dc.DrawRectangle(LabelBack, null, new Rect(at.X - 1, at.Y, t.Width + 2, t.Height));
            dc.DrawText(t, at);
        }
    }

    private void DrawNode(DrawingContext dc, Node n)
    {
        var b = n.Block;
        var r = n.Rect;
        long count = BlockCount(b);
        bool current = b.Index == _current;
        bool visited = count > 0 || current;

        if (n.Terminal)
        {
            var back = current ? CurrentHeader : visited ? TerminalVisited : TerminalBack;
            dc.DrawRoundedRectangle(back, current ? CurrentPen : BoxPen, r, r.Height / 2, r.Height / 2);
            var t = Text(n.Header, FontSize, TextBrush, LabelFace);
            dc.DrawText(t, new Point(r.Left + (r.Width - t.Width) / 2, r.Top + (r.Height - t.Height) / 2));
            return;
        }

        bool ghost = !b.Reachable;
        var pen = ghost ? GhostPen : current ? CurrentPen : visited ? VisitedPen : BoxPen;
        dc.DrawRoundedRectangle(ghost ? GhostBack : current ? CurrentBack : Brushes.White, pen, r, 4, 4);
        var headerBack = ghost ? GhostBack : current ? CurrentHeader : visited ? VisitedHeader : HeaderBack;
        dc.DrawRectangle(headerBack, null, new Rect(r.Left + 1.5, r.Top + 1.5, r.Width - 3, HeaderH - 1.5));

        var textBrush = ghost ? SubTextBrush : TextBrush;
        var header = Text(n.Header, FontSize, textBrush, BoldFace);
        dc.DrawText(header, new Point(r.Left + 6, r.Top + 2));
        double hx = r.Left + 6 + header.Width + 8;
        if (b.Region is { } region)
        {
            var rt = Text(region, SmallSize, RegionBrush, Face);
            dc.DrawRoundedRectangle(RegionBack, null, new Rect(hx - 3, r.Top + 3, rt.Width + 6, HeaderH - 5), 3, 3);
            dc.DrawText(rt, new Point(hx, r.Top + 4));
        }
        if (count > 0)
        {
            var ct = Text("×" + count.ToString("N0"), SmallSize, CountBrush, LabelFace);
            dc.DrawText(ct, new Point(r.Right - 6 - ct.Width, r.Top + 3));
        }

        double y = r.Top + HeaderH + 4;
        foreach (var line in n.Lines)
        {
            dc.DrawText(Text(line, FontSize, textBrush, Face), new Point(r.Left + 8, y));
            y += LineH;
        }
        if (n.HiddenLines > 0)
        {
            dc.DrawText(Text($"…ほか {n.HiddenLines} 行", SmallSize, SubTextBrush, Face), new Point(r.Left + 8, y + 1));
            y += LineH;
        }
        if (b.Branch is not null)
        {
            if (n.Lines.Length > 0) dc.DrawLine(RowPen, new Point(r.Left + 2, y - 1), new Point(r.Right - 2, y - 1));
            dc.DrawText(Text("◆ " + b.Branch, FontSize, ghost ? SubTextBrush : BranchBrush, BoldFace), new Point(r.Left + 8, y));
        }
    }

    // ======================= クリック =======================

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_layout is null) return;
        var p = e.GetPosition(this);
        var hit = _layout.Nodes.FirstOrDefault(n => n.Rect.Contains(p));
        if (hit is not null) BlockClicked?.Invoke(hit.Block);
    }
}
