using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace HotaruCodeViz;

public enum FlowBlockKind { Entry, Exit, Block }

/// <summary>辺の種類：ふつうの流れ / 条件が true のとき / false のとき。</summary>
public enum FlowEdgeKind { Always, WhenTrue, WhenFalse }

/// <summary>制御フローグラフの基本ブロック（途中で分かれも合流もしない、ひと続きの処理）。</summary>
public sealed class FlowBlock
{
    public required int Index { get; init; }              // グラフの中の番号（= Roslyn の Ordinal、だいたいソースの順）
    public required int Global { get; init; }             // プログラム全体での番号（FlowTracker の配列の添字）
    public required FlowBlockKind Kind { get; init; }
    public required string[] Lines { get; init; }         // 中の処理（文や式の 1 行目）
    public string? Branch { get; init; }                  // 最後の分岐（"if (x > 0)" "return x" など）
    public bool Reachable { get; init; } = true;
    public bool Throws { get; init; }                     // throw で抜ける
    public int FirstLine { get; init; } = -1;             // ソースの行（0 始まり、-1 = なし）
    public string? Region { get; init; }                  // catch / finally の中なら、その印
    public int Layer { get; set; }                        // 絵の段（上から 0, 1, …）
    public List<int> Out { get; } = new();                // 出ていく辺（グラフの中の番号）
    public List<int> In { get; } = new();
    internal TextSpan[] Spans { get; init; } = [];        // 中の処理の場所（計装ポイントとの対応付けに使う）

    /// <summary>中の処理がソースのどこからどこまでか（なければ null）。</summary>
    public TextSpan? SourceSpan => Spans.Length == 0 ? null : TextSpan.FromBounds(Spans.Min(s => s.Start), Spans.Max(s => s.End));
}

/// <param name="Back">上（前のブロック）へ戻る辺（ループ）</param>
public sealed record FlowEdge(int Index, int Global, int From, int To, FlowEdgeKind Kind, bool Back);

/// <summary>メソッド 1 つ分（トップレベルのステートメント・ローカル関数・ラムダもそれぞれ 1 つ）の制御フローグラフ。</summary>
public sealed class FlowGraph
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required SyntaxNode Root { get; init; }
    public required FlowBlock[] Blocks { get; init; }
    public required FlowEdge[] Edges { get; init; }
    public int Entry => 0;
    public int Exit => Blocks.Length - 1;
    public int LayerCount => Blocks.Length == 0 ? 0 : Blocks.Max(b => b.Layer) + 1;
}

/// <summary>
/// プログラム全体の制御フローグラフ（Roslyn の ControlFlowGraph から作る）と、計装ポイント → ブロックの対応。
/// 計装ポイントは「元の」構文木のノードなので、同じ構文木から作ること。
/// </summary>
public sealed class FlowProgram
{
    public FlowGraph[] Graphs { get; }

    /// <summary>計装ポイント ID → (グラフ, グラフの中のブロック)。文と条件だけ（式は対応付けない）。</summary>
    public Dictionary<int, (int Graph, int Block)> HitBlocks { get; } = new();

    /// <summary>計装ポイント ID → ブロックの中での順番（ソースの位置）。</summary>
    public Dictionary<int, int> HitOrder { get; } = new();

    public int BlockCount { get; }
    public int EdgeCount { get; }

    private FlowProgram(List<FlowGraph> graphs)
    {
        Graphs = graphs.ToArray();
        BlockCount = graphs.Sum(g => g.Blocks.Length);
        EdgeCount = graphs.Sum(g => g.Edges.Length);
    }

    public (FlowGraph Graph, FlowBlock Block) BlockAt(int global)
    {
        foreach (var g in Graphs)
        {
            if (g.Blocks.Length > 0 && global >= g.Blocks[0].Global && global <= g.Blocks[^1].Global)
                return (g, g.Blocks[global - g.Blocks[0].Global]);
        }
        throw new ArgumentOutOfRangeException(nameof(global));
    }

    /// <summary>node を含む、いちばん内側のグラフ。</summary>
    public FlowGraph? GraphFor(SyntaxNode node)
    {
        FlowGraph? best = null;
        foreach (var g in Graphs)
            if (g.Root.SyntaxTree == node.SyntaxTree && g.Root.Span.Contains(node.Span) && (best is null || g.Root.Span.Length < best.Root.Span.Length))
                best = g;
        return best;
    }

    // ======================= 作る =======================

    /// <param name="map">計装ポイント（なければ対応付けしない）</param>
    public static FlowProgram Build(SyntaxTree tree, SemanticModel model, IReadOnlyDictionary<int, HitInfo>? map = null)
    {
        var builder = new Builder(tree.GetText());
        var root = tree.GetRoot();

        if (root is CompilationUnitSyntax cu && cu.Members.Any(m => m is GlobalStatementSyntax))
            builder.TryAdd(cu, model, "トップレベルのステートメント");

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseMethodDeclarationSyntax m when m.Body is not null || m.ExpressionBody is not null:
                    builder.TryAdd(m, model, MemberName(m, model));
                    break;
                case AccessorDeclarationSyntax a when a.Body is not null || a.ExpressionBody is not null:
                    builder.TryAdd(a, model, MemberName(a, model));
                    break;
                case ArrowExpressionClauseSyntax arrow when arrow.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                    builder.TryAdd(arrow, model, MemberName(arrow.Parent, model) + ".get");
                    break;
            }
        }

        var program = new FlowProgram(builder.Graphs);
        if (map is not null) program.MapHits(map);
        return program;
    }

    private static string MemberName(SyntaxNode decl, SemanticModel model)
    {
        var symbol = model.GetDeclaredSymbol(decl);
        if (symbol is null) return "?";
        string type = symbol.ContainingType?.Name ?? "";
        string name = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "new " + type,
            IMethodSymbol { MethodKind: MethodKind.StaticConstructor } => "static " + type,
            IMethodSymbol { AssociatedSymbol: { } p } m => p.Name + "." + (m.MethodKind == MethodKind.PropertySet ? "set" : m.MethodKind == MethodKind.PropertyGet ? "get" : m.Name),
            _ => symbol.Name,
        };
        return string.IsNullOrEmpty(type) || name.StartsWith("new ") || name.StartsWith("static ") ? name : type + "." + name;
    }

    private sealed class Builder(SourceText text)
    {
        public readonly List<FlowGraph> Graphs = new();
        private int _blocks, _edges;

        public void TryAdd(SyntaxNode node, SemanticModel model, string name)
        {
            ControlFlowGraph? cfg;
            try
            {
                cfg = ControlFlowGraph.Create(node, model);
            }
            catch
            {
                return; // 作れない形のもの（エラーのあるコードなど）は描かない
            }
            if (cfg is not null) Add(cfg, node, name);
        }

        private void Add(ControlFlowGraph cfg, SyntaxNode root, string name)
        {
            var graph = Convert(cfg, root, name);
            Graphs.Add(graph);

            // 中のローカル関数とラムダ（{ } の本体があるもの）
            foreach (var f in cfg.LocalFunctions)
            {
                try
                {
                    var sub = cfg.GetLocalFunctionControlFlowGraph(f);
                    var syntax = f.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                    if (syntax is not null) Add(sub, syntax, f.Name + "（ローカル関数）");
                }
                catch { }
            }
            foreach (var block in cfg.Blocks)
            {
                var ops = block.Operations.AsEnumerable();
                if (block.BranchValue is not null) ops = ops.Append(block.BranchValue);
                foreach (var lambda in ops.SelectMany(o => o.DescendantsAndSelf()).OfType<IFlowAnonymousFunctionOperation>())
                {
                    if (lambda.Syntax is not AnonymousFunctionExpressionSyntax { Block: not null }) continue;
                    try
                    {
                        var sub = cfg.GetAnonymousFunctionControlFlowGraph(lambda);
                        int line = text.Lines.GetLineFromPosition(lambda.Syntax.SpanStart).LineNumber + 1;
                        Add(sub, lambda.Syntax, $"ラムダ（{line} 行目）");
                    }
                    catch { }
                }
            }
        }

        private FlowGraph Convert(ControlFlowGraph cfg, SyntaxNode root, string name)
        {
            int baseBlock = _blocks, baseEdge = _edges;
            var blocks = new FlowBlock[cfg.Blocks.Length];
            var edges = new List<FlowEdge>();

            for (int i = 0; i < cfg.Blocks.Length; i++)
            {
                var b = cfg.Blocks[i];
                var spans = b.Operations.Select(o => o.Syntax.Span).ToList();
                if (b.BranchValue is not null) spans.Add(b.BranchValue.Syntax.Span);
                int first = spans.Count == 0 ? -1 : text.Lines.GetLineFromPosition(spans.Min(s => s.Start)).LineNumber;
                var region = RegionLabel(b, cfg);
                bool cleanup = region?.EndsWith("の後片付け") == true;

                blocks[i] = new FlowBlock
                {
                    Index = i,
                    Global = baseBlock + i,
                    Kind = b.Kind switch { BasicBlockKind.Entry => FlowBlockKind.Entry, BasicBlockKind.Exit => FlowBlockKind.Exit, _ => FlowBlockKind.Block },
                    Lines = DescribeOperations(b, cleanup),
                    Branch = DescribeBranch(b, cleanup),
                    Reachable = b.IsReachable,
                    Throws = b.FallThroughSuccessor?.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow,
                    FirstLine = first,
                    Region = region,
                    Spans = spans.ToArray(),
                };
            }

            void AddEdge(BasicBlock from, ControlFlowBranch? branch, FlowEdgeKind kind)
            {
                if (branch?.Destination is not { } to) return; // throw や finally の終わり（行き先がない）
                int index = edges.Count;
                edges.Add(new FlowEdge(index, baseEdge + index, from.Ordinal, to.Ordinal, kind, to.Ordinal <= from.Ordinal));
                blocks[from.Ordinal].Out.Add(index);
                blocks[to.Ordinal].In.Add(index);
            }

            foreach (var b in cfg.Blocks)
            {
                if (b.ConditionKind == ControlFlowConditionKind.None)
                {
                    AddEdge(b, b.FallThroughSuccessor, FlowEdgeKind.Always);
                }
                else
                {
                    // ConditionalSuccessor は、BranchValue が ConditionKind（true か false）のときに進む先
                    bool whenTrue = b.ConditionKind == ControlFlowConditionKind.WhenTrue;
                    AddEdge(b, b.ConditionalSuccessor, whenTrue ? FlowEdgeKind.WhenTrue : FlowEdgeKind.WhenFalse);
                    AddEdge(b, b.FallThroughSuccessor, whenTrue ? FlowEdgeKind.WhenFalse : FlowEdgeKind.WhenTrue);
                }
            }

            _blocks += blocks.Length;
            _edges += edges.Count;
            var graph = new FlowGraph { Id = Graphs.Count, Name = name, Root = root, Blocks = blocks, Edges = edges.ToArray() };
            AssignLayers(graph);
            return graph;
        }
    }

    // ======================= ブロックの中身の説明 =======================

    private const int MaxLineText = 48;

    private static string[] DescribeOperations(BasicBlock b, bool cleanup)
    {
        var shown = new List<SyntaxNode>();
        var special = new List<(int At, string Text)>();
        void Special(int at, string text)
        {
            if (!special.Any(x => x.Text == text)) special.Add((at, text));
        }

        foreach (var op in b.Operations)
        {
            // foreach・using・lock の ( ) の中の、コンパイラが作った処理（列挙子・次の要素・後片付け）は言葉で書く
            if (HeaderOf(op.Syntax) is { } header)
            {
                Special(op.Syntax.SpanStart, HeaderOperationText(op, header, cleanup));
                continue;
            }
            if (op.IsImplicit && CalledNames(op).Contains("Dispose"))
            {
                Special(op.Syntax.SpanStart, "（後片付け：Dispose）"); // using var の後片付け
                continue;
            }
            // switch (x) の x
            if (op.Syntax.Parent is SwitchStatementSyntax sw && sw.Expression == op.Syntax)
            {
                Special(op.Syntax.SpanStart, OneLine($"switch ({sw.Expression})"));
                continue;
            }
            // 条件演算子・?? などで分かれた片方の値は、その値だけ
            var node = op is IFlowCaptureOperation ? op.Syntax : DisplayNode(op.Syntax);
            // 同じ文の一部は 1 回だけ
            if (shown.Any(s => s.Span.Contains(node.Span))) continue;
            shown.RemoveAll(s => node.Span.Contains(s.Span));
            shown.Add(node);
        }
        return shown.Select(n => (At: n.SpanStart, Text: OneLine(n.ToString()))).Concat(special)
            .OrderBy(x => x.At).Select(x => x.Text).ToArray();
    }

    /// <summary>syntax が foreach・using・lock の ( ) の部分（本体ではない）なら、その文。</summary>
    private static StatementSyntax? HeaderOf(SyntaxNode syntax)
    {
        foreach (var a in syntax.AncestorsAndSelf())
        {
            switch (a)
            {
                case CommonForEachStatementSyntax fe: return fe.Statement.Span.Contains(syntax.Span) ? null : fe;
                case UsingStatementSyntax us: return us.Statement.Span.Contains(syntax.Span) ? null : us;
                case LockStatementSyntax ls: return ls.Statement.Span.Contains(syntax.Span) ? null : ls;
                case StatementSyntax or MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax: return null;
            }
        }
        return null;
    }

    private static string ForEachText(CommonForEachStatementSyntax fe) => fe switch
    {
        ForEachStatementSyntax f => OneLine($"foreach ({f.Type} {f.Identifier.Text} in {f.Expression})"),
        ForEachVariableStatementSyntax v => OneLine($"foreach ({v.Variable} in {v.Expression})"),
        _ => "foreach",
    };

    private static List<string> CalledNames(IOperation op) =>
        op.DescendantsAndSelf().OfType<IInvocationOperation>().Select(i => i.TargetMethod.Name).ToList();

    private static string HeaderOperationText(IOperation op, StatementSyntax header, bool cleanup)
    {
        var names = CalledNames(op);
        switch (header)
        {
            case CommonForEachStatementSyntax fe:
                if (cleanup || names.Contains("Dispose") || names.Contains("DisposeAsync")) return "（列挙の後片付け）";
                if (names.Contains("GetEnumerator") || names.Contains("GetAsyncEnumerator")) return OneLine($"（{fe.Expression} の列挙を始める）");
                string variable = fe is ForEachStatementSyntax f ? f.Identifier.Text : ((ForEachVariableStatementSyntax)fe).Variable.ToString();
                return OneLine($"{variable} = 次の要素");
            case LockStatementSyntax ls:
                if (names.Contains("Exit")) return "（lock を放す）";
                if (names.Contains("Enter")) return OneLine($"（lock ({ls.Expression}) を取る）");
                return cleanup ? "（lock を放す）" : OneLine(ls.Expression.ToString());
            default:
                if (cleanup || names.Contains("Dispose") || names.Contains("DisposeAsync")) return "（後片付け：Dispose）";
                return OneLine(DisplayNode(op.Syntax).ToString());
        }
    }

    /// <summary>catch / finally の中のブロックなら、その印。</summary>
    private static string? RegionLabel(BasicBlock b, ControlFlowGraph cfg)
    {
        for (var r = b.EnclosingRegion; r is not null; r = r.EnclosingRegion)
        {
            switch (r.Kind)
            {
                case ControlFlowRegionKind.Catch:
                    return "catch";
                case ControlFlowRegionKind.Filter:
                    return "when";
                case ControlFlowRegionKind.Finally:
                    // foreach・using・lock が作った finally（後片付け）か
                    StatementSyntax? owner = null;
                    bool generated = true;
                    for (int i = r.FirstBlockOrdinal; i <= r.LastBlockOrdinal && generated; i++)
                    {
                        var ops = cfg.Blocks[i].Operations.AsEnumerable();
                        if (cfg.Blocks[i].BranchValue is { } v) ops = ops.Append(v);
                        foreach (var o in ops)
                        {
                            var h = HeaderOf(o.Syntax);
                            if (h is null || (owner is not null && owner != h)) { generated = false; break; }
                            owner = h;
                        }
                    }
                    if (!generated || owner is null) return "finally";
                    return owner switch
                    {
                        CommonForEachStatementSyntax => "foreach の後片付け",
                        LockStatementSyntax => "lock の後片付け",
                        _ => "using の後片付け",
                    };
            }
        }
        return null;
    }

    /// <summary>処理の構文を、見せる単位（ふつうの文なら文全体、for の ( ) の中なら式だけ）まで広げる。</summary>
    private static SyntaxNode DisplayNode(SyntaxNode syntax)
    {
        var n = syntax;
        for (var p = syntax.Parent; p is not null; p = p.Parent)
        {
            if (p is AnonymousFunctionExpressionSyntax or MemberDeclarationSyntax or CompilationUnitSyntax) break;
            if (p is StatementSyntax st)
            {
                if (st is LocalDeclarationStatementSyntax or ExpressionStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax or YieldStatementSyntax)
                    n = st;
                break;
            }
            if (p is ArgumentListSyntax or ArgumentSyntax or ExpressionSyntax or VariableDeclarationSyntax or VariableDeclaratorSyntax or EqualsValueClauseSyntax)
                n = p;
            else
                break;
        }
        return n;
    }

    private static string? DescribeBranch(BasicBlock b, bool cleanup)
    {
        if (b.BranchValue is not { } value) return null;
        var syntax = value.Syntax;
        string text = OneLine(syntax.ToString());

        switch (b.FallThroughSuccessor?.Semantics)
        {
            case ControlFlowBranchSemantics.Return:
                return syntax.FirstAncestorOrSelf<ReturnStatementSyntax>() is not null || syntax.Parent is ArrowExpressionClauseSyntax
                    ? "return " + text : text;
            case ControlFlowBranchSemantics.Throw:
                return "throw " + text;
        }
        if (b.ConditionKind == ControlFlowConditionKind.None) return text;

        if (HeaderOf(syntax) is { } header)
        {
            var names = CalledNames(value);
            return header is CommonForEachStatementSyntax fe && !cleanup && (names.Contains("MoveNext") || names.Contains("MoveNextAsync"))
                ? ForEachText(fe) + " 次がある?" : "（後片付けが要る?）";
        }
        if (value is IIsNullOperation) return text + " が null?";

        // 条件の分岐：どの文の条件かを書く
        var owner = syntax.AncestorsAndSelf().FirstOrDefault(a => a is StatementSyntax or SwitchLabelSyntax or SwitchExpressionArmSyntax
            or ConditionalExpressionSyntax or AnonymousFunctionExpressionSyntax or MemberDeclarationSyntax);
        return owner switch
        {
            IfStatementSyntax => $"if ({text})",
            WhileStatementSyntax => $"while ({text})",
            DoStatementSyntax => $"do … while ({text})",
            ForStatementSyntax => $"for (…; {text}; …)",
            SwitchLabelSyntax label => OneLine(label.ToString().TrimEnd(':')) + " ?",
            SwitchExpressionArmSyntax arm => OneLine(arm.Pattern + (arm.WhenClause is null ? "" : " " + arm.WhenClause)) + " ?",
            _ => text + " ?",
        };
    }

    private static string OneLine(string s)
    {
        s = string.Join(" ", s.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return s.Length > MaxLineText ? s[..MaxLineText] + "…" : s;
    }

    // ======================= 段の決め方 =======================

    /// <summary>
    /// ブロックを上から下へ段に並べる。前から来る辺の元より 1 段下に置く（戻る辺 = ループは数えない）。
    /// ループを抜けた先は、ループの中のいちばん下の段より下に置く（ループの本体と横並びにならないように）。
    /// </summary>
    private static void AssignLayers(FlowGraph g)
    {
        var blocks = g.Blocks;
        if (blocks.Length == 0) return;

        // ループ：戻る辺 s → t があれば、番号 t..s のブロックがループの中
        var loops = g.Edges.Where(e => e.Back).Select(e => (Head: e.To, Tail: e.From)).ToList();

        for (int i = 0; i < blocks.Length; i++)
        {
            var b = blocks[i];
            if (i == 0) { b.Layer = 0; continue; }

            int layer = -1;
            foreach (var ei in b.In)
            {
                var e = g.Edges[ei];
                if (e.Back) continue;
                int from = blocks[e.From].Layer;
                foreach (var (head, tail) in loops)
                {
                    // ループの中から外へ出る辺
                    if (e.From >= head && e.From <= tail && (i < head || i > tail))
                        for (int k = head; k <= tail && k < i; k++) from = Math.Max(from, blocks[k].Layer);
                }
                layer = Math.Max(layer, from + 1);
            }
            // 前から来る辺がない（到達できないブロックや finally の中など）：1 つ前のブロックの下
            if (layer < 0) layer = blocks[i - 1].Layer + 1;
            b.Layer = layer;
        }

        // 終わりのブロックはいちばん下
        int max = blocks.Take(blocks.Length - 1).Select(b => b.Layer).DefaultIfEmpty(0).Max();
        blocks[^1].Layer = Math.Max(blocks[^1].Layer, max + 1);
    }

    // ======================= 計装ポイントとの対応 =======================

    private void MapHits(IReadOnlyDictionary<int, HitInfo> map)
    {
        foreach (var (id, info) in map)
        {
            if (info.Kind == HitKind.Expression) continue;
            var node = info.Node;
            var graph = GraphFor(node);
            if (graph is null) continue;
            int block = FindBlock(graph, node.Span);
            if (block < 0) continue;
            HitBlocks[id] = (graph.Id, block);
            HitOrder[id] = node.SpanStart;
        }
    }

    /// <summary>
    /// 文（や条件）が始まるブロック：中の処理が文の範囲に入っているブロックのうち、いちばん前のもの。
    /// なければ、文を含む処理のあるブロック（範囲がいちばん小さいもの）。
    /// </summary>
    private static int FindBlock(FlowGraph g, TextSpan span)
    {
        foreach (var b in g.Blocks)
            if (b.Spans.Any(s => span.Contains(s))) return b.Index;

        int best = -1, bestLength = int.MaxValue;
        foreach (var b in g.Blocks)
            foreach (var s in b.Spans)
                if (s.Contains(span) && s.Length < bestLength) { best = b.Index; bestLength = s.Length; }
        return best;
    }
}

/// <summary>
/// 実行中に、どのブロックを何回通ったか・どの辺を何回通ったかを数える（ユーザーのコードのスレッドで呼ばれる）。
/// 計装ポイントのないブロック（for の i++ やループの条件の続きなど）は、前後の計装ポイントのあいだの道筋から補う。
/// 呼び出しごと（Tracer.Enter / Exit）に「最後にいたブロック」を覚えるので、再帰でも道筋が混ざらない。
/// </summary>
public sealed class FlowTracker
{
    private readonly FlowProgram _program;
    private readonly int[] _hitBlock;   // 計装ポイント ID → 全体のブロック番号（-1 = なし）
    private readonly int[] _hitOrder;
    private readonly int[] _blockGraph; // 全体のブロック番号 → グラフ
    private readonly int[] _blockLocal; // 全体のブロック番号 → グラフの中の番号
    private readonly bool[] _observable; // 計装ポイントのあるブロック
    private readonly Dictionary<long, Route?> _paths = new();
    private readonly List<Dictionary<int, (int Block, int Order)>> _frames = [new()];
    private int _thread = -1;

    /// <summary>全体のブロック番号 → 通った回数。</summary>
    public long[] BlockCounts { get; }

    /// <summary>全体の辺の番号 → 通った回数。</summary>
    public long[] EdgeCounts { get; }

    /// <summary>全体のブロック番号 → 最後にそのブロックへ来たときの道筋（全体の辺の番号）。</summary>
    public int[]?[] LastPathInto { get; }

    public FlowTracker(FlowProgram program, int hitCount)
    {
        _program = program;
        _hitBlock = Enumerable.Repeat(-1, hitCount).ToArray();
        _hitOrder = new int[hitCount];
        _blockGraph = new int[program.BlockCount];
        _blockLocal = new int[program.BlockCount];
        _observable = new bool[program.BlockCount];
        BlockCounts = new long[program.BlockCount];
        EdgeCounts = new long[program.EdgeCount];
        LastPathInto = new int[]?[program.BlockCount];

        foreach (var g in program.Graphs)
            foreach (var b in g.Blocks)
            {
                _blockGraph[b.Global] = g.Id;
                _blockLocal[b.Global] = b.Index;
            }
        foreach (var (id, (graph, block)) in program.HitBlocks)
        {
            if ((uint)id >= (uint)hitCount) continue;
            int global = program.Graphs[graph].Blocks[block].Global;
            _hitBlock[id] = global;
            _hitOrder[id] = program.HitOrder[id];
            _observable[global] = true;
        }
    }

    private bool Owns()
    {
        int me = Environment.CurrentManagedThreadId;
        if (_thread == me) return true;
        if (_thread != -1) return false;
        _thread = me;
        return true;
    }

    /// <summary>文・条件の計装ポイントを通った。</summary>
    public void Visit(int id)
    {
        if ((uint)id >= (uint)_hitBlock.Length) return;
        int b = _hitBlock[id];
        if (b < 0 || !Owns()) return;

        var frame = _frames[^1];
        int g = _blockGraph[b];
        int order = _hitOrder[id];
        if (frame.TryGetValue(g, out var last))
        {
            if (last.Block != b) Move(last.Block, b);
            else if (order <= last.Order) Move(b, b); // 同じブロックにもう一度入った（ループ）
        }
        else
        {
            Move(EntryOf(g), b);
        }
        frame[g] = (b, order);
    }

    /// <summary>メソッドに入った（Tracer.Enter）。</summary>
    public void Push()
    {
        if (Owns()) _frames.Add(new());
    }

    /// <summary>メソッドから出た（Tracer.Exit）。最後のブロックから終わりのブロックまでの道筋を数える。</summary>
    public void Pop()
    {
        if (!Owns() || _frames.Count <= 1) return;
        Finish(_frames[^1]);
        _frames.RemoveAt(_frames.Count - 1);
    }

    /// <summary>プログラムが最後まで終わった。</summary>
    public void Finish()
    {
        if (!Owns()) return;
        for (int i = _frames.Count - 1; i >= 0; i--) Finish(_frames[i]);
        _frames.Clear();
        _frames.Add(new());
    }

    private void Finish(Dictionary<int, (int Block, int Order)> frame)
    {
        foreach (var (g, last) in frame)
        {
            int exit = ExitOf(g);
            var path = Path(last.Block, exit);
            if (path is null) continue;
            Count(path);
            BlockCounts[exit]++;
        }
    }

    private int EntryOf(int g) => _program.Graphs[g].Blocks[0].Global;

    private int ExitOf(int g) => _program.Graphs[g].Blocks[^1].Global;

    private void Move(int from, int to)
    {
        var path = Path(from, to);
        if (path is not null)
        {
            Count(path);
            if (from == EntryOf(_blockGraph[to])) BlockCounts[from]++;
        }
        BlockCounts[to]++;
        LastPathInto[to] = path?.Edges;
    }

    /// <summary>道筋の辺と、途中で通った（計装ポイントのない）ブロックを数える。</summary>
    private void Count(Route path)
    {
        foreach (var e in path.Edges) EdgeCounts[e]++;
        foreach (var b in path.Blocks) BlockCounts[b]++;
    }

    /// <summary>道筋：必ず通ったと言える辺と、途中のブロック（どちらも全体の番号）。</summary>
    private sealed record Route(int[] Edges, int[] Blocks);

    /// <summary>
    /// from から to への道筋。途中で通れるのは計装ポイントのないブロックだけ
    /// （計装ポイントのあるブロックを通っていたら、そこで Visit が呼ばれているはず）。
    /// いちばん短い道筋を通ったとみなす。短い道筋がいくつもあるとき（x ? a : b の a と b など、
    /// 計装ポイントがなくて見分けられない）は、どの道筋でも通る辺とブロックだけを返す（推測で間違った道を光らせない）。
    /// たどり着けなければ null。
    /// </summary>
    private Route? Path(int from, int to)
    {
        long key = ((long)from << 32) | (uint)to;
        if (_paths.TryGetValue(key, out var cached)) return cached;
        var result = _blockGraph[to] == _blockGraph[from] ? FindPath(from, to) : null;
        _paths[key] = result;
        return result;
    }

    private Route? FindPath(int from, int to)
    {
        var graph = _program.Graphs[_blockGraph[from]];
        int baseBlock = graph.Blocks[0].Global;
        int n = graph.Blocks.Length;
        int start = _blockLocal[from], goal = n; // 行き先は番号 n の別の点にする（from == to のループでも同じに扱える）
        int goalBlock = _blockLocal[to];

        // 通れる辺：start か計装ポイントのないブロックから、行き先か計装ポイントのないブロックへ
        var edges = new List<(int U, int V, FlowEdge E)>();
        foreach (var e in graph.Edges)
        {
            bool fromOk = e.From == start || (!_observable[baseBlock + e.From] && e.From != goalBlock);
            if (!fromOk) continue;
            int v = e.To == goalBlock ? goal : e.To;
            if (v != goal && (v == start || _observable[baseBlock + v])) continue;
            edges.Add((e.From, v, e));
        }

        // 前から（start からの距離と、最短の道筋の数）と、後ろから（行き先までの距離と数）
        var (ds, cs) = Bfs(n + 1, start, edges.Select(x => (x.U, x.V)));
        if (ds[goal] < 0) return null;
        var (dg, cg) = Bfs(n + 1, goal, edges.Select(x => (x.V, x.U)));
        int length = ds[goal];
        long total = cs[goal];

        bool OnAll(int u, int v) =>
            ds[u] >= 0 && dg[v] >= 0 && ds[u] + 1 + dg[v] == length && Mul(cs[u], cg[v]) == total;

        var certain = edges.Where(x => OnAll(x.U, x.V)).OrderBy(x => ds[x.U]).Select(x => x.E.Global).ToArray();
        var blocks = Enumerable.Range(0, n)
            .Where(b => b != start && ds[b] > 0 && dg[b] > 0 && ds[b] + dg[b] == length && Mul(cs[b], cg[b]) == total)
            .Select(b => baseBlock + b).ToArray();
        return new Route(certain, blocks);
    }

    private const long CountCap = 1L << 40;

    private static long Saturate(long v) => v < 0 || v > CountCap ? CountCap : v;

    private static long Mul(long a, long b) => a != 0 && b > CountCap / a ? CountCap : a * b;

    /// <summary>幅優先で、各点までの距離（-1 = 届かない）と最短の道筋の数を求める。</summary>
    private static (int[] Dist, long[] Count) Bfs(int size, int source, IEnumerable<(int From, int To)> edges)
    {
        var adj = new List<int>[size];
        foreach (var (a, b) in edges) (adj[a] ??= new List<int>()).Add(b);
        var dist = new int[size];
        var count = new long[size];
        Array.Fill(dist, -1);
        dist[source] = 0;
        count[source] = 1;
        var queue = new Queue<int>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            if (adj[u] is not { } next) continue;
            foreach (int v in next)
            {
                if (dist[v] < 0)
                {
                    dist[v] = dist[u] + 1;
                    queue.Enqueue(v);
                }
                if (dist[v] == dist[u] + 1) count[v] = Saturate(count[v] + count[u]);
            }
        }
        return (dist, count);
    }
}
