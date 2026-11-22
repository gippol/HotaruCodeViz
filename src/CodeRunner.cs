using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HotaruCodeViz;

/// <param name="BreakConditionIds">条件付きブレークポイントの条件を差し込めた計装ポイント ID → 行</param>
/// <param name="ConditionErrors">使えなかった条件（行 → 理由）</param>
/// <param name="Methods">呼び出しの木に入れたメソッド（Tracer.Enter の ID → 情報）</param>
public sealed record BuildResult(
    byte[]? Bytes, Dictionary<int, HitInfo> Map, List<string> Errors, List<string> Notes,
    Dictionary<int, int>? BreakConditionIds = null, Dictionary<int, string>? ConditionErrors = null,
    Dictionary<int, CallTarget>? Methods = null);

/// <summary>Roslyn でメモリ上にコンパイルして実行する。</summary>
public static class CodeRunner
{
    public static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    private const string GlobalUsings =
        "global using System; global using System.Collections.Generic; global using System.Linq; " +
        "global using System.Text; global using System.Threading.Tasks;";

    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p);
                return name.StartsWith("System.") || name is "System" or "mscorlib" or "netstandard" or "Microsoft.CSharp";
            })
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Tracer).Assembly.Location))
            .ToArray());

    public static CSharpCompilation CreateCompilation(SyntaxTree tree) =>
        CSharpCompilation.Create(
            "UserCode_" + Guid.NewGuid().ToString("N"),
            new[] { tree, CSharpSyntaxTree.ParseText(GlobalUsings, ParseOptions) },
            References.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug, allowUnsafe: true));

    public static string Format(Diagnostic d)
    {
        var pos = d.Location.GetLineSpan().StartLinePosition;
        return $"  ({pos.Line + 1},{pos.Character + 1}) {d.Id}: {d.GetMessage()}";
    }

    /// <summary>
    /// 元のコードをチェック → 計装 → コンパイル。
    /// 計装後のコードがコンパイルできなかったら、原因の式やウォッチを外して作り直す。
    /// </summary>
    /// <param name="breakConditions">条件付きブレークポイント（行 → 条件式）</param>
    /// <param name="calls">メソッドの入口・出口を計装して呼び出しの木を記録する</param>
    public static BuildResult Build(SyntaxTree original, bool watch, bool expressions,
        IReadOnlyDictionary<int, string>? breakConditions = null, bool calls = false)
    {
        var notes = new List<string>();
        var compilation = CreateCompilation(original);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            return new BuildResult(null, new(), errors.Select(Format).ToList(), notes);

        var model = compilation.GetSemanticModel(original);
        var opt = new InstrumentOptions { Watch = watch, Expressions = expressions, Calls = calls };
        var conditionErrors = new Dictionary<int, string>();
        foreach (var (line, text) in breakConditions ?? new Dictionary<int, string>())
        {
            var parsed = SyntaxFactory.ParseExpression(text);
            if (parsed.ContainsDiagnostics)
                conditionErrors[line] = "式として読めません: " + parsed.GetDiagnostics().First().GetMessage();
            else
                opt.BreakConditions[line] = text;
        }
        int excluded = 0;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            Instrumenter ins;
            CSharpSyntaxNode root;
            try
            {
                ins = new Instrumenter(model, opt);
                root = (CSharpSyntaxNode)ins.Visit(original.GetRoot())!;
            }
            catch (Exception ex) when (opt.Expressions || opt.Calls || opt.Watch)
            {
                notes.Add($"計装中のエラー（{ex.GetType().Name}）のため一部機能を無効にしました");
                if (opt.Expressions) opt.Expressions = false;
                else if (opt.Calls) opt.Calls = false;
                else opt.Watch = false;
                continue;
            }

            var tree = CSharpSyntaxTree.Create(root, ParseOptions);
            using var ms = new MemoryStream();
            var emit = CreateCompilation(tree).Emit(ms);
            if (emit.Success)
            {
                if (excluded > 0) notes.Add($"コンパイルの都合で {excluded} 箇所の計装を外しました");

                // どの場所にも差し込めなかった条件を知らせる
                // （for の行の「文の手前」では i がまだないが「ループの条件」では使える、のように一部で使えればよい）
                var usedLines = ins.BreakConditionIds.Values.ToHashSet();
                foreach (var line in usedLines) conditionErrors.Remove(line);
                foreach (var line in opt.BreakConditions.Keys)
                    if (!usedLines.Contains(line) && !conditionErrors.ContainsKey(line))
                        conditionErrors[line] = "この行には止まれる場所（文やループの条件）がありません";

                return new BuildResult(ms.ToArray(), ins.Map, new(), notes, ins.BreakConditionIds, conditionErrors, ins.Methods);
            }

            // エラーの場所から、原因になった Tracer.Expr / Tracer.Hit を探して外す
            var diags = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            bool progressed = false;
            var newRoot = tree.GetRoot();
            foreach (var d in diags)
            {
                if (d.Location.SourceTree != tree) continue;
                var node = newRoot.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
                foreach (var inv in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    if (!TryGetTracerCall(inv, out var name, out var id)) continue;

                    // 呼び出しの木の計装（Enter / Return / Fail / Exit）が原因 → そのメソッドは木に入れない
                    if (name is "Enter" or "Return" or "Fail" or "Exit")
                    {
                        if (ins.Methods.TryGetValue(id, out var target) && opt.NoCall.Add(target.Node)) { progressed = true; excluded++; }
                        break;
                    }

                    if (!ins.Map.TryGetValue(id, out var info)) continue;

                    // 条件付きブレークポイントの条件が原因 → その場所では条件を使わない（ほかの場所で使えればよい）
                    bool inCondition = name == "BreakIf"
                        || (name == "Cond" && inv.ArgumentList.Arguments.Count == 3
                            && inv.ArgumentList.Arguments[2].FullSpan.Contains(d.Location.SourceSpan));
                    if (inCondition)
                    {
                        if (opt.NoBreakCondition.Add(info.Node))
                        {
                            progressed = true;
                            if (ins.BreakConditionIds.TryGetValue(id, out var condLine))
                                conditionErrors.TryAdd(condLine, ConditionHint(d));
                        }
                        break;
                    }

                    if (name == "Expr" && opt.NoExpr.Add(info.Node)) { progressed = true; excluded++; }
                    else if (name == "Hit" && opt.NoWatch.Add(info.Node)) { progressed = true; excluded++; }
                    break;
                }
            }

            if (progressed) continue;
            if (opt.Expressions)
            {
                opt.Expressions = false;
                notes.Add("式の値の表示を無効にしました（計装後のコードがコンパイルできなかったため）");
            }
            else if (opt.Calls)
            {
                opt.Calls = false;
                notes.Add("呼び出しの木を無効にしました（計装後のコードがコンパイルできなかったため）");
            }
            else if (opt.Watch)
            {
                opt.Watch = false;
                notes.Add("変数ウォッチを無効にしました（計装後のコードがコンパイルできなかったため）");
            }
            else
            {
                return new BuildResult(null, ins.Map,
                    diags.Select(d => "（計装後）" + Format(d)).Prepend("計装後のコードがコンパイルできませんでした").ToList(), notes);
            }
        }
        return new BuildResult(null, new(), new() { "計装のやり直し回数の上限に達しました" }, notes);
    }

    /// <summary>条件がコンパイルできなかった理由に、よくあるものは日本語のヒントを付ける。</summary>
    private static string ConditionHint(Diagnostic d) => d.Id switch
    {
        "CS0103" => "その場所では使えない名前です（スコープの外か、まだ宣言されていない）。foreach の変数はループの中の行で使ってください — " + d.GetMessage(),
        "CS0029" or "CS0266" => "条件は bool（true / false）になる式にしてください — " + d.GetMessage(),
        "CS1673" => "struct の中では、フィールドを条件に使えません（ローカル変数か引数を使ってください） — " + d.GetMessage(),
        "CS0165" => "まだ値が入っていない変数を使っています — " + d.GetMessage(),
        _ => d.GetMessage(),
    };

    private static bool TryGetTracerCall(InvocationExpressionSyntax inv, out string name, out int id)
    {
        name = "";
        id = -1;
        var callee = inv.Expression.ToString();
        if (!callee.StartsWith("global::HotaruCodeViz.Tracer.")) return false;
        name = callee["global::HotaruCodeViz.Tracer.".Length..];
        int generic = name.IndexOf('<');
        if (generic >= 0) name = name[..generic]; // Return<int> → Return

        switch (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression)
        {
            case LiteralExpressionSyntax { Token.Value: int i }:
                id = i;
                return true;
            // 呼び出しの木の Return / Fail / Exit は __call3 のような変数を渡している
            case IdentifierNameSyntax ident when ident.Identifier.ValueText.StartsWith("__call")
                                                 && int.TryParse(ident.Identifier.ValueText["__call".Length..], out var n):
                id = n;
                return true;
            default:
                return false;
        }
    }

    public static void Execute(byte[] assemblyBytes)
    {
        var alc = new AssemblyLoadContext("UserCode", isCollectible: true);
        try
        {
            var asm = alc.LoadFromStream(new MemoryStream(assemblyBytes));
            var entry = asm.EntryPoint ?? throw new InvalidOperationException("エントリポイント（Main またはトップレベルステートメント）がありません。");
            object?[]? args = entry.GetParameters().Length == 1 ? new object?[] { Array.Empty<string>() } : null;
            var ret = entry.Invoke(null, args);
            if (ret is Task t) t.GetAwaiter().GetResult();
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
        finally
        {
            alc.Unload();
        }
    }
}
