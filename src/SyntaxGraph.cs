using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HotaruCodeViz;

/// <summary>色や表示名など、ツリーとグラフで共通の見た目。</summary>
public static class Palette
{
    public static readonly Brush Statement = Freeze(Color.FromRgb(0x15, 0x65, 0xC0));
    public static readonly Brush Expression = Freeze(Color.FromRgb(0x8E, 0x24, 0xAA));
    public static readonly Brush Declaration = Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    public static readonly Brush OtherNode = Freeze(Color.FromRgb(0x37, 0x47, 0x4F));
    public static readonly Brush Token = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
    public static readonly Brush SnippetText = Freeze(Color.FromRgb(0x61, 0x61, 0x61));
    public static readonly Brush Value = Freeze(Color.FromRgb(0xD8, 0x43, 0x15));
    public static readonly Brush Edge = Freeze(Color.FromRgb(0xB0, 0xBE, 0xC5));
    public static readonly Brush CollapsedBack = Freeze(Color.FromRgb(0xEC, 0xEF, 0xF1));

    public static readonly Color Amber = Color.FromRgb(0xFF, 0xB3, 0x00);
    public static readonly Color ExprPurple = Color.FromRgb(0xCE, 0x93, 0xD8);
    public static readonly Color TrueGreen = Color.FromRgb(0x43, 0xC0, 0x4A);
    public static readonly Color FalseRed = Color.FromRgb(0xF4, 0x43, 0x36);

    public static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush KindBrush(SyntaxNode node) => node switch
    {
        StatementSyntax => Statement,
        ExpressionSyntax => Expression,
        MemberDeclarationSyntax or VariableDeclarationSyntax or VariableDeclaratorSyntax or ParameterSyntax => Declaration,
        _ => OtherNode,
    };

    public static string ShortKind(SyntaxNode node) => node.Kind().ToString()
        .Replace("Expression", "Expr").Replace("Statement", "Stmt").Replace("Declaration", "Decl");

    public static string Snippet(SyntaxNode node, int max = 40)
    {
        var text = node.ToString();
        var firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length > max) return firstLine[..max] + "…";
        return text.Contains('\n') ? firstLine + " …" : firstLine;
    }

    /// <summary>トレイル（直前に実行したノード）の色。0 が最新。</summary>
    public static Brush TrailBrush(int index, Color latest)
    {
        var c = index == 0 ? latest : Amber;
        byte alpha = index == 0 ? (byte)255 : (byte)Math.Max(20, 110 - index * 22);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}

/// <summary>構文木を Canvas 上にグラフとして描く。</summary>
public sealed class SyntaxGraph
{
    private const double CharWidth = 6.7;
    private const double NodeHeight = 50;
    private const double HGap = 10;
    private const double VGap = 36;
    private const double Pad = 20;
    private const int TrailLength = 5;

    private sealed class LayoutNode
    {
        public required SyntaxNode Node;
        public required bool Collapsed;
        public List<LayoutNode> Children = new();
        public string Kind = "", Snippet = "";
        public double Width, SubtreeWidth, X, Y;
    }

    private sealed class NodeVisual
    {
        public required Border Border;
        public required TextBlock Value;
        public required Brush NormalBackground;
        public double X, Y, Width;
    }

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private Dictionary<SyntaxNode, NodeVisual> _visuals = new();
    private readonly List<NodeVisual> _trail = new();

    // 一度作ったグラフは使い回す（ループで文を行き来するたびに作り直さない）
    private sealed record CachedGraph(List<UIElement> Elements, Dictionary<SyntaxNode, NodeVisual> Visuals, double Width, double Height);
    private readonly Dictionary<(SyntaxNode Root, bool Collapse), CachedGraph> _cache = new();

    private Point? _dragStart;
    private Point _dragOffset;
    private bool _dragged;

    public SyntaxNode? Root { get; private set; }
    public bool Collapse { get; private set; }

    public event Action<SyntaxNode>? NodeClicked;
    public event Action? ZoomChanged;

    public SyntaxGraph(ScrollViewer scroll)
    {
        _scroll = scroll;
        _canvas.LayoutTransform = _scale;
        _canvas.Background = Brushes.Transparent;
        _scroll.Content = _canvas;

        _scroll.PreviewMouseWheel += OnWheel;
        _scroll.PreviewMouseLeftButtonDown += OnMouseDown;
        _scroll.PreviewMouseMove += OnMouseMove;
        _scroll.PreviewMouseLeftButtonUp += OnMouseUp;
    }

    public double Zoom
    {
        get => _scale.ScaleX;
        set
        {
            var z = Math.Clamp(value, 0.15, 3.0);
            if (Math.Abs(z - _scale.ScaleX) < 0.0001) return;
            _scale.ScaleX = _scale.ScaleY = z;
            ZoomChanged?.Invoke();
        }
    }

    // ======================= レイアウト =======================

    /// <param name="collapseStatements">true なら root 以外のステートメントを畳んで1つの箱にする</param>
    public void Show(SyntaxNode root, bool collapseStatements, Func<SyntaxNode, string?> valueOf)
    {
        ResetTrail();
        Root = root;
        Collapse = collapseStatements;
        _canvas.Children.Clear();

        if (_cache.TryGetValue((root, collapseStatements), out var cached))
        {
            foreach (var el in cached.Elements) _canvas.Children.Add(el);
            _visuals = cached.Visuals;
            _canvas.Width = cached.Width;
            _canvas.Height = cached.Height;
            foreach (var node in _visuals.Keys) SetValue(node, valueOf(node));
            return;
        }

        _visuals = new();
        var layout = Build(root, isRoot: true);
        Measure(layout);
        int maxDepth = 0;
        Place(layout, Pad, 0, ref maxDepth);

        _canvas.Width = layout.SubtreeWidth + Pad * 2;
        _canvas.Height = (maxDepth + 1) * (NodeHeight + VGap) - VGap + Pad * 2;

        var all = new List<LayoutNode>();
        Collect(layout, all);
        foreach (var n in all)
            foreach (var c in n.Children)
                _canvas.Children.Add(new Line
                {
                    X1 = n.X + n.Width / 2, Y1 = n.Y + NodeHeight,
                    X2 = c.X + c.Width / 2, Y2 = c.Y,
                    Stroke = Palette.Edge, StrokeThickness = 1.3,
                });
        foreach (var n in all)
            AddNodeVisual(n, valueOf(n.Node));

        _cache[(root, collapseStatements)] = new CachedGraph(
            _canvas.Children.Cast<UIElement>().ToList(), _visuals, _canvas.Width, _canvas.Height);
    }

    /// <summary>構文木が新しくなったら呼ぶ。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>グラフの代わりにメッセージを表示する。</summary>
    public void ShowMessage(string message)
    {
        ResetTrail();
        Root = null;
        _canvas.Children.Clear();
        _visuals = new();
        var text = new TextBlock { Text = message, Foreground = Palette.SnippetText, FontSize = 14, TextWrapping = TextWrapping.Wrap, Width = 560 };
        Canvas.SetLeft(text, Pad);
        Canvas.SetTop(text, Pad);
        _canvas.Children.Add(text);
        _canvas.Width = 600;
        _canvas.Height = 120;
    }

    public void ResetTrail()
    {
        foreach (var v in _trail)
        {
            v.Border.Background = v.NormalBackground;
            v.Border.BorderThickness = new Thickness(1.5);
        }
        _trail.Clear();
    }

    private LayoutNode Build(SyntaxNode node, bool isRoot)
    {
        bool collapsed = Collapse && !isRoot && node is StatementSyntax;
        var l = new LayoutNode { Node = node, Collapsed = collapsed };
        l.Kind = Palette.ShortKind(node);
        l.Snippet = SnippetFor(node, collapsed);
        if (!collapsed)
            foreach (var child in node.ChildNodes())
                l.Children.Add(Build(child, false));
        return l;
    }

    private static string SnippetFor(SyntaxNode n, bool collapsed)
    {
        if (collapsed || !n.ChildNodes().Any()) return Palette.Snippet(n, 22);
        return n switch
        {
            BinaryExpressionSyntax b => b.OperatorToken.Text,
            AssignmentExpressionSyntax a => a.OperatorToken.Text,
            PrefixUnaryExpressionSyntax p => p.OperatorToken.Text,
            PostfixUnaryExpressionSyntax p => p.OperatorToken.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            BaseTypeDeclarationSyntax t => t.Identifier.Text,
            LocalFunctionStatementSyntax f => f.Identifier.Text,
            VariableDeclaratorSyntax v => v.Identifier.Text,
            ParameterSyntax p => p.Identifier.Text,
            ForEachStatementSyntax f => f.Identifier.Text,
            _ => "",
        };
    }

    private static void Measure(LayoutNode n)
    {
        n.Width = Math.Max(Math.Max(n.Kind.Length, n.Snippet.Length), 8) * CharWidth + 16;
        double children = 0;
        foreach (var c in n.Children)
        {
            Measure(c);
            children += c.SubtreeWidth;
        }
        if (n.Children.Count > 1) children += HGap * (n.Children.Count - 1);
        n.SubtreeWidth = Math.Max(n.Width, children);
    }

    private static void Place(LayoutNode n, double left, int depth, ref int maxDepth)
    {
        maxDepth = Math.Max(maxDepth, depth);
        n.X = left + (n.SubtreeWidth - n.Width) / 2;
        n.Y = Pad + depth * (NodeHeight + VGap);

        double childrenWidth = n.Children.Sum(c => c.SubtreeWidth) + HGap * Math.Max(0, n.Children.Count - 1);
        double x = left + (n.SubtreeWidth - childrenWidth) / 2;
        foreach (var c in n.Children)
        {
            Place(c, x, depth + 1, ref maxDepth);
            x += c.SubtreeWidth + HGap;
        }
    }

    private static void Collect(LayoutNode n, List<LayoutNode> list)
    {
        list.Add(n);
        foreach (var c in n.Children) Collect(c, list);
    }

    private void AddNodeVisual(LayoutNode n, string? value)
    {
        var kind = new TextBlock
        {
            Text = n.Kind, FontWeight = FontWeights.SemiBold, Foreground = Palette.KindBrush(n.Node),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var snippet = new TextBlock
        {
            Text = n.Snippet, Foreground = Palette.SnippetText, HorizontalAlignment = HorizontalAlignment.Center,
            FontStyle = n.Collapsed ? FontStyles.Italic : FontStyles.Normal,
        };
        var valueText = new TextBlock
        {
            Foreground = Palette.Value, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(kind);
        panel.Children.Add(snippet);
        panel.Children.Add(valueText);

        var normal = n.Collapsed ? Palette.CollapsedBack : Brushes.White;
        var border = new Border
        {
            Width = n.Width, Height = NodeHeight,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Palette.KindBrush(n.Node),
            Background = normal,
            Child = panel,
            Tag = n.Node,
            ToolTip = Palette.Snippet(n.Node, 200),
        };
        Canvas.SetLeft(border, n.X);
        Canvas.SetTop(border, n.Y);
        _canvas.Children.Add(border);

        var visual = new NodeVisual { Border = border, Value = valueText, NormalBackground = normal, X = n.X, Y = n.Y, Width = n.Width };
        _visuals[n.Node] = visual;
        SetValue(n.Node, value);
    }

    // ======================= 値とハイライト =======================

    public void SetValue(SyntaxNode node, string? value)
    {
        if (!_visuals.TryGetValue(node, out var v)) return;
        v.Value.Text = value is null ? "" : "→ " + value;
        v.Value.ToolTip = value;
    }

    public void Highlight(SyntaxNode node, Color color)
    {
        if (!_visuals.TryGetValue(node, out var v)) return;

        _trail.Remove(v);
        _trail.Insert(0, v);
        while (_trail.Count > TrailLength)
        {
            var old = _trail[^1];
            old.Border.Background = old.NormalBackground;
            old.Border.BorderThickness = new Thickness(1.5);
            _trail.RemoveAt(_trail.Count - 1);
        }
        for (int i = 0; i < _trail.Count; i++)
        {
            _trail[i].Border.Background = Palette.TrailBrush(i, color);
            _trail[i].Border.BorderThickness = new Thickness(i == 0 ? 3 : 1.5);
        }

        _scroll.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ScrollTo(v));
    }

    private void ScrollTo(NodeVisual v)
    {
        double z = Zoom;
        double cx = (v.X + v.Width / 2) * z, cy = (v.Y + NodeHeight / 2) * z;
        // 見えているなら動かさない（ガタガタしないように）
        bool visibleX = cx > _scroll.HorizontalOffset + 40 && cx < _scroll.HorizontalOffset + _scroll.ViewportWidth - 40;
        bool visibleY = cy > _scroll.VerticalOffset + 30 && cy < _scroll.VerticalOffset + _scroll.ViewportHeight - 30;
        if (!visibleX) _scroll.ScrollToHorizontalOffset(cx - _scroll.ViewportWidth / 2);
        if (!visibleY) _scroll.ScrollToVerticalOffset(cy - _scroll.ViewportHeight / 2);
    }

    // ======================= マウス操作 =======================

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        Zoom *= e.Delta > 0 ? 1.1 : 1 / 1.1;
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // スクロールバー上の操作は邪魔しない
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(d) is not null) return;
        _dragStart = e.GetPosition(_scroll);
        _dragOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _dragged = false;
        _scroll.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_scroll);
        var delta = p - start;
        if (!_dragged && Math.Abs(delta.X) + Math.Abs(delta.Y) < 4) return;
        _dragged = true;
        _scroll.Cursor = Cursors.SizeAll;
        _scroll.ScrollToHorizontalOffset(_dragOffset.X - delta.X);
        _scroll.ScrollToVerticalOffset(_dragOffset.Y - delta.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _scroll.ReleaseMouseCapture();
        _scroll.Cursor = null;
        if (!_dragged)
        {
            var hit = _canvas.InputHitTest(e.GetPosition(_canvas)) as DependencyObject;
            if (hit is not null && FindAncestor<Border>(hit) is { Tag: SyntaxNode node }) NodeClicked?.Invoke(node);
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? cur = d; cur is not null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }
}
