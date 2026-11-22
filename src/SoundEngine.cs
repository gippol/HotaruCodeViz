using Microsoft.CodeAnalysis.CSharp.Syntax;
using NAudio.Wave;

namespace HotaruCodeViz;

public enum Waveform { Sine, Triangle, Square, Bell }

/// <summary>
/// とても小さなシンセサイザー。Play で鳴らした音を混ぜて、NAudio に流し続ける。
/// Play はどのスレッドから呼んでもよい。
/// </summary>
public sealed class SoundEngine : ISampleProvider, IDisposable
{
    private const int SampleRate = 44100;
    private const int MaxVoices = 24;

    private sealed class Voice
    {
        public double Freq, Phase, Glide, DecayRate;
        public long Age, Length;
        public float Gain;
        public Waveform Wave;
    }

    private readonly List<Voice> _voices = new();
    private readonly object _lock = new();
    private readonly WaveOutEvent? _output;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    /// <summary>全体の音量（0〜1）。</summary>
    public float Volume { get; set; } = 0.5f;

    private WaveFileWriter? _recorder;

    /// <summary>録音を始める。スピーカーに流したのと同じ音を WAV に書く（無音のところも書く）。</summary>
    public void StartRecording(string path)
    {
        lock (_lock) _recorder = new WaveFileWriter(path, WaveFormat);
    }

    public void StopRecording()
    {
        lock (_lock)
        {
            _recorder?.Dispose();
            _recorder = null;
        }
    }

    /// <param name="startOutput">false なら音声デバイスを開かない（テストで Read を直接呼ぶ用）</param>
    public SoundEngine(bool startOutput = true)
    {
        if (!startOutput) return;
        _output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
        _output.Init(this);
        _output.Play();
    }

    /// <param name="glide">1秒あたりに何オクターブ音程を動かすか（マイナスで下がる）</param>
    public void Play(double freq, double seconds, Waveform wave, float gain, double glide = 0)
    {
        var length = (long)(Math.Clamp(seconds, 0.03, 2.0) * SampleRate);
        var voice = new Voice
        {
            Freq = freq, Glide = glide, Wave = wave, Gain = gain, Length = length,
            DecayRate = wave == Waveform.Bell ? 2.5 / (length / (double)SampleRate) : 4.0 / (length / (double)SampleRate),
        };
        lock (_lock)
        {
            if (_voices.Count >= MaxVoices) _voices.RemoveAt(0);
            _voices.Add(voice);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        const double attack = SampleRate * 0.004;
        const double release = SampleRate * 0.006;
        float volume = Volume;

        lock (_lock)
        {
            for (int n = 0; n < count; n++)
            {
                double sum = 0;
                for (int v = _voices.Count - 1; v >= 0; v--)
                {
                    var voice = _voices[v];
                    if (voice.Age >= voice.Length)
                    {
                        _voices.RemoveAt(v);
                        continue;
                    }
                    double t = voice.Age / (double)SampleRate;
                    double env = Math.Min(1, voice.Age / attack)
                                 * Math.Exp(-voice.DecayRate * t)
                                 * Math.Min(1, (voice.Length - voice.Age) / release);
                    double freq = voice.Glide == 0 ? voice.Freq : voice.Freq * Math.Pow(2, voice.Glide * t);
                    voice.Phase += freq / SampleRate;
                    voice.Phase -= Math.Floor(voice.Phase);
                    sum += voice.Gain * env * Shape(voice.Wave, voice.Phase);
                    voice.Age++;
                }
                buffer[offset + n] = (float)Math.Tanh(sum * volume * 1.5); // まとめて鳴っても割れないように柔らかく丸める
            }
            _recorder?.WriteSamples(buffer, offset, count);
        }
        return count; // 鳴らす音がなくても無音を流し続ける
    }

    private static double Shape(Waveform wave, double phase) => wave switch
    {
        Waveform.Sine => Math.Sin(2 * Math.PI * phase),
        Waveform.Triangle => 4 * Math.Abs(phase - 0.5) - 1,
        Waveform.Square => Math.Tanh(4 * Math.Sin(2 * Math.PI * phase)) * 0.6,
        Waveform.Bell => Math.Sin(2 * Math.PI * phase) * 0.7 + Math.Sin(2 * Math.PI * phase * 2.76) * 0.3, // 倍音をずらして鐘っぽく
        _ => 0,
    };

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
        StopRecording();
    }
}

/// <summary>実行のステップを音に変換する。</summary>
public static class SoundMapper
{
    // ペンタトニック音階（ド レ ミ ソ ラ）。どの組み合わせで鳴っても濁らない
    private static readonly int[] Pentatonic = { 0, 2, 4, 7, 9 };

    /// <summary>音階の degree 番目の音の周波数（degree 0 = C4）。</summary>
    private static double Note(int degree)
    {
        int octave = (int)Math.Floor(degree / 5.0);
        int step = degree - octave * 5;
        int midi = 60 + octave * 12 + Pentatonic[step];
        return 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }

    /// <param name="noteSeconds">1音の長さの目安（待ち時間から決める）</param>
    public static void Play(SoundEngine engine, HitInfo info, HitEvent ev, int line, double noteSeconds, bool expressions)
    {
        // 行番号で音の高さ、呼び出しの深さで持ち上げる（再帰が深くなると音程が上がる）
        int degree = line % 10 + Math.Min(ev.Depth, 20) * 2;

        switch (info.Kind)
        {
            case HitKind.Statement when info.Node is ReturnStatementSyntax:
                engine.Play(Note(degree + 5), noteSeconds * 1.8, Waveform.Bell, 0.45f);
                break;
            case HitKind.Statement when info.Node is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CommonForEachStatementSyntax:
                engine.Play(Note(degree - 10), noteSeconds * 1.5, Waveform.Triangle, 0.6f); // ループの入口はベース
                break;
            case HitKind.Statement:
                engine.Play(Note(degree), noteSeconds, Waveform.Triangle, 0.5f);
                break;
            case HitKind.Condition when ev.Value == "true":
                engine.Play(Note(degree + 5), noteSeconds * 0.7, Waveform.Square, 0.45f);
                break;
            case HitKind.Condition:
                engine.Play(Note(degree - 5), noteSeconds * 0.9, Waveform.Sine, 0.55f, glide: -1.5); // 「ぽん」と下がる
                break;
            case HitKind.Expression when expressions:
                engine.Play(Note(degree + 10), Math.Min(noteSeconds * 0.4, 0.08), Waveform.Sine, 0.12f);
                break;
        }
    }
}
