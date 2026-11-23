using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.CodeAnalysis.CSharp;

namespace HotaruCodeViz;

/// <summary>条件付きブレークポイントとヒットカウント（「N 回目に止まる」）。</summary>
public partial class MainWindow
{
    /// <summary>行 → 条件式（ブレークポイントのある行だけ）。</summary>
    private readonly Dictionary<int, string> _breakpointConditions = new();

    /// <summary>行 → ヒットカウント（ブレークポイントのある行だけ）。</summary>
    private readonly Dictionary<int, HitCountRule> _hitCountRules = new();

    /// <summary>今の実行のブレークポイントの回数（まだ一度も実行していなければ null）。</summary>
    private HitCounter? _hitCounter;

    /// <summary>今の実行でコンパイルした条件（行 → 条件式）。実行中に条件を変えても、これと同じ間は有効。</summary>
    private Dictionary<int, string> _compiledConditions = new();

    /// <summary>今の実行で条件を差し込めた計装ポイント ID → 行。</summary>
    private Dictionary<int, int> _compiledConditionIds = new();

    private static readonly Brush ConditionMarkBrush = Palette.Freeze(Color.FromRgb(0xE5, 0x39, 0x35));

    /// <summary>コンパイルに渡す条件（ブレークポイントがある行のものだけ）。</summary>
    private Dictionary<int, string> ActiveBreakConditions() =>
        _breakpointConditions.Where(kv => _breakpoints.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Tracer に渡すヒットカウント（ブレークポイントがある行のものだけ）。</summary>
    private Dictionary<int, HitCountRule> ActiveHitCountRules() =>
        _hitCountRules.Where(kv => _breakpoints.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    private bool IsConditionCompiled(int line, string condition) =>
        _compiledConditions.TryGetValue(line, out var compiled) && compiled == condition;

    /// <summary>コンパイル結果から、使える条件を覚えて、使えなかった条件を知らせる。</summary>
    private void AcceptCompiledConditions(BuildResult result)
    {
        var errors = result.ConditionErrors ?? new();
        _compiledConditionIds = result.BreakConditionIds ?? new();
        _compiledConditions = ActiveBreakConditions()
            .Where(kv => !errors.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (line, message) in errors.OrderBy(kv => kv.Key))
        {
            _breakpointConditions.TryGetValue(line, out var text);
            AppendOutput($"⚠ 行 {line + 1} の条件「{text}」は使えません（この行では止まりません）\n   {message}\n");
        }

        Tracer.OnConditionError = (id, message) => Dispatcher.BeginInvoke(() =>
        {
            int line = _compiledConditionIds.TryGetValue(id, out var l) ? l : -1;
            _breakpointConditions.TryGetValue(line, out var text);
            AppendOutput($"⚠ 行 {line + 1} の条件「{text}」で例外が出たので false とみなしました（以後は表示しません）\n   {message}\n");
        });
    }

    /// <summary>ブレークポイントを外したとき：その行の条件・ヒットカウントも消す。</summary>
    private void ClearBreakpointSettings(int line)
    {
        _breakpointConditions.Remove(line);
        _hitCountRules.Remove(line);
    }

    /// <summary>ブレークポイントで止まっているときのステータスの頭（条件と、このステップで止まったのなら何回目か）。</summary>
    private string BreakpointStatus(long step, int line)
    {
        bool hasCondition = _breakpointConditions.TryGetValue(line, out var condition);
        var details = new List<string>();
        if (hasCondition) details.Add(condition!);
        if (_hitCounter?.LastStop is { } stop && stop.Step == step && stop.Line == line)
        {
            details.Add($"{stop.Hit:N0} 回目"
                        + (_hitCountRules.TryGetValue(line, out var rule) ? $"／{rule.Describe()}に止まる" : ""));
        }
        return $"🔴 {(hasCondition ? "条件付きブレークポイント" : "ブレークポイント")}"
               + (details.Count > 0 ? $"（{string.Join("、", details)}）" : "") + "　";
    }

    private void Gutter_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        double y = e.GetPosition(Gutter).Y;
        foreach (var (line, rect) in VisibleLines())
        {
            if (y >= rect.Top && y < rect.Bottom)
            {
                EditBreakpointCondition(line);
                break;
            }
        }
        e.Handled = true;
    }

    private void EditBreakpointCondition(int line)
    {
        if (line < 0) return;
        _breakpointConditions.TryGetValue(line, out var current);
        _hitCountRules.TryGetValue(line, out var currentRule);
        var lineText = line < SourceBox.LineCount ? SourceBox.GetLineText(line).Trim() : "";
        long? hits = _running && _hitCounter is { } counter ? counter.CountOf(line) : null;

        var dialog = new ConditionDialog(line, lineText, current, currentRule, hits) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (string.IsNullOrWhiteSpace(dialog.Condition))
        {
            _breakpointConditions.Remove(line); // 条件を外す（ブレークポイントは残す）
        }
        else
        {
            _breakpointConditions[line] = dialog.Condition.Trim();
            if (_running && !IsConditionCompiled(line, _breakpointConditions[line]))
                AppendOutput($"ℹ 行 {line + 1} の条件は、次の実行から有効になります（それまでこの行では止まりません）\n");
        }

        // ヒットカウントはコンパイルが要らないので、実行中に変えてもすぐ効く
        if (dialog.Rule is { } rule) _hitCountRules[line] = rule;
        else _hitCountRules.Remove(line);

        _breakpoints.Add(line);
        UpdateTracerBreakpoints();
        DrawGutter();
    }

    /// <summary>
    /// ブレークポイントの印。条件付きは「?」、ヒットカウント付きは「#」入り、まだコンパイルしていない条件は白抜き。
    /// ツールチップには、今の実行でここに来た回数も出す。
    /// </summary>
    private void DrawBreakpointMark(int line, Rect rect)
    {
        double top = rect.Top + (rect.Height - 12) / 2;
        bool hasCondition = _breakpointConditions.TryGetValue(line, out var condition);
        bool hasRule = _hitCountRules.TryGetValue(line, out var rule);
        string hits = _running && _hitCounter is { } counter ? $"\nこの実行でここに来た回数: {counter.CountOf(line):N0}" : "";

        if (!hasCondition && !hasRule)
        {
            var dot = new Ellipse
            {
                Width = 12, Height = 12, Fill = BreakpointBrush,
                ToolTip = "ブレークポイント" + hits + "\n右クリックで条件・ヒットカウントを付ける",
            };
            Canvas.SetLeft(dot, 4);
            Canvas.SetTop(dot, top);
            Gutter.Children.Add(dot);
            return;
        }

        bool pending = hasCondition && _running && !IsConditionCompiled(line, condition!);
        string text = (hasCondition ? "?" : "") + (hasRule ? "#" : "");
        var tip = new List<string>();
        if (hasCondition) tip.Add($"条件: {condition}" + (pending ? "（次の実行から有効）" : ""));
        if (hasRule) tip.Add($"ヒットカウント: {rule!.Describe()}に止まる" + (hasCondition ? "（条件が true になった回数で数える）" : ""));

        bool wide = text.Length > 1;
        var mark = new Grid
        {
            Width = wide ? 16 : 13, Height = 13,
            ToolTip = string.Join("\n", tip) + hits + "\n右クリックで変える",
        };
        mark.Children.Add(new Ellipse
        {
            Fill = pending ? Brushes.White : ConditionMarkBrush,
            Stroke = ConditionMarkBrush,
            StrokeThickness = 1.5,
        });
        mark.Children.Add(new TextBlock
        {
            Text = text, FontSize = wide ? 8 : 10, FontWeight = FontWeights.Bold,
            Foreground = pending ? ConditionMarkBrush : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        });
        Canvas.SetLeft(mark, wide ? 2 : 3.5);
        Canvas.SetTop(mark, top);
        Gutter.Children.Add(mark);
    }
}

/// <summary>ブレークポイントの条件とヒットカウントを入力する小さなダイアログ。</summary>
public sealed class ConditionDialog : Window
{
    private static readonly (string Label, HitCountMode? Mode)[] Modes =
    [
        ("毎回止まる", null),
        ("ちょうど N 回目だけ", HitCountMode.Equal),
        ("N の倍数回目ごと", HitCountMode.Multiple),
        ("N 回目以降は毎回", HitCountMode.AtLeast),
    ];

    private static readonly FontFamily CodeFont = new("Consolas, MS Gothic");

    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly ComboBox _mode;
    private readonly TextBox _count;
    private readonly TextBlock _countError;

    public string Condition => _box.Text;

    /// <summary>OK を押したときのヒットカウント（毎回止まるなら null）。</summary>
    public HitCountRule? Rule { get; private set; }

    /// <param name="hits">実行中なら、今の実行でここに来た回数</param>
    public ConditionDialog(int line, string lineText, string? current, HitCountRule? currentRule = null, long? hits = null)
    {
        Title = $"ブレークポイントの設定（行 {line + 1}）";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), Width = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = lineText, FontFamily = CodeFont, Foreground = Brushes.DimGray,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8),
        });

        // ---- 条件
        panel.Children.Add(new TextBlock { Text = "条件", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(new TextBlock
        {
            Text = "条件が true のときだけ止まります（例: i == 500、sum > 100 && a[i] % 2 == 0）",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        _box = new TextBox { Text = current ?? "", FontFamily = CodeFont, FontSize = 14, Padding = new Thickness(3) };
        _box.AutomationId("ConditionBox");
        panel.Children.Add(_box);
        _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _error.AutomationId("ConditionError");
        panel.Children.Add(_error);
        panel.Children.Add(new TextBlock
        {
            Text = "その場所で使える変数が使えます。空にすると条件を外します。条件を変えたときは、次の実行から有効になります。",
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        // ---- ヒットカウント
        panel.Children.Add(new TextBlock { Text = "ヒットカウント", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 4) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _mode = new ComboBox { Width = 160, ItemsSource = Modes.Select(m => m.Label).ToList() };
        _mode.SelectedIndex = currentRule is null ? 0 : Array.FindIndex(Modes, m => m.Mode == currentRule.Mode);
        _mode.AutomationId("HitCountMode");
        _count = new TextBox
        {
            Text = currentRule?.Count.ToString() ?? "", Width = 100, Margin = new Thickness(6, 0, 0, 0),
            FontFamily = CodeFont, FontSize = 14, Padding = new Thickness(3),
        };
        _count.AutomationId("HitCountBox");
        row.Children.Add(_mode);
        row.Children.Add(new TextBlock { Text = "N =", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        row.Children.Add(_count);
        if (hits is { } h)
        {
            row.Children.Add(new TextBlock
            {
                Text = $"（これまで {h:N0} 回）", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
        panel.Children.Add(row);
        _countError = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _countError.AutomationId("HitCountError");
        panel.Children.Add(_countError);
        panel.Children.Add(new TextBlock
        {
            Text = "ここに来た回数（条件があるときは、条件が true になった回数）で止まるかを決めます。実行中に変えてもすぐ効きます（回数はそのまま続きから数えます）。",
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        _mode.SelectionChanged += (_, _) =>
        {
            UpdateCountEnabled();
            if (_count.IsEnabled && _count.Text.Length == 0) _count.Text = "1";
        };
        UpdateCountEnabled();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.AutomationId("ConditionOk");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    private HitCountMode? SelectedMode => Modes[Math.Max(0, _mode.SelectedIndex)].Mode;

    private void UpdateCountEnabled() => _count.IsEnabled = SelectedMode is not null;

    private void Accept()
    {
        _error.Text = _countError.Text = "";
        bool ok = true;

        var text = _box.Text.Trim();
        if (text.Length > 0)
        {
            // 式として読めるかだけここで確かめる（型や変数のチェックは実行するときのコンパイルで）
            var expr = SyntaxFactory.ParseExpression(text);
            var diag = expr.GetDiagnostics().FirstOrDefault();
            if (diag is not null || expr.FullSpan.Length != text.Length)
            {
                _error.Text = "式として読めません: " + (diag?.GetMessage() ?? "余分な文字があります");
                ok = false;
            }
        }

        Rule = null;
        if (SelectedMode is { } mode)
        {
            var countText = _count.Text.Trim().Replace(",", "").Replace("_", "");
            if (long.TryParse(countText, out var n) && n >= 1)
            {
                Rule = new HitCountRule(mode, n);
            }
            else
            {
                _countError.Text = "N には 1 以上の整数を入れてください";
                ok = false;
            }
        }

        if (ok) DialogResult = true;
    }
}

internal static class AutomationExtensions
{
    public static void AutomationId(this UIElement element, string id) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
}
