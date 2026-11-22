using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HotaruCodeViz;

public sealed record RecordingResult(string? Gif, string? Mp4, string? Wav, List<string> Notes);

/// <summary>
/// 画面の録画と録音。
///   録画中：画面を 1 秒に 10 コマ PNG で一時フォルダに保存し、音は WAV に書く
///   停止後：GIF を作る（自作エンコーダ）。ffmpeg があれば音付きの MP4 も作る
/// </summary>
public sealed class ScreenRecorder
{
    public const int FramesPerSecond = 10;
    public const double Scale = 0.6;               // 画面を何倍に縮めて録るか
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(3);

    public static string OutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HotaruCodeViz");

    private readonly FrameworkElement _target;
    private readonly SoundEngine? _sound;
    private readonly string _tempDir;
    private readonly string _baseName;
    private readonly List<(string Path, double Seconds)> _frames = new();
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private string? _wavPath;
    private int _width, _height;

    public TimeSpan Elapsed => _clock.Elapsed;
    public int FrameCount => _frames.Count;
    public bool IsRecording => _timer.IsEnabled;

    /// <summary>1コマ録るたびに呼ばれる（経過時間の表示用）。</summary>
    public event Action? FrameCaptured;

    /// <summary>上限の長さに達して自動で止まったときに呼ばれる。</summary>
    public event Action? MaxDurationReached;

    public ScreenRecorder(FrameworkElement target, SoundEngine? sound)
    {
        _target = target;
        _sound = sound;
        _baseName = "録画_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _tempDir = Path.Combine(Path.GetTempPath(), "HotaruCodeViz", _baseName);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond) };
        _timer.Tick += (_, _) => Capture();
    }

    public void Start()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(OutputDirectory);

        // H.264 は幅・高さが偶数でないといけないので偶数にそろえる
        _width = (int)(_target.ActualWidth * Scale) & ~1;
        _height = (int)(_target.ActualHeight * Scale) & ~1;

        if (_sound is not null)
        {
            _wavPath = Path.Combine(OutputDirectory, _baseName + ".wav");
            _sound.StartRecording(_wavPath);
        }
        _clock.Start();
        Capture();
        _timer.Start();
    }

    private void Capture()
    {
        if (_clock.Elapsed > MaxDuration)
        {
            _timer.Stop();
            MaxDurationReached?.Invoke();
            return;
        }

        var rtb = new RenderTargetBitmap(_width, _height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        // 背景を白で塗ってから描く（透明部分が黒くならないように）
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _target.ActualWidth, _target.ActualHeight));
            dc.DrawRectangle(new VisualBrush(_target), null, new Rect(0, 0, _target.ActualWidth, _target.ActualHeight));
        }
        rtb.Render(visual);

        var path = Path.Combine(_tempDir, $"frame_{_frames.Count:D5}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) encoder.Save(fs);

        _frames.Add((path, _clock.Elapsed.TotalSeconds));
        FrameCaptured?.Invoke();
    }

    /// <summary>録画を止めて、GIF（と ffmpeg があれば MP4）を書き出す。</summary>
    public async Task<RecordingResult> StopAsync(string? ffmpegPath, IProgress<string>? progress = null)
    {
        _timer.Stop();
        double end = _clock.Elapsed.TotalSeconds;
        _clock.Stop();
        _sound?.StopRecording();

        var notes = new List<string>();
        var frames = _frames.ToList();
        if (frames.Count == 0)
            return new RecordingResult(null, null, _wavPath, notes);

        // 各コマの表示時間（次のコマまで）
        var durations = new double[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            durations[i] = (i + 1 < frames.Count ? frames[i + 1].Seconds : end) - frames[i].Seconds;

        string gifPath = Path.Combine(OutputDirectory, _baseName + ".gif");
        progress?.Report("GIF を書き出し中…");
        await Task.Run(() => WriteGif(frames.Select(f => f.Path).ToList(), durations, gifPath, progress));

        string? mp4Path = null;
        if (ffmpegPath is not null)
        {
            progress?.Report("MP4 を書き出し中…（ffmpeg）");
            mp4Path = Path.Combine(OutputDirectory, _baseName + ".mp4");
            var error = await Task.Run(() => WriteMp4(ffmpegPath, frames.Select(f => f.Path).ToList(), durations, _wavPath, mp4Path));
            if (error is not null)
            {
                notes.Add("MP4 の作成に失敗しました: " + error);
                mp4Path = null;
            }
        }
        else
        {
            notes.Add("ffmpeg が見つからないので MP4 と音は作りませんでした（GIF は保存済み）");
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 一時ファイルの削除失敗は無視 */ }
        return new RecordingResult(gifPath, mp4Path, _wavPath, notes);
    }

    /// <summary>録画をやめて、何も書き出さない（ウィンドウを閉じるときなど）。</summary>
    public void Abort()
    {
        _timer.Stop();
        _sound?.StopRecording();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteGif(List<string> paths, double[] durations, string gifPath, IProgress<string>? progress)
    {
        using var fs = File.Create(gifPath);
        using var gif = new GifWriter(fs, _width, _height);
        var pixels = new byte[_width * _height * 4];
        for (int i = 0; i < paths.Count; i++)
        {
            var frame = BitmapDecoder.Create(new Uri(paths[i]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            frame.CopyPixels(pixels, _width * 4, 0);
            gif.AddFrame(pixels, _width * 4, (int)(durations[i] * 1000));
            if (i % 20 == 0) progress?.Report($"GIF を書き出し中… {i + 1}/{paths.Count}");
        }
    }

    /// <summary>ffmpeg の concat 形式で「このコマを何秒」という一覧を作り、WAV と合わせて MP4 にする。</summary>
    private static string? WriteMp4(string ffmpeg, List<string> paths, double[] durations, string? wav, string mp4Path)
    {
        var listPath = Path.Combine(Path.GetDirectoryName(paths[0])!, "frames.txt");
        using (var w = new StreamWriter(listPath))
        {
            w.WriteLine("ffconcat version 1.0");
            for (int i = 0; i < paths.Count; i++)
            {
                w.WriteLine($"file '{paths[i].Replace("\\", "/").Replace("'", "'\\''")}'");
                w.WriteLine($"duration {durations[i].ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            // concat の仕様で、最後のコマの duration を効かせるためにもう一度書く
            w.WriteLine($"file '{paths[^1].Replace("\\", "/").Replace("'", "'\\''")}'");
        }

        var args = new List<string> { "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath };
        if (wav is not null) args.AddRange(new[] { "-i", wav });
        args.AddRange(new[] { "-vf", "fps=30,format=yuv420p" });
        args.AddRange(ChooseVideoEncoder(ffmpeg));
        if (wav is not null) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-shortest" });
        args.Add(mp4Path);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            proc.Kill(true);
            return "時間がかかりすぎたので中止しました";
        }
        return proc.ExitCode == 0 ? null : $"ffmpeg の終了コード {proc.ExitCode}: {stderr.Result.Trim()}";
    }

    /// <summary>
    /// 使える H.264 エンコーダを選ぶ。ffmpeg のビルドによって入っているものが違う
    /// （LGPL 版には libx264 がないので、libopenh264 や Windows の Media Foundation を使う）。
    /// </summary>
    private static string[] ChooseVideoEncoder(string ffmpeg)
    {
        string list = "";
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -encoders")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEndAsync();
            list = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
        }
        catch
        {
            // 一覧が取れなければ下の既定にまかせる
        }

        bool Has(string name) => System.Text.RegularExpressions.Regex.IsMatch(list, $@"\s{name}\s");
        if (Has("libx264")) return new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20" };
        if (Has("libopenh264")) return new[] { "-c:v", "libopenh264", "-b:v", "3M" };
        if (Has("h264_mf")) return new[] { "-c:v", "h264_mf", "-b:v", "3M" };
        return new[] { "-c:v", "mpeg4", "-q:v", "3" };
    }

    /// <summary>ffmpeg.exe を探す：設定 → 環境変数 PATH → アプリと同じフォルダ → 上の階層の tools フォルダ。</summary>
    public static string? FindFfmpeg(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        // アプリのフォルダから上にたどって tools\ffmpeg.exe を探す（開発中は HotaruCodeViz\tools に置く）
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH に変な文字が入っていても無視
            }
        }

        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(local) ? local : null;
    }
}
