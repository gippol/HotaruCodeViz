using System.IO;

namespace HotaruCodeViz;

/// <summary>
/// アニメーション GIF を書き出す小さなエンコーダ。
/// ・パレットは固定の 252 色（赤 6 段階 × 緑 7 段階 × 青 6 段階）
/// ・前のコマから変わった四角の部分だけを書く（画面の録画はほとんど変わらないので小さくなる）
/// ・変化のないコマは、前のコマの表示時間を延ばすだけにする
/// </summary>
public sealed class GifWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _width, _height;
    private byte[]? _previous;          // 前のコマ（パレット番号）
    private PendingFrame? _pending;     // まだ書いていないコマ（表示時間を延ばせるように1つ待つ）

    private sealed record PendingFrame(int X, int Y, int W, int H, byte[] Pixels)
    {
        public int DelayCs { get; set; }
    }

    private static readonly byte[] Palette = BuildPalette();

    public GifWriter(Stream stream, int width, int height)
    {
        _stream = stream;
        _width = width;
        _height = height;

        WriteAscii("GIF89a");
        WriteUInt16(width);
        WriteUInt16(height);
        _stream.WriteByte(0xF7); // グローバルカラーテーブルあり、256 色
        _stream.WriteByte(0);    // 背景色
        _stream.WriteByte(0);    // 縦横比
        _stream.Write(Palette);

        // 無限ループ（NETSCAPE2.0 拡張）
        _stream.Write(new byte[] { 0x21, 0xFF, 0x0B });
        WriteAscii("NETSCAPE2.0");
        _stream.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
    }

    private static byte[] BuildPalette()
    {
        var p = new byte[256 * 3];
        for (int r = 0; r < 6; r++)
            for (int g = 0; g < 7; g++)
                for (int b = 0; b < 6; b++)
                {
                    int i = (r * 42 + g * 6 + b) * 3;
                    p[i] = (byte)(r * 255 / 5);
                    p[i + 1] = (byte)(g * 255 / 6);
                    p[i + 2] = (byte)(b * 255 / 5);
                }
        return p;
    }

    private static byte Quantize(byte r, byte g, byte b) =>
        (byte)((r * 5 + 127) / 255 * 42 + (g * 6 + 127) / 255 * 6 + (b * 5 + 127) / 255);

    /// <param name="bgra">BGRA 32bit のピクセル（1行 = stride バイト）</param>
    /// <param name="delayMs">このコマを表示する時間</param>
    public void AddFrame(byte[] bgra, int stride, int delayMs)
    {
        var current = new byte[_width * _height];
        for (int y = 0; y < _height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < _width; x++)
            {
                int i = row + x * 4;
                current[y * _width + x] = Quantize(bgra[i + 2], bgra[i + 1], bgra[i]);
            }
        }

        int delayCs = Math.Max(2, (int)Math.Round(delayMs / 10.0));

        // 変化した範囲を探す
        int minX = _width, minY = _height, maxX = -1, maxY = -1;
        if (_previous is null)
        {
            (minX, minY, maxX, maxY) = (0, 0, _width - 1, _height - 1);
        }
        else
        {
            for (int y = 0; y < _height; y++)
            {
                int row = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    if (current[row + x] == _previous[row + x]) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        _previous = current;

        if (maxX < 0)
        {
            // 何も変わっていない → 前のコマを長く表示するだけ
            if (_pending is not null) _pending.DelayCs += delayCs;
            return;
        }

        int w = maxX - minX + 1, h = maxY - minY + 1;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(current, (minY + y) * _width + minX, pixels, y * w, w);

        Flush();
        _pending = new PendingFrame(minX, minY, w, h, pixels) { DelayCs = delayCs };
    }

    private void Flush()
    {
        if (_pending is not { } f) return;
        _pending = null;

        // グラフィック制御拡張：表示時間、前のコマは消さずに残す（disposal = 1）
        _stream.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 });
        WriteUInt16(Math.Min(f.DelayCs, 65535));
        _stream.Write(new byte[] { 0x00, 0x00 });

        // イメージ記述子
        _stream.WriteByte(0x2C);
        WriteUInt16(f.X);
        WriteUInt16(f.Y);
        WriteUInt16(f.W);
        WriteUInt16(f.H);
        _stream.WriteByte(0x00);

        WriteLzw(f.Pixels);
    }

    /// <summary>LZW 圧縮して、255 バイトずつのブロックで書く。</summary>
    private void WriteLzw(byte[] data)
    {
        const int minCodeSize = 8;
        const int clearCode = 1 << minCodeSize;
        const int endCode = clearCode + 1;

        var output = new List<byte>(data.Length / 2);
        int bitBuffer = 0, bitCount = 0;
        void Emit(int code, int size)
        {
            bitBuffer |= code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                output.Add((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        var dict = new Dictionary<int, int>();
        int codeSize = minCodeSize + 1;
        int maxCode = endCode;
        Emit(clearCode, codeSize);

        int cur = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            int k = data[i];
            int key = (cur << 8) | k;
            if (dict.TryGetValue(key, out int code))
            {
                cur = code;
                continue;
            }
            Emit(cur, codeSize);
            ++maxCode;
            dict[key] = maxCode;
            if (maxCode >= (1 << codeSize)) codeSize++;
            if (maxCode == 4095)
            {
                Emit(clearCode, codeSize);
                dict.Clear();
                codeSize = minCodeSize + 1;
                maxCode = endCode;
            }
            cur = k;
        }
        Emit(cur, codeSize);
        Emit(clearCode, codeSize);
        Emit(endCode, minCodeSize + 1);
        if (bitCount > 0) output.Add((byte)(bitBuffer & 0xFF));

        _stream.WriteByte(minCodeSize);
        for (int i = 0; i < output.Count; i += 255)
        {
            int n = Math.Min(255, output.Count - i);
            _stream.WriteByte((byte)n);
            for (int j = 0; j < n; j++) _stream.WriteByte(output[i + j]);
        }
        _stream.WriteByte(0);
    }

    private void WriteUInt16(int v)
    {
        _stream.WriteByte((byte)(v & 0xFF));
        _stream.WriteByte((byte)((v >> 8) & 0xFF));
    }

    private void WriteAscii(string s)
    {
        foreach (var c in s) _stream.WriteByte((byte)c);
    }

    public void Dispose()
    {
        Flush();
        _stream.WriteByte(0x3B);
        _stream.Flush();
    }
}
