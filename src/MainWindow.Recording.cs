using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HotaruCodeViz;

/// <summary>実行の録画（GIF / MP4）と録音（WAV）。</summary>
public partial class MainWindow
{
    private ScreenRecorder? _recorder;
    private string? _audioOnlyPath;   // 「🎙 録音」だけしているときの WAV
    private bool _finishingRecording;
    private string? _ffmpeg;          // 見つからなければ null（録音は無効、録画は GIF だけ）

    private void InitRecording()
    {
        _ffmpeg = ScreenRecorder.FindFfmpeg();
        if (_ffmpeg is null)
        {
            RecordAudioButton.IsEnabled = false;
            RecordAudioButton.ToolTip = "ffmpeg が見つからないので録音は使えません（tools\\ffmpeg.exe に置くか PATH を通してください）";
            RecordVideoButton.ToolTip = "ウィンドウを録画して GIF にする（ffmpeg が見つからないので音なし・MP4 なし）。保存先：ビデオ\\HotaruCodeViz";
        }
    }

    /// <summary>録音してよいか（ffmpeg があって、音がオン）。</summary>
    private bool CanRecordAudio => _ffmpeg is not null && SoundOn;

    private async void RecordVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_finishingRecording) return;
        if (_recorder is null) StartVideoRecording();
        else await StopVideoRecordingAsync();
    }

    private void StartVideoRecording()
    {
        if (_audioOnlyPath is not null) StopAudioRecording(); // 録画のほうで一緒に録音する

        bool withAudio = CanRecordAudio;
        _recorder = new ScreenRecorder(RootPanel, withAudio ? _sound : null);
        _recorder.FrameCaptured += () =>
            RecordingText.Text = $"● 録画中 {_recorder.Elapsed:m\\:ss}" + (withAudio ? "（音あり）" : "");
        _recorder.MaxDurationReached += async () =>
        {
            AppendOutput($"⏺ 録画は {ScreenRecorder.MaxDuration.TotalMinutes:0} 分までなので止めました\n");
            await StopVideoRecordingAsync();
        };
        _recorder.Start();
        RecordVideoButton.Content = "⏹ 録画を止める";
        RecordAudioButton.IsEnabled = false;
        if (_ffmpeg is null) AppendOutput("⏺ 録画を始めました（ffmpeg が見つからないので GIF だけ・音なし）\n");
        else if (!withAudio) AppendOutput("⏺ 録画を始めました（🔊 音がオフなので音なし）\n");
        else AppendOutput("⏺ 録画を始めました（音も録音します）\n");
    }

    private async Task StopVideoRecordingAsync()
    {
        if (_recorder is not { } rec || _finishingRecording) return;
        _finishingRecording = true;
        RecordVideoButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => RecordingText.Text = s);
            var result = await rec.StopAsync(_ffmpeg, progress);

            AppendOutput($"⏺ 録画を保存しました（{rec.FrameCount} コマ、{rec.Elapsed:m\\:ss}）\n");
            foreach (var f in new[] { result.Mp4, result.Gif, result.Wav })
                if (f is not null && File.Exists(f))
                    AppendOutput($"   {f}（{new FileInfo(f).Length / 1024.0 / 1024.0:F1} MB）\n");
            foreach (var n in result.Notes) AppendOutput($"   ⚠ {n}\n");

            OpenOutputFolder(result.Mp4 ?? result.Gif);
        }
        catch (Exception ex)
        {
            AppendOutput($"⚠ 録画の保存に失敗しました: {ex.Message}\n");
        }
        finally
        {
            _recorder = null;
            _finishingRecording = false;
            RecordingText.Text = "";
            RecordVideoButton.Content = "⏺ 録画";
            RecordVideoButton.IsEnabled = true;
            RecordAudioButton.IsEnabled = _ffmpeg is not null;
        }
    }

    private void RecordAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioOnlyPath is not null)
        {
            StopAudioRecording();
            return;
        }
        if (_ffmpeg is null) return; // ボタンは無効にしてあるが念のため
        if (!SoundOn)
        {
            // 録音するなら音が要るので、オンにする
            SoundCheck.IsChecked = true;
            if (!SoundOn) return;
        }
        Directory.CreateDirectory(ScreenRecorder.OutputDirectory);
        _audioOnlyPath = Path.Combine(ScreenRecorder.OutputDirectory, "録音_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
        _sound!.StartRecording(_audioOnlyPath);
        RecordAudioButton.Content = "⏹ 録音を止める";
        RecordingText.Text = "● 録音中";
        AppendOutput("🎙 録音を始めました\n");
    }

    private void StopAudioRecording()
    {
        if (_audioOnlyPath is not { } path) return;
        _sound?.StopRecording();
        _audioOnlyPath = null;
        RecordAudioButton.Content = "🎙 録音";
        RecordingText.Text = "";
        if (File.Exists(path))
            AppendOutput($"🎙 録音を保存しました\n   {path}（{new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB）\n");
    }

    /// <summary>音をオフにしたとき・ウィンドウを閉じるときの後始末。</summary>
    private void AbortRecordings()
    {
        _recorder?.Abort();
        _recorder = null;
        StopAudioRecording();
    }

    private static void OpenOutputFolder(string? selectFile)
    {
        try
        {
            if (selectFile is not null && File.Exists(selectFile))
                Process.Start("explorer.exe", $"/select,\"{selectFile}\"");
        }
        catch
        {
            // フォルダが開けなくても保存はできているので無視
        }
    }
}
