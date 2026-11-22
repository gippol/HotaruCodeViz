using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.CodeAnalysis.CSharp;

namespace HotaruCodeViz;

/// <summary>条件付きブレークポイント。</summary>
public partial class MainWindow
{
    /// <summary>行 → 条件式（ブレークポイントのある行だけ）。</summary>
    private readonly Dictionary<int, string> _breakpointConditions = new();

    /// <summary>今の実行でコンパイルした条件（行 → 条件式）。実行中に条件を変えても、これと同じ間は有効。</summary>
    private Dictionary<int, string> _compiledConditions = new();

    /// <summary>今の実行で条件を差し込めた計装ポイント ID → 行。</summary>
    private Dictionary<int, int> _compiledConditionIds = new();

    private static readonly Brush ConditionMarkBrush = Palette.Freeze(Color.FromRgb(0xE5, 0x39, 0x35));

    /// <summary>コンパイルに渡す条件（ブレークポイントがある行のものだけ）。</summary>
    private Dictionary<int, string> ActiveBreakConditions() =>
        _breakpointConditions.Where(kv => _breakpoints.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

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
        var lineText = line < SourceBox.LineCount ? SourceBox.GetLineText(line).Trim() : "";

        var dialog = new ConditionDialog(line, lineText, current) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (string.IsNullOrWhiteSpace(dialog.Condition))
        {
            _breakpointConditions.Remove(line); // 条件を外す（ブレークポイントは残す）
        }
        else
        {
            _breakpointConditions[line] = dialog.Condition.Trim();
            _breakpoints.Add(line);
            if (_running && !IsConditionCompiled(line, _breakpointConditions[line]))
                AppendOutput($"ℹ 行 {line + 1} の条件は、次の実行から有効になります（それまでこの行では止まりません）\n");
        }
        UpdateTracerBreakpoints();
        DrawGutter();
    }

    /// <summary>ブレークポイントの印。条件付きは「?」入り、まだコンパイルしていない条件は白抜き。</summary>
    private void DrawBreakpointMark(int line, Rect rect)
    {
        double top = rect.Top + (rect.Height - 12) / 2;
        if (!_breakpointConditions.TryGetValue(line, out var condition))
        {
            var dot = new Ellipse { Width = 12, Height = 12, Fill = BreakpointBrush, ToolTip = "ブレークポイント（右クリックで条件を付ける）" };
            Canvas.SetLeft(dot, 4);
            Canvas.SetTop(dot, top);
            Gutter.Children.Add(dot);
            return;
        }

        bool pending = _running && !IsConditionCompiled(line, condition);
        var mark = new Grid
        {
            Width = 13, Height = 13,
            ToolTip = $"条件: {condition}" + (pending ? "\n（次の実行から有効）" : "") + "\n右クリックで条件を変える",
        };
        mark.Children.Add(new Ellipse
        {
            Fill = pending ? Brushes.White : ConditionMarkBrush,
            Stroke = ConditionMarkBrush,
            StrokeThickness = 1.5,
        });
        mark.Children.Add(new TextBlock
        {
            Text = "?", FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = pending ? ConditionMarkBrush : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        });
        Canvas.SetLeft(mark, 3.5);
        Canvas.SetTop(mark, top);
        Gutter.Children.Add(mark);
    }
}

/// <summary>条件を入力する小さなダイアログ。</summary>
public sealed class ConditionDialog : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;

    public string Condition => _box.Text;

    public ConditionDialog(int line, string lineText, string? current)
    {
        Title = $"条件付きブレークポイント（行 {line + 1}）";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), Width = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = lineText, FontFamily = new FontFamily("Consolas, MS Gothic"), Foreground = Brushes.DimGray,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "条件が true のときだけ止まります（例: i == 500、sum > 100 && a[i] % 2 == 0）",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        _box = new TextBox
        {
            Text = current ?? "", FontFamily = new FontFamily("Consolas, MS Gothic"), FontSize = 14, Padding = new Thickness(3),
        };
        _box.AutomationId("ConditionBox");
        panel.Children.Add(_box);
        _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _error.AutomationId("ConditionError");
        panel.Children.Add(_error);
        panel.Children.Add(new TextBlock
        {
            Text = "その場所で使える変数が使えます。空にして OK を押すと条件を外します（ブレークポイントは残ります）。",
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

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

    private void Accept()
    {
        var text = _box.Text.Trim();
        if (text.Length > 0)
        {
            // 式として読めるかだけここで確かめる（型や変数のチェックは実行するときのコンパイルで）
            var expr = SyntaxFactory.ParseExpression(text);
            var diag = expr.GetDiagnostics().FirstOrDefault();
            if (diag is not null || expr.FullSpan.Length != text.Length)
            {
                _error.Text = "式として読めません: " + (diag?.GetMessage() ?? "余分な文字があります");
                return;
            }
        }
        DialogResult = true;
    }
}

internal static class AutomationExtensions
{
    public static void AutomationId(this UIElement element, string id) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
}
