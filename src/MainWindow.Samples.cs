using System.Windows;
using System.Windows.Controls;

namespace HotaruCodeViz;

/// <summary>📚 図鑑（アルゴリズムのサンプル集）。</summary>
public partial class MainWindow
{
    private string? _loadedSampleSource;   // 図鑑から読み込んだままのコード（書き換えたかの判定用）
    private bool _loadingSample;

    private void InitSamples()
    {
        SampleCombo.Items.Add(new ComboBoxItem { Content = "（選んでください）", IsEnabled = false });
        foreach (var sample in AlgorithmSamples.All) SampleCombo.Items.Add(sample);
        SampleCombo.SelectedIndex = 0;
    }

    private void SampleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSample || SampleCombo.SelectedItem is not AlgorithmSample sample) return;
        if (_running || _building)
        {
            ResetSampleCombo();
            StatusText.Text = "実行中は図鑑を読み込めません（⏹ 停止してから）";
            return;
        }

        // 自分で書いたコードを消してしまわないように確かめる
        bool edited = SourceBox.Text != Sample && SourceBox.Text != _loadedSampleSource;
        if (edited && MessageBox.Show(this, $"今のコードを「{sample.Name}」のサンプルに置き換えます。よろしいですか？",
                "📚 図鑑", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            ResetSampleCombo();
            return;
        }

        LoadSample(sample);
    }

    private void LoadSample(AlgorithmSample sample)
    {
        _breakpoints.Clear();
        _breakpointConditions.Clear();
        _hitCountRules.Clear();
        SourceBox.Text = sample.Code;
        InputBox.Text = sample.Input.Replace("\n", Environment.NewLine);
        _loadedSampleSource = SourceBox.Text;
        SpeedSlider.Value = sample.DelayMs;
        WatchCheck.IsChecked = true; // 配列の絵には変数ウォッチが要る

        SampleDescription.Text = $"📚 {sample.Category}：{sample.Name} — {sample.Description}";
        SampleDescription.Visibility = Visibility.Visible;
        ArrayPanel.Clear();
        MemoryPanel.Clear();
        ViewTabs.SelectedItem = sample.Category == "再帰" ? CallTab : ArrayTab;
        OutputBox.Clear();
        AppendOutput($"📚 {sample.Name} を読み込みました。▶ 実行すると「🎨 配列」タブで動きが見られます。\n");

        if (sample.BreakAt is { } marker)
        {
            int index = sample.Code.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                int line = sample.Code[..index].Count(ch => ch == '\n');
                _breakpoints.Add(line);
                AppendOutput($"● 行 {line + 1} にブレークポイントを置きました。⏩ 早送りで準備を飛ばして、そこから ▶ でゆっくり見られます。\n");
            }
        }
        StatusText.Text = $"📚 {sample.Name}";
        DrawGutter();
        ResetSampleCombo();
    }

    private void ResetSampleCombo()
    {
        _loadingSample = true;
        SampleCombo.SelectedIndex = 0;
        _loadingSample = false;
    }
}
