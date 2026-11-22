using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace HotaruCodeViz;

public enum HitKind { Statement, Condition, Expression }

/// <summary>計装ポイント1つ分の情報。Node は「元の」構文木のノード。</summary>
public sealed record HitInfo(SyntaxNode Node, HitKind Kind, string[] VarNames, string[] VarTypes, string Scope);

/// <summary>呼び出しの木に入れるメソッド1つ分の情報。Node は「元の」構文木の宣言。ArgNames は記録する引数の名前。</summary>
public sealed record CallTarget(SyntaxNode Node, string Name, string[] ArgNames, int ParameterCount);

public sealed class InstrumentOptions
{
    public bool Watch { get; set; } = true;
    public bool Expressions { get; set; } = true;

    /// <summary>コンパイルエラーの原因になったので包まない式。</summary>
    public HashSet<SyntaxNode> NoExpr { get; } = new();

    /// <summary>コンパイルエラーの原因になったので変数ウォッチしないステートメント。</summary>
    public HashSet<SyntaxNode> NoWatch { get; } = new();

    /// <summary>メソッドの入口・出口を計装して、呼び出しの木を記録する。</summary>
    public bool Calls { get; set; }

    /// <summary>コンパイルエラーの原因になったので、呼び出しの木に入れないメソッド。</summary>
    public HashSet<SyntaxNode> NoCall { get; } = new();

    /// <summary>条件付きブレークポイント：行（0 始まり）→ 条件式のテキスト。</summary>
    public Dictionary<int, string> BreakConditions { get; } = new();

    /// <summary>条件がその場所ではコンパイルできなかった（変数がスコープにないなど）ので、条件を差し込まない場所。</summary>
    public HashSet<SyntaxNode> NoBreakCondition { get; } = new();
}

/// <summary>
/// 構文木を書き換えて、実行位置・変数・式の値を Tracer に通知するコードを差し込む。
/// どこに差し込めるかの判断には SemanticModel（型や変数の情報）を使う。
/// </summary>
public sealed class Instrumenter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly InstrumentOptions _opt;

    public Dictionary<int, HitInfo> Map { get; } = new();

    /// <summary>条件付きブレークポイントの条件を差し込んだ計装ポイント ID → 行。</summary>
    public Dictionary<int, int> BreakConditionIds { get; } = new();

    public Instrumenter(SemanticModel model, InstrumentOptions options)
    {
        _model = model;
        _opt = options;
    }

    private int Register(HitInfo info)
    {
        int id = Map.Count;
        Map[id] = info;
        return id;
    }

    // ======================= ステートメント =======================

    private StatementSyntax MakeHit(StatementSyntax original)
    {
        var vars = _opt.Watch && !_opt.NoWatch.Contains(original) ? WatchVariables(original) : new List<WatchVar>();
        int id = Register(new HitInfo(original, HitKind.Statement,
            vars.Select(v => v.Name).ToArray(), vars.Select(v => v.Type).ToArray(), ScopeName(original.SpanStart)));

        var hit = vars.Count == 0
            ? ParseStatement($"global::HotaruCodeViz.Tracer.Hit({id});")
            : ParseStatement($"global::HotaruCodeViz.Tracer.Hit({id}, new object[] {{ {string.Join(", ", vars.Select(v => v.Access))} }});");

        // 条件付きブレークポイントの行なら { Tracer.BreakIf(id, () => (条件)); Tracer.Hit(id); } にする
        if (BreakConditionLambda(original, id) is { } lambda)
        {
            var breakIf = ExpressionStatement(InvocationExpression(
                ParseExpression("global::HotaruCodeViz.Tracer.BreakIf"),
                ArgumentList(SeparatedList(new[]
                {
                    Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(id))),
                    Argument(lambda),
                }))));
            return Block(breakIf, hit);
        }
        return hit;
    }

    /// <summary>node の行に条件付きブレークポイントがあれば () => (条件) を作る。</summary>
    private LambdaExpressionSyntax? BreakConditionLambda(SyntaxNode node, int id)
    {
        if (_opt.BreakConditions.Count == 0 || _opt.NoBreakCondition.Contains(node)) return null;
        int line = node.GetLocation().GetLineSpan().StartLinePosition.Line;
        if (!_opt.BreakConditions.TryGetValue(line, out var text)) return null;

        var condition = ParseExpression(text);
        if (condition.ContainsDiagnostics) return null;

        BreakConditionIds[id] = line;
        return ParenthesizedLambdaExpression(ParenthesizedExpression(condition));
    }

    private static bool ShouldTrace(StatementSyntax s) =>
        s is not (BlockSyntax or LocalFunctionStatementSyntax or EmptyStatementSyntax);

    private SyntaxList<StatementSyntax> InstrumentList(SyntaxList<StatementSyntax> statements)
    {
        var list = new List<StatementSyntax>();
        foreach (var s in statements)
        {
            if (ShouldTrace(s)) list.Add(MakeHit(s));
            list.Add((StatementSyntax)Visit(s)!);
        }
        return List(list);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node) =>
        node.WithStatements(InstrumentList(node.Statements));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node) =>
        node.WithStatements(InstrumentList(node.Statements));

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var members = new List<MemberDeclarationSyntax>();
        foreach (var m in node.Members)
        {
            if (m is GlobalStatementSyntax gs && ShouldTrace(gs.Statement))
                members.Add(GlobalStatement(MakeHit(gs.Statement)));
            members.Add((MemberDeclarationSyntax)Visit(m)!);
        }
        return node.WithMembers(List(members));
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        // if (x) Foo(); のような { } なしの埋め込みステートメントは { Hit; Foo(); } に包む
        if (node is StatementSyntax s && s is not BlockSyntax && IsEmbedded(s))
        {
            var hit = MakeHit(s);
            var inner = (StatementSyntax)base.Visit(s)!;
            return Block(hit, inner);
        }

        var visited = base.Visit(node);

        // 式を Tracer.Expr(id, 式) で包む
        if (_opt.Expressions && node is ExpressionSyntax e && visited is ExpressionSyntax ve && ShouldWrap(e))
        {
            int id = Register(new HitInfo(e, HitKind.Expression, [], [], ""));
            return TracerCall("Expr", id, ve);
        }
        return visited;
    }

    private static bool IsEmbedded(StatementSyntax s) => s.Parent is
        IfStatementSyntax or ElseClauseSyntax or WhileStatementSyntax or DoStatementSyntax or
        ForStatementSyntax or CommonForEachStatementSyntax or UsingStatementSyntax or
        LockStatementSyntax or FixedStatementSyntax;

    private static InvocationExpressionSyntax TracerCall(string name, int id, ExpressionSyntax value) =>
        InvocationExpression(
            ParseExpression("global::HotaruCodeViz.Tracer." + name),
            ArgumentList(SeparatedList(new[]
            {
                Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(id))),
                Argument(ParenthesizedExpression(value)),
            })));

    // ======================= 条件式 =======================

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var v = (IfStatementSyntax)base.VisitIfStatement(node)!;
        return v.WithCondition(WrapCondition(node.Condition, v.Condition));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var v = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return v.WithCondition(WrapCondition(node.Condition, v.Condition));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var v = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return v.WithCondition(WrapCondition(node.Condition, v.Condition));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var v = (ForStatementSyntax)base.VisitForStatement(node)!;
        return node.Condition is null ? v : v.WithCondition(WrapCondition(node.Condition, v.Condition!));
    }

    private ExpressionSyntax WrapCondition(ExpressionSyntax original, ExpressionSyntax visited)
    {
        // while (true) を包むと「ループを抜けうる」と判断されてコンパイルエラーになりうる。
        // x is string s / out var v を包むと変数の確定代入が崩れる。どちらも包まない。
        if (_model.GetConstantValue(original).HasValue) return visited;
        if (original.DescendantNodesAndSelf().Any(n => n is SingleVariableDesignationSyntax)) return visited;

        int id = Register(new HitInfo(original, HitKind.Condition, [], [], ""));
        var call = TracerCall("Cond", id, visited);

        // 条件付きブレークポイント：Tracer.Cond(id, (条件式), () => (止まる条件))
        if (BreakConditionLambda(original, id) is { } lambda)
            call = call.AddArgumentListArguments(Argument(lambda));
        return call;
    }

    // ======================= 呼び出しの木（メソッドの入口・出口） =======================
    //
    //   int __call3 = Tracer.Enter(3, new object[] { 引数… });
    //   try { …本体…（return x; は return Tracer.Return<戻り値の型>(__call3, x);）}
    //   catch (Exception) when (Tracer.Fail(__call3)) { throw; }   // 例外で抜けたことの記録（捕まえない）
    //   finally { Tracer.Exit(__call3); }

    public Dictionary<int, CallTarget> Methods { get; } = new();

    private sealed record CallContext(int Id, string? ReturnType, string[] ArgAccess)
    {
        public string Variable => "__call" + Id;
    }

    // 今書き換えているメソッド（ラムダの中では null：ラムダの return は書き換えない）
    private readonly Stack<CallContext?> _calls = new();

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var ctx = BeginCall(node, node.Identifier.ValueText);
        _calls.Push(ctx);
        var v = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        _calls.Pop();
        if (ctx is null) return v;
        return v.WithBody(WrapBody(ctx, v.Body, v.ExpressionBody))
            .WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var ctx = node.Modifiers.Any(SyntaxKind.StaticKeyword) ? null : BeginCall(node, "new " + node.Identifier.ValueText);
        _calls.Push(ctx);
        var v = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        _calls.Pop();
        if (ctx is null) return v;
        return v.WithBody(WrapBody(ctx, v.Body, v.ExpressionBody))
            .WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var ctx = BeginCall(node, node.Identifier.ValueText);
        _calls.Push(ctx);
        var v = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        _calls.Pop();
        if (ctx is null) return v;
        return v.WithBody(WrapBody(ctx, v.Body, v.ExpressionBody))
            .WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => InLambda(() => base.VisitParenthesizedLambdaExpression(node));
    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => InLambda(() => base.VisitSimpleLambdaExpression(node));
    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => InLambda(() => base.VisitAnonymousMethodExpression(node));

    private SyntaxNode? InLambda(Func<SyntaxNode?> visit)
    {
        _calls.Push(null);
        try { return visit(); }
        finally { _calls.Pop(); }
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        var v = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
        if (_calls.Count > 0 && _calls.Peek() is { ReturnType: not null } ctx && v.Expression is { } e and not RefExpressionSyntax)
            return v.WithExpression(ReturnCall(ctx, e));
        return v;
    }

    /// <summary>呼び出しの木に入れられるメソッドなら登録して、書き換えに使う情報を返す。</summary>
    private CallContext? BeginCall(SyntaxNode decl, string name)
    {
        if (!_opt.Calls || _opt.NoCall.Contains(decl)) return null;
        if (decl is BaseMethodDeclarationSyntax { Body: null, ExpressionBody: null }) return null;
        if (decl is LocalFunctionStatementSyntax { Body: null, ExpressionBody: null }) return null;
        if (_model.GetDeclaredSymbol(decl) is not IMethodSymbol m) return null;

        // async・yield・ref 戻り値・ref 構造体（Span など）を返すものは対象外
        if (m.IsAsync || m.ReturnsByRef || m.ReturnsByRefReadonly || ContainsYield(decl)) return null;
        if (!m.ReturnsVoid && (m.ReturnType.IsRefLikeType ||
            m.ReturnType.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Error)) return null;

        // out 引数（まだ値がない）や ref 構造体の引数は記録しない
        var args = m.Parameters.Where(p => p.RefKind != RefKind.Out && Watchable(p.Type)).ToList();
        int id = Methods.Count;
        Methods[id] = new CallTarget(decl, name, args.Select(p => p.Name).ToArray(), m.Parameters.Length);
        string? returnType = m.ReturnsVoid ? null : m.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new CallContext(id, returnType, args.Select(p => Escape(p.Name)).ToArray());
    }

    /// <summary>yield return / yield break を含む（イテレーター）か。中のラムダやローカル関数の分は数えない。</summary>
    private static bool ContainsYield(SyntaxNode decl) =>
        decl.DescendantNodes(n => n == decl || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .OfType<YieldStatementSyntax>().Any();

    private static ExpressionSyntax ReturnCall(CallContext ctx, ExpressionSyntax value) =>
        InvocationExpression(
            ParseExpression($"global::HotaruCodeViz.Tracer.Return<{ctx.ReturnType}>"),
            ArgumentList(SeparatedList(new[] { Argument(IdentifierName(ctx.Variable)), Argument(value) })));

    private static BlockSyntax WrapBody(CallContext ctx, BlockSyntax? body, ArrowExpressionClauseSyntax? arrow)
    {
        // => 式 はブロックに直す
        body ??= Block(arrow!.Expression switch
        {
            ThrowExpressionSyntax t => ThrowStatement(t.Expression),
            var e when ctx.ReturnType is null => ExpressionStatement(e),
            var e => ReturnStatement(ReturnCall(ctx, e)),
        });

        string args = ctx.ArgAccess.Length == 0 ? "null" : $"new object[] {{ {string.Join(", ", ctx.ArgAccess)} }}";
        var enter = ParseStatement($"int {ctx.Variable} = global::HotaruCodeViz.Tracer.Enter({ctx.Id}, {args});");
        var tryStatement = (TryStatementSyntax)ParseStatement(
            $"try {{ }} catch (global::System.Exception) when (global::HotaruCodeViz.Tracer.Fail({ctx.Variable})) {{ throw; }} " +
            $"finally {{ global::HotaruCodeViz.Tracer.Exit({ctx.Variable}); }}");
        return Block(enter, tryStatement.WithBlock(body.WithoutTrivia()));
    }

    private static bool IsConditionRoot(ExpressionSyntax e) => e.Parent switch
    {
        IfStatementSyntax s => s.Condition == e,
        WhileStatementSyntax s => s.Condition == e,
        DoStatementSyntax s => s.Condition == e,
        ForStatementSyntax s => s.Condition == e,
        _ => false,
    };

    // ======================= 変数ウォッチ =======================

    private sealed record WatchVar(string Name, string Type, string Access, int Order);

    private List<WatchVar> WatchVariables(StatementSyntax s)
    {
        var result = new List<WatchVar>();
        try
        {
            int pos = s.SpanStart;
            var enclosing = _model.GetEnclosingSymbol(pos);
            if (enclosing is null) return result;

            var flow = _model.AnalyzeDataFlow(s);
            if (flow is null || !flow.Succeeded) return result;
            var assigned = new HashSet<ISymbol>(flow.DefinitelyAssignedOnEntry, SymbolEqualityComparer.Default);

            foreach (var sym in _model.LookupSymbols(pos))
            {
                // 今いる関数自身のローカル変数・引数だけ（外側の変数をキャプチャするとコンパイルエラーの元になる）
                if (!SymbolEqualityComparer.Default.Equals(sym.ContainingSymbol, enclosing)) continue;
                switch (sym)
                {
                    case ILocalSymbol l when assigned.Contains(l) && Watchable(l.Type):
                        result.Add(new WatchVar(l.Name, TypeName(l.Type), Escape(l.Name), 1000 + Position(l)));
                        break;
                    case IParameterSymbol p when (p.RefKind != RefKind.Out || assigned.Contains(p)) && Watchable(p.Type):
                        result.Add(new WatchVar(p.Name, TypeName(p.Type), Escape(p.Name), p.Ordinal));
                        break;
                }
            }

            // フィールド（競技プログラミングでよくある static フィールドも見えるように）
            bool staticContext = false;
            ISymbol? cur = enclosing;
            for (; cur is IMethodSymbol m; cur = m.ContainingSymbol)
                if (m.IsStatic) staticContext = true;

            if (cur is INamedTypeSymbol t)
            {
                foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
                {
                    if (f.IsImplicitlyDeclared || !Watchable(f.Type)) continue;
                    if (!f.IsStatic && (staticContext || t.TypeKind != TypeKind.Class)) continue;
                    var access = f.IsStatic
                        ? $"{t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{Escape(f.Name)}"
                        : $"this.{Escape(f.Name)}";
                    result.Add(new WatchVar(f.Name, TypeName(f.Type), access, 100000 + Position(f)));
                }
            }
        }
        catch
        {
            result.Clear();
        }
        return result.OrderBy(v => v.Order).ToList();
    }

    private static int Position(ISymbol s) => s.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0;

    private static string TypeName(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string Escape(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static bool Watchable(ITypeSymbol t) =>
        t.TypeKind is not (TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer) &&
        !t.IsRefLikeType && t.SpecialType != SpecialType.System_Void;

    private string ScopeName(int position)
    {
        for (var sym = _model.GetEnclosingSymbol(position); sym is not null; sym = sym.ContainingSymbol)
        {
            if (sym is not IMethodSymbol m) continue;
            switch (m.MethodKind)
            {
                case MethodKind.AnonymousFunction: continue;
                case MethodKind.LocalFunction: return m.Name;
                default:
                    if (m.Name == WellKnownMemberNames.TopLevelStatementsEntryPointMethodName) return "トップレベル";
                    return $"{m.ContainingType.Name}.{m.Name}";
            }
        }
        return "";
    }

    // ======================= 式の値 =======================

    private static bool IsWrapKind(ExpressionSyntax e) => e switch
    {
        BinaryExpressionSyntax => true,
        PrefixUnaryExpressionSyntax u => !u.IsKind(SyntaxKind.AddressOfExpression) &&
                                         !u.IsKind(SyntaxKind.PointerIndirectionExpression) &&
                                         !u.IsKind(SyntaxKind.IndexExpression),
        PostfixUnaryExpressionSyntax u => !u.IsKind(SyntaxKind.SuppressNullableWarningExpression),
        InvocationExpressionSyntax => true,
        ElementAccessExpressionSyntax => true,
        MemberAccessExpressionSyntax m => m.IsKind(SyntaxKind.SimpleMemberAccessExpression),
        ConditionalExpressionSyntax c => c.WhenTrue is not RefExpressionSyntax,
        CastExpressionSyntax => true,
        AssignmentExpressionSyntax => true,
        AwaitExpressionSyntax => true,
        InterpolatedStringExpressionSyntax => true,
        SwitchExpressionSyntax => true,
        IsPatternExpressionSyntax => true,
        ConditionalAccessExpressionSyntax => true,
        _ => false,
    };

    private bool ShouldWrap(ExpressionSyntax e)
    {
        if (_opt.NoExpr.Contains(e) || !IsWrapKind(e) || IsConditionRoot(e)) return false;
        if (e.Ancestors().Any(a => a is AttributeSyntax)) return false;
        if (IsInsideNameof(e) || IsLValue(e) || IsInConditionalAccessChain(e)) return false;

        var p = e.Parent;
        // new { x.Y } や (x.Y, ...) は名前が推論されるので包むと名前が変わってしまう
        if (p is AnonymousObjectMemberDeclaratorSyntax { NameEquals: null }) return false;
        if (p is ArgumentSyntax { NameColon: null, Parent: TupleExpressionSyntax } && e is MemberAccessExpressionSyntax) return false;

        if (e is AssignmentExpressionSyntax asg)
        {
            if (p is InitializerExpressionSyntax) return false;                 // new P { X = 1 }
            if (asg.Left is TupleExpressionSyntax or DeclarationExpressionSyntax) return false; // (a, b) = ...
            if (asg.Right is RefExpressionSyntax) return false;
            if (asg.Left is IdentifierNameSyntax { Identifier.ValueText: "_" }) return false;
        }

        if (HasDesignationThatNeedsFlow(e)) return false;

        var type = _model.GetTypeInfo(e).Type;
        if (type is null || !Watchable(type) || type.TypeKind == TypeKind.Dynamic) return false;
        if (_model.GetConstantValue(e).HasValue) return false;

        if (e is MemberAccessExpressionSyntax &&
            _model.GetSymbolInfo(e).Symbol is not (IPropertySymbol or IFieldSymbol)) return false;

        // 「式の形」によって決まる変換（定数・ラムダ・null・target-typed など）が絡むものは包まない
        var conv = _model.GetConversion(e);
        if (!conv.Exists) return false;
        if (!conv.IsIdentity &&
            (!conv.IsImplicit || conv.IsInterpolatedString || conv.IsInterpolatedStringHandler ||
             conv.IsStackAlloc || conv.IsMethodGroup || conv.IsAnonymousFunction ||
             conv.IsCollectionExpression || conv.IsConditionalExpression || conv.IsSwitchExpression ||
             conv.IsDefaultLiteral || conv.IsNullLiteral || conv.IsTupleLiteralConversion || conv.IsObjectCreation))
            return false;

        // 値型をレシーバーとして包むとコピーに対して操作することになるので包まない
        if (!type.IsReferenceType && IsReceiver(e)) return false;

        return true;
    }

    private static bool IsReceiver(ExpressionSyntax e) => e.Parent switch
    {
        MemberAccessExpressionSyntax m => m.Expression == e,
        ElementAccessExpressionSyntax a => a.Expression == e,
        ConditionalAccessExpressionSyntax c => c.Expression == e,
        _ => false,
    };

    private static bool IsLValue(ExpressionSyntax e) => e.Parent switch
    {
        AssignmentExpressionSyntax a => a.Left == e,
        PrefixUnaryExpressionSyntax u => u.IsKind(SyntaxKind.PreIncrementExpression) ||
                                         u.IsKind(SyntaxKind.PreDecrementExpression) ||
                                         u.IsKind(SyntaxKind.AddressOfExpression),
        PostfixUnaryExpressionSyntax u => u.IsKind(SyntaxKind.PostIncrementExpression) ||
                                          u.IsKind(SyntaxKind.PostDecrementExpression),
        ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None) => true,
        ArgumentSyntax { Parent: TupleExpressionSyntax t } => IsLValue(t),
        RefExpressionSyntax => true,
        ParenthesizedExpressionSyntax par => IsLValue(par),
        _ => false,
    };

    private static bool IsInsideNameof(ExpressionSyntax e) =>
        e.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    // a?.b.c の「.b.c」の部分は単独では式として取り出せない
    private static bool IsInConditionalAccessChain(ExpressionSyntax e)
    {
        for (SyntaxNode n = e; n.Parent is not null; n = n.Parent)
        {
            if (n is ArgumentSyntax or AnonymousFunctionExpressionSyntax or StatementSyntax) return false;
            if (n.Parent is ConditionalAccessExpressionSyntax ca && ca.WhenNotNull == n) return true;
        }
        return false;
    }

    // x is int n や a && Foo(out var v) を包むと「確定代入」が崩れてコンパイルエラーになる
    private static bool HasDesignationThatNeedsFlow(ExpressionSyntax e)
    {
        foreach (var d in e.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax).OfType<VariableDesignationSyntax>())
        {
            if (d is DiscardDesignationSyntax) continue;
            if (e is InvocationExpressionSyntax inv &&
                d.Parent is DeclarationExpressionSyntax { Parent: ArgumentSyntax arg } &&
                arg.Parent?.Parent == inv) continue; // Foo(out var v) の直接の引数は OK
            return true;
        }
        return false;
    }
}
