using System.Windows;

namespace HotaruCodeViz;

/// <summary>音で聴く実行。</summary>
public partial class MainWindow
{
    private SoundEngine? _sound;

    private bool SoundOn => SoundCheck?.IsChecked == true && _sound is not null;

    private void SoundCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool on = SoundCheck.IsChecked == true;
        if (on && _sound is null)
        {
            try
            {
                _sound = new SoundEngine { Volume = (float)(VolumeSlider.Value / 100) };
            }
            catch (Exception ex)
            {
                AppendOutput($"🔇 音を出せませんでした: {ex.Message}\n");
                SoundCheck.IsChecked = false;
                return;
            }
        }
        Tracer.MeasureDepth = on;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sound is not null) _sound.Volume = (float)(e.NewValue / 100);
    }

    private void PlaySound(HitInfo info, HitEvent ev)
    {
        if (!SoundOn) return;
        // 待ち時間が長いときはゆったり、短いときは短く（重ならないように）
        double noteSeconds = Math.Clamp(Tracer.DelayMs / 1000.0 * 0.9, 0.05, 0.5);
        SoundMapper.Play(_sound!, info, ev, LineOf(info.Node), noteSeconds, expressions: ExprCheck.IsChecked == true);
    }

    private void DisposeSound()
    {
        _sound?.Dispose();
        _sound = null;
    }
}
