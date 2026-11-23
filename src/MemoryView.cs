using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HotaruCodeViz;

/// <summary>メモリタブに出す変数1つ（名前・型は MainWindow が計装ポイントの情報から付ける）。</summary>
public sealed record MemSlot(string Name, string Type, MemValue Value);

/// <summary>スタックフレーム1つ。Depth は本当の深さ（0 = いちばん外側）。</summary>
public sealed record MemFrame(string Title, int Depth, MemSlot[] Slots);

/// <summary>
/// 🧠 メモリタブ：スタック（左）とヒープ（右）を描き、変数からオブジェクトへの参照を矢印で結ぶ。
///   ・値型（int・bool・構造体）と文字列は、変数の箱の中に値をそのまま書く
///   ・参照型（class・配列・List…）はヒープに箱を置き、変数からは矢印
///   ・同じオブジェクトを 2 本以上の矢印が指していたら「参照 ×N」（エイリアス）
///   ・前のステップから変わった変数・フィールドはオレンジ、新しくできたオブジェクトは緑の枠
///   ・どの変数からもたどれなくなったオブジェクトは、しばらく 🗑 の列に薄く残す（GC で回収されうる）
/// </summary>
public sealed class MemoryView : FrameworkElement
{
    /// <summary>たどれなくなったオブジェクトを、何ステップ（文）のあいだ 🗑 の列に残すか。</summary>
    private const int GhostKeep = 6;
    private const int MaxGhosts = 8;

    private MemoryShot? _shot;
    private MemFrame[] _frames = [];
    private MemSlot[] _statics = [];
    private string? _message;
    private long _updates;

    // ---- 前のステップとの比較 ----
    private Dictionary<string, string>? _prevSlots;
    private Dictionary<int, Dictionary<string, string>>? _prevFields;
    private Dictionary<int, HeapObject>? _prevObjects;
    private bool _prevReliable;
    private readonly HashSet<string> _changedSlots = new();
    private readonly Dictionary<int, HashSet<string>> _changedFields = new();
    private readonly HashSet<int> _newObjects = new();
    private readonly Dictionary<int, (HeapObject Obj, long At)> _ghosts = new();

    private Scene? _scene;

    public bool HasContent => _shot is not null;

    // ======================= データ =======================

    public void Update(MemoryShot shot, MemFrame[] frames, MemSlot[] statics)
    {
        _updates++;
        _message = null;
        // 省いたフレームやたどりきれなかったオブジェクトがあると、「消えた」「新しい」を正しく決められない
        bool reliable = !shot.Truncated && shot.SkippedFrames == 0;
        bool compare = _prevSlots is not null;

        _changedSlots.Clear();
        _changedFields.Clear();
        _newObjects.Clear();

        var slots = new Dictionary<string, string>();
        foreach (var s in statics) Compare(SlotKey("static", 0, s.Name), Key(shot, s.Value));
        foreach (var f in frames)
            foreach (var s in f.Slots)
                Compare(SlotKey(f.Title, f.Depth, s.Name), Key(shot, s.Value));

        void Compare(string key, string value)
        {
            slots[key] = value;
            if (compare && (!_prevSlots!.TryGetValue(key, out var old) || old != value)) _changedSlots.Add(key);
        }

        var fields = new Dictionary<int, Dictionary<string, string>>();
        var objects = new Dictionary<int, HeapObject>();
        foreach (var o in shot.Objects)
        {
            objects[o.Identity] = o;
            var now = new Dictionary<string, string>();
            foreach (var f in o.Fields) now[f.Name] = Key(shot, f.Value);
            if (o.Summary is not null) now[SummaryKey] = o.Summary;
            fields[o.Identity] = now;

            if (!compare) continue;
            if (!_prevFields!.TryGetValue(o.Identity, out var before))
            {
                if (_prevReliable) _newObjects.Add(o.Identity);
                continue;
            }
            foreach (var (name, value) in now)
            {
                if (before.TryGetValue(name, out var old) && old == value) continue;
                if (!_changedFields.TryGetValue(o.Identity, out var set)) _changedFields[o.Identity] = set = new HashSet<string>();
                set.Add(name);
            }
        }

        // 前はたどれたのに、今はどの変数からもたどれないオブジェクト
        if (compare && _prevReliable && reliable && _prevObjects is not null)
            foreach (var (id, o) in _prevObjects)
                if (!objects.ContainsKey(id)) _ghosts[id] = (o, _updates);
        foreach (var id in _ghosts.Keys.ToList())
            if (objects.ContainsKey(id) || _updates - _ghosts[id].At >= GhostKeep) _ghosts.Remove(id);
        while (_ghosts.Count > MaxGhosts)
            _ghosts.Remove(_ghosts.MinBy(kv => kv.Value.At).Key);

        _prevSlots = slots;
        _prevFields = fields;
        _prevObjects = objects;
        _prevReliable = reliable;

        _shot = shot;
        _frames = frames;
        _statics = statics;
        Invalidate();
    }

    /// <summary>この時点の記録がない（早送り中に通ったステップ）。絵は前のまま。</summary>
    public void ShowNoRecord()
    {
        if (_shot is null) return;
        _message = "（この時点のメモリの記録はありません — 早送り中に通ったステップです）";
        Invalidate();
    }

    /// <summary>前のステップとの比較を忘れる（巻き戻しで、離れたステップへ飛ぶとき）。</summary>
    public void ForgetPrevious()
    {
        _prevSlots = null;
        _prevFields = null;
        _prevObjects = null;
        _ghosts.Clear();
    }

    public void Clear(string? message = null)
    {
        _shot = null;
        _frames = [];
        _statics = [];
        _message = message;
        ForgetPrevious();
        _changedSlots.Clear();
        _changedFields.Clear();
        _newObjects.Clear();
        Invalidate();
    }

    private void Invalidate()
    {
        _scene = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private const string SummaryKey = "\u0001summary";

    private static string SlotKey(string title, int depth, string name) => $"{depth}\u001f{title}\u001f{name}";

    private static string Key(MemoryShot shot, MemValue v) =>
        v.IsRef ? "#" + shot.Objects[v.Ref].Identity : (v.Ref == MemValue.Omitted ? "~" : "=") + v.Text;

    // ======================= レイアウト =======================

    private sealed class Row
    {
        public required string Name;
        public required string Text;
        public required MemValue Value;
        public bool Changed;
        public Rect Rect;
    }

    private enum BoxKind { Frame, Statics, Heap, Ghost, Skipped }

    private sealed class Box
    {
        public required BoxKind Kind;
        public required string Header;
        public string? Badge;          // 参照 ×2 など
        public bool Current;           // 今実行中のフレーム
        public bool IsNew;
        public bool Cells;             // 数などの配列を横に並べたマスで描く
        public string? Summary;
        public bool SummaryChanged;
        public int Hidden;
        public int ObjectIndex = -1;   // ヒープの箱なら MemoryShot.Objects の番号
        public readonly List<Row> Rows = new();
        public Rect Rect;
        public double NameW;
    }

    private sealed record Arrow(Point From, int Target, bool Changed);

    private sealed class Scene
    {
        public readonly List<Box> Boxes = new();
        public readonly List<Arrow> Arrows = new();
        public readonly Dictionary<int, Box> Heap = new(); // オブジェクトの番号 → 箱
        public readonly List<(string Text, Point At)> Labels = new();
        public Size Size;
    }

    private const double Pad = 12, HeaderH = 22, RowH = 19, BoxGap = 12, ColGap = 64, CellH = 24;
    private const double FontSize = 12.5, SmallSize = 11;
    private const int MaxText = 40;

    private static readonly Typeface Face = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Consolas, Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Brush TextBrush = Palette.Freeze(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush SubTextBrush = Palette.Freeze(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush NameBrush = Palette.Freeze(Color.FromRgb(0x15, 0x65, 0xC0));
    private static readonly Brush ValueBrush = Palette.Freeze(Color.FromRgb(0xD8, 0x43, 0x15));
    private static readonly Brush FrameHeader = Palette.Freeze(Color.FromRgb(0xEC, 0xEF, 0xF1));
    private static readonly Brush CurrentHeader = Palette.Freeze(Color.FromRgb(0xFF, 0xE0, 0x82));
    private static readonly Brush StaticHeader = Palette.Freeze(Color.FromRgb(0xE1, 0xF5, 0xFE));
    private static readonly Brush HeapHeader = Palette.Freeze(Color.FromRgb(0xE8, 0xEA, 0xF6));
    private static readonly Brush GhostBack = Palette.Freeze(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private static readonly Brush ChangedBack = Palette.Freeze(Color.FromRgb(0xFF, 0xE0, 0xB2));
    private static readonly Brush AliasBrush = Palette.Freeze(Color.FromRgb(0x8E, 0x24, 0xAA));
    private static readonly Brush NewBrush = Palette.Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush RefDot = Palette.Freeze(Color.FromRgb(0x54, 0x6E, 0x7A));
    private static readonly Brush ArrowBrush = Palette.Freeze(Color.FromRgb(0x78, 0x90, 0x9C));
    private static readonly Brush ChangedArrowBrush = Palette.Freeze(Color.FromRgb(0xEF, 0x6C, 0x00));
    private static readonly Pen BoxPen = new Pen(Palette.Freeze(Color.FromRgb(0xB0, 0xBE, 0xC5)), 1).Frozen();
    private static readonly Pen CurrentPen = new Pen(Palette.Freeze(Color.FromRgb(0xF5, 0x7C, 0x00)), 2).Frozen();
    private static readonly Pen NewPen = new Pen(NewBrush, 2).Frozen();
    private static readonly Pen GhostPen = new Pen(Palette.Freeze(Color.FromRgb(0xBD, 0xBD, 0xBD)), 1) { DashStyle = DashStyles.Dash }.Frozen();
    private static readonly Pen RowPen = new Pen(Palette.Freeze(Color.FromRgb(0xEC, 0xEF, 0xF1)), 1).Frozen();
    private static readonly Pen ArrowPen = new Pen(ArrowBrush, 1.4).Frozen();
    private static readonly Pen ChangedArrowPen = new Pen(ChangedArrowBrush, 2.2).Frozen();

    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string text, double size, Brush brush, bool bold = false, Typeface? face = null) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? (bold ? BoldFace : Face), size, brush, Dip);

    private double TextWidth(string text, double size = FontSize, bool bold = false) => Text(text, size, TextBrush, bold).WidthIncludingTrailingWhitespace;

    private static string Trim(string s) => s.Length > MaxText ? s[..MaxText] + "…" : s;

    private string ValueText(MemValue v)
    {
        if (v.IsRef && _shot is { } shot) return "#" + shot.Objects[v.Ref].Identity;
        return Trim(v.Text ?? "");
    }

    private Scene BuildScene()
    {
        var scene = new Scene();
        if (_shot is not { } shot) return scene;

        // 何本の矢印に指されているか（2 本以上ならエイリアス）
        var incoming = new int[shot.Objects.Length];
        void CountRef(MemValue v) { if (v.IsRef) incoming[v.Ref]++; }
        foreach (var s in _statics) CountRef(s.Value);
        foreach (var f in _frames) foreach (var s in f.Slots) CountRef(s.Value);
        foreach (var o in shot.Objects) foreach (var f in o.Fields) CountRef(f.Value);

        // ---- 左：static フィールドとスタック ----
        double y = Pad;
        var left = new List<Box>();
        if (_statics.Length > 0)
        {
            var box = new Box { Kind = BoxKind.Statics, Header = "static フィールド" };
            foreach (var s in _statics)
                box.Rows.Add(new Row { Name = s.Name, Text = ValueText(s.Value), Value = s.Value, Changed = _changedSlots.Contains(SlotKey("static", 0, s.Name)) });
            left.Add(box);
        }
        // 上が今実行中（内側）
        for (int i = _frames.Length - 1; i >= 0; i--)
        {
            var f = _frames[i];
            var box = new Box { Kind = BoxKind.Frame, Header = f.Title, Current = i == _frames.Length - 1 };
            foreach (var s in f.Slots)
                box.Rows.Add(new Row { Name = s.Name, Text = ValueText(s.Value), Value = s.Value, Changed = _changedSlots.Contains(SlotKey(f.Title, f.Depth, s.Name)) });
            if (box.Rows.Count == 0) box.Summary = "（変数なし）";
            left.Add(box);
            if (i == 1 && shot.SkippedFrames > 0)
                left.Add(new Box { Kind = BoxKind.Skipped, Header = $"… {shot.SkippedFrames:N0} 段省略 …" });
        }

        double stackW = Math.Clamp(left.Select(BoxMinWidth).DefaultIfEmpty(180).Max(), 180, 360);
        scene.Labels.Add(("スタック（上が今実行中）", new Point(Pad, y)));
        y += 20;
        foreach (var box in left)
        {
            if (box.Kind == BoxKind.Statics)
            {
                // static は呼び出しと関係なく1つだけあるので、スタックとは少し離して上に置く
                Place(box, Pad, y, stackW);
                y = box.Rect.Bottom + BoxGap + 8;
                continue;
            }
            Place(box, Pad, y, stackW);
            y = box.Rect.Bottom + (box.Kind == BoxKind.Skipped ? 4 : BoxGap);
        }
        scene.Boxes.AddRange(left);
        double bottom = y;

        // ---- 右：ヒープ（変数から何本目の矢印で届くかで列を分ける） ----
        double heapX = Pad + stackW + ColGap;
        var levels = shot.Objects.Select((o, i) => (o, i)).GroupBy(t => t.o.Level).OrderBy(g => g.Key).ToList();
        if (shot.Objects.Length > 0) scene.Labels.Add(("ヒープ", new Point(heapX, Pad)));

        // 矢印の出発点（その列より左にある行）の y を見て、指している行の近くに置く
        var anchorY = new Dictionary<int, double>();
        void NoteAnchors(Box box)
        {
            foreach (var r in box.Rows)
                if (r.Value.IsRef && !anchorY.ContainsKey(r.Value.Ref)) anchorY[r.Value.Ref] = r.Rect.Top;
        }
        foreach (var box in left) NoteAnchors(box);

        double x = heapX;
        foreach (var level in levels)
        {
            var boxes = new List<(Box Box, double Want)>();
            foreach (var (o, index) in level)
            {
                var box = HeapBox(o, index, incoming[index]);
                boxes.Add((box, anchorY.TryGetValue(index, out var ay) ? ay : double.MaxValue));
            }
            double colW = Math.Clamp(boxes.Max(b => BoxMinWidth(b.Box)), 110, 420);
            double cy = Pad + 20;
            foreach (var (box, want) in boxes.OrderBy(b => b.Want))
            {
                double top = Math.Max(cy, want == double.MaxValue ? cy : want - 2);
                Place(box, x, top, box.Cells ? BoxMinWidth(box) : colW);
                cy = box.Rect.Bottom + BoxGap;
                scene.Heap[box.ObjectIndex] = box;
                scene.Boxes.Add(box);
                NoteAnchors(box);
            }
            bottom = Math.Max(bottom, cy);
            x += Math.Max(colW, boxes.Max(b => b.Box.Rect.Width)) + ColGap;
        }

        // ---- いちばん右：たどれなくなったオブジェクト ----
        if (_ghosts.Count > 0)
        {
            scene.Labels.Add(("🗑 どの変数からもたどれなくなった（GC で回収されうる）", new Point(x, Pad)));
            double gy = Pad + 20;
            foreach (var (o, _) in _ghosts.Values.OrderBy(g => g.Obj.Identity))
            {
                var box = new Box { Kind = BoxKind.Ghost, Header = $"#{o.Identity} {o.TypeName}", Summary = o.Summary, Hidden = o.Hidden };
                foreach (var f in o.Fields)
                    box.Rows.Add(new Row { Name = f.Name, Text = f.Value.IsRef ? "→" : Trim(f.Value.Text ?? ""), Value = new MemValue(null, MemValue.Inline) });
                Place(box, x, gy, Math.Clamp(BoxMinWidth(box), 110, 300));
                gy = box.Rect.Bottom + BoxGap;
                scene.Boxes.Add(box);
            }
            bottom = Math.Max(bottom, gy);
            x += 320;
        }

        // ---- 矢印 ----
        foreach (var box in scene.Boxes)
        {
            if (box.Kind is BoxKind.Ghost or BoxKind.Skipped) continue;
            foreach (var r in box.Rows)
                if (r.Value.IsRef)
                    scene.Arrows.Add(new Arrow(new Point(r.Rect.Right - 8, r.Rect.Top + RowH / 2), r.Value.Ref, r.Changed));
        }

        scene.Size = new Size(Math.Max(x, heapX + 200), bottom + Pad);
        return scene;
    }

    private Box HeapBox(HeapObject o, int index, int incoming)
    {
        var changed = _changedFields.GetValueOrDefault(o.Identity);
        var box = new Box
        {
            Kind = BoxKind.Heap, Header = $"#{o.Identity} {o.TypeName}", ObjectIndex = index,
            IsNew = _newObjects.Contains(o.Identity), Badge = incoming >= 2 ? $"参照 ×{incoming}" : null,
            Summary = o.Summary is null ? null : Trim(o.Summary), Hidden = o.Hidden,
            // 数や文字の配列は、メモリの中で隣どうしに並んでいる感じが出るように横に並べる
            Cells = o.Kind == HeapKind.Array && o.Fields.Length > 0 && o.Fields.All(f => !f.Value.IsRef && f.Value.Ref != MemValue.Omitted),
        };
        foreach (var f in o.Fields)
            box.Rows.Add(new Row { Name = f.Name, Text = ValueText(f.Value), Value = f.Value, Changed = changed?.Contains(f.Name) == true });
        box.SummaryChanged = changed?.Contains(SummaryKey) == true;
        return box;
    }

    private double CellWidth(Row r) => Math.Max(26, TextWidth(r.Text) + 10);

    /// <summary>箱の中身が入りきる幅。</summary>
    private double BoxMinWidth(Box box)
    {
        double header = TextWidth(box.Header, FontSize, bold: true) + 16 + (box.Badge is null ? 0 : TextWidth(box.Badge, SmallSize) + 14)
                        + (box.IsNew ? TextWidth("new", SmallSize) + 14 : 0);
        if (box.Cells) return Math.Max(header, box.Rows.Sum(CellWidth) + 12);
        box.NameW = box.Rows.Select(r => TextWidth(r.Name)).DefaultIfEmpty(0).Max() + 14;
        double rows = box.Rows.Select(r => box.NameW + TextWidth(r.Text) + (r.Value.IsRef ? 22 : 12)).DefaultIfEmpty(0).Max();
        double extra = Math.Max(box.Summary is null ? 0 : TextWidth(box.Summary) + 16, box.Hidden > 0 ? TextWidth($"…ほか {box.Hidden} 個", SmallSize) + 16 : 0);
        return Math.Max(header, Math.Max(rows, extra));
    }

    private void Place(Box box, double x, double y, double width)
    {
        if (box.NameW == 0) BoxMinWidth(box);
        if (box.Kind == BoxKind.Skipped)
        {
            box.Rect = new Rect(x, y, width, 20);
            return;
        }
        double h = HeaderH;
        if (box.Cells)
        {
            double cx = x + 6;
            foreach (var r in box.Rows)
            {
                double w = CellWidth(r);
                r.Rect = new Rect(cx, y + h + 4, w, CellH);
                cx += w;
            }
            h += 4 + CellH + 15;
        }
        else
        {
            foreach (var r in box.Rows)
            {
                r.Rect = new Rect(x, y + h, width, RowH);
                h += RowH;
            }
        }
        if (box.Summary is not null) h += RowH;
        if (box.Hidden > 0) h += 16;
        box.Rect = new Rect(x, y, width, h + 3);
    }

    protected override Size MeasureOverride(Size available)
    {
        _scene ??= BuildScene();
        return _shot is null ? new Size(0, 80) : _scene.Size;
    }

    // ======================= 描画 =======================

    protected override void OnRender(DrawingContext dc)
    {
        _scene ??= BuildScene();
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(ActualWidth, _scene.Size.Width), Math.Max(ActualHeight, _scene.Size.Height)));

        if (_shot is null)
        {
            dc.DrawText(Text(_message ?? "「変数ウォッチ」をオンにして実行すると、スタック（変数）とヒープ（オブジェクト）の様子をここに描きます。",
                13, SubTextBrush, face: LabelFace), new Point(Pad, Pad));
            return;
        }

        foreach (var (text, at) in _scene.Labels)
            dc.DrawText(Text(text, 12, SubTextBrush, face: LabelFace), at);
        if (_message is not null)
        {
            var m = Text(_message, 12, SubTextBrush);
            dc.DrawText(m, new Point(Math.Max(Pad, _scene.Size.Width - m.Width - Pad), Pad));
        }

        // 矢印は箱の上に描く（箱の下だと、列をまたぐ矢印が隠れてしまう）。変わった矢印をいちばん上に
        foreach (var box in _scene.Boxes) DrawBox(dc, box);
        foreach (var a in _scene.Arrows.OrderBy(a => a.Changed)) DrawArrow(dc, a);
    }

    private void DrawBox(DrawingContext dc, Box box)
    {
        var r = box.Rect;
        if (box.Kind == BoxKind.Skipped)
        {
            var t = Text(box.Header, SmallSize, SubTextBrush);
            dc.DrawText(t, new Point(r.Left + (r.Width - t.Width) / 2, r.Top + 2));
            return;
        }

        bool ghost = box.Kind == BoxKind.Ghost;
        var pen = ghost ? GhostPen : box.Current ? CurrentPen : box.IsNew ? NewPen : BoxPen;
        dc.DrawRoundedRectangle(ghost ? GhostBack : Brushes.White, pen, r, 4, 4);

        var headerBrush = ghost ? GhostBack : box.Kind switch
        {
            BoxKind.Frame => box.Current ? CurrentHeader : FrameHeader,
            BoxKind.Statics => StaticHeader,
            _ => HeapHeader,
        };
        dc.DrawRectangle(headerBrush, null, new Rect(r.Left + 1, r.Top + 1, r.Width - 2, HeaderH - 1));
        dc.DrawText(Text(box.Header, FontSize, ghost ? SubTextBrush : TextBrush, bold: true), new Point(r.Left + 6, r.Top + 3));

        double bx = r.Right - 6;
        if (box.Badge is not null) bx = DrawBadge(dc, box.Badge, bx, r.Top + 4, AliasBrush);
        if (box.IsNew) DrawBadge(dc, "new", bx, r.Top + 4, NewBrush);

        var valueBrush = ghost ? SubTextBrush : ValueBrush;
        var nameBrush = ghost ? SubTextBrush : NameBrush;
        if (box.Cells)
        {
            foreach (var row in box.Rows)
            {
                dc.DrawRectangle(row.Changed ? ChangedBack : Brushes.White, BoxPen, row.Rect);
                var v = Text(row.Text, FontSize, valueBrush);
                dc.DrawText(v, new Point(row.Rect.Left + (row.Rect.Width - v.Width) / 2, row.Rect.Top + 4));
                var idx = Text(row.Name.Trim('[', ']'), 10, SubTextBrush);
                dc.DrawText(idx, new Point(row.Rect.Left + (row.Rect.Width - idx.Width) / 2, row.Rect.Bottom + 1));
            }
        }
        else
        {
            foreach (var row in box.Rows)
            {
                var rr = row.Rect;
                if (row.Changed) dc.DrawRectangle(ChangedBack, null, new Rect(rr.Left + 1, rr.Top, rr.Width - 2, rr.Height));
                dc.DrawLine(RowPen, new Point(rr.Left + 1, rr.Top), new Point(rr.Right - 1, rr.Top));
                dc.DrawText(Text(row.Name, FontSize, nameBrush), new Point(rr.Left + 6, rr.Top + 2));
                if (row.Value.IsRef)
                {
                    // 参照：番号を薄く書いて、右端の点から矢印を出す
                    var t = Text(row.Text, SmallSize, SubTextBrush);
                    dc.DrawText(t, new Point(rr.Right - 16 - t.Width, rr.Top + 3));
                    dc.DrawEllipse(RefDot, null, new Point(rr.Right - 8, rr.Top + RowH / 2), 3.5, 3.5);
                }
                else
                {
                    var brush = row.Value.Ref == MemValue.Omitted ? SubTextBrush : valueBrush;
                    dc.DrawText(Text(row.Text, FontSize, brush), new Point(rr.Left + box.NameW, rr.Top + 2));
                }
            }
        }

        double y = box.Rows.Count > 0 && !box.Cells ? box.Rows[^1].Rect.Bottom : box.Cells ? r.Top + HeaderH + 4 + CellH + 15 : r.Top + HeaderH;
        if (box.Summary is not null)
        {
            if (box.SummaryChanged) dc.DrawRectangle(ChangedBack, null, new Rect(r.Left + 1, y, r.Width - 2, RowH));
            dc.DrawText(Text(box.Summary, FontSize, box.Kind == BoxKind.Frame ? SubTextBrush : valueBrush), new Point(r.Left + 6, y + 2));
            y += RowH;
        }
        if (box.Hidden > 0)
            dc.DrawText(Text($"…ほか {box.Hidden} 個", SmallSize, SubTextBrush), new Point(r.Left + 6, y + 1));
    }

    private double DrawBadge(DrawingContext dc, string text, double right, double top, Brush brush)
    {
        var t = Text(text, SmallSize, Brushes.White, face: LabelFace);
        var rect = new Rect(right - t.Width - 10, top, t.Width + 10, 15);
        dc.DrawRoundedRectangle(brush, null, rect, 7, 7);
        dc.DrawText(t, new Point(rect.Left + 5, top));
        return rect.Left - 4;
    }

    private void DrawArrow(DrawingContext dc, Arrow a)
    {
        if (_scene is null || !_scene.Heap.TryGetValue(a.Target, out var target)) return;
        var pen = a.Changed ? ChangedArrowPen : ArrowPen;
        var brush = a.Changed ? ChangedArrowBrush : ArrowBrush;
        var from = a.From;
        double ty = target.Rect.Top + HeaderH / 2;

        Point end, c1, c2;
        Vector dir;
        if (target.Rect.Left > from.X + 12)
        {
            // ふつう：右の列の箱の左端へ
            end = new Point(target.Rect.Left, ty);
            double dx = Math.Max(30, (end.X - from.X) / 2);
            c1 = new Point(from.X + dx, from.Y);
            c2 = new Point(end.X - dx, end.Y);
            dir = new Vector(1, 0);
        }
        else
        {
            // 同じ列か左の箱（循環する参照など）：右に回り込んで、箱の右端へ
            end = new Point(target.Rect.Right, ty);
            double out_ = Math.Max(from.X, end.X) + 40;
            c1 = new Point(out_, from.Y);
            c2 = new Point(out_, end.Y);
            dir = new Vector(-1, 0);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(from, false, false);
            g.BezierTo(c1, c2, end - dir * 6, true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);

        // 矢じり
        var normal = new Vector(-dir.Y, dir.X);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(end, true, true);
            g.LineTo(end - dir * 8 + normal * 4, true, false);
            g.LineTo(end - dir * 8 - normal * 4, true, false);
        }
        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }
}
