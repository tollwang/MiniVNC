using System.IO;
using System.IO.Compression;
using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// ZRLE（Zlib Run-Length Encoding）编码解码器（编码类型16），输出 BGRA32。
///
/// <para>关键实现要点：</para>
/// <list type="bullet">
/// <item><b>持久化 zlib 上下文</b>：整条会话共用一个解压流，解压字典跨矩形保持
/// （服务器在每个矩形末尾使用 Z_SYNC_FLUSH，从不 Z_FINISH，故流“永不结束”）。
/// 通过一个可增量喂入压缩字节的 <see cref="FeedStream"/> 实现。</item>
/// <item><b>CPIXEL</b>：当 32bpp 且有效颜色位落在低/高 3 字节时，像素在 ZRLE 中只占 3 字节。</item>
/// <item><b>瓦片</b> 64×64；PackedPalette 每行按字节对齐；游程长度 = 1 + Σ(连续255) + 末字节。</item>
/// </list>
/// </summary>
public sealed class ZrleEncoding : IEncoding, IDisposable
{
    /// <summary>ZRLE 编码类型标识符。</summary>
    public int EncodingType => EncodingTypes.Zrle;

    private FeedStream? _feed;
    private ZLibStream? _inflate;

    /// <summary>
    /// 解码一个 ZRLE 矩形并返回 BGRA32 数据。
    /// </summary>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct)
    {
        // 读取本矩形的压缩长度与压缩数据
        uint compressedLength = await stream.ReadUInt32Async(ct);
        if (compressedLength > 64u * 1024 * 1024)
            throw new IOException($"ZRLE 压缩长度异常: {compressedLength}");

        byte[] compressed = compressedLength == 0
            ? Array.Empty<byte>()
            : await stream.ReadExactlyAsync((int)compressedLength, ct);

        if (_inflate == null)
        {
            _feed = new FeedStream();
            _inflate = new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
        }
        _feed!.Feed(compressed);

        int rw = rect.Width, rh = rect.Height;
        byte[] outBgra = new byte[rw * rh * 4];
        if (rw == 0 || rh == 0) return outBgra;

        int cpixelSize = ComputeCPixelSize(format);
        Stream z = _inflate!;
        byte[] pixelBuf = new byte[4];

        for (int tileY = 0; tileY < rh; tileY += 64)
        {
            int tileH = Math.Min(64, rh - tileY);
            for (int tileX = 0; tileX < rw; tileX += 64)
            {
                int tileW = Math.Min(64, rw - tileX);
                DecodeTile(z, outBgra, rw, tileX, tileY, tileW, tileH, format, cpixelSize, pixelBuf);
            }
        }

        return outBgra;
    }

    private static void DecodeTile(Stream z, byte[] outBgra, int rw,
        int tileX, int tileY, int tileW, int tileH, PixelFormat format, int cpixelSize, byte[] pixelBuf)
    {
        int subencoding = ReadByteStrict(z);
        int total = tileW * tileH;

        // 0: Raw —— 逐像素 CPIXEL
        if (subencoding == 0)
        {
            for (int i = 0; i < total; i++)
            {
                uint pixel = ReadCPixel(z, format, cpixelSize, pixelBuf);
                PutTilePixel(outBgra, rw, tileX, tileY, tileW, i, format, pixel);
            }
            return;
        }

        // 1: Solid —— 单色填充
        if (subencoding == 1)
        {
            uint pixel = ReadCPixel(z, format, cpixelSize, pixelBuf);
            byte[] bgra = new byte[4];
            format.WriteBgra32(pixel, bgra, 0);
            for (int i = 0; i < total; i++)
                PutTilePixelBgra(outBgra, rw, tileX, tileY, tileW, i, bgra);
            return;
        }

        // 2-16: PackedPalette
        if (subencoding >= 2 && subencoding <= 16)
        {
            int paletteSize = subencoding;
            byte[] paletteBgra = ReadPaletteBgra(z, format, cpixelSize, pixelBuf, paletteSize);
            int bitsPerIndex = paletteSize <= 2 ? 1 : (paletteSize <= 4 ? 2 : 4);
            int mask = (1 << bitsPerIndex) - 1;

            // 每行按字节对齐
            int rowBytes = (tileW * bitsPerIndex + 7) / 8;
            byte[] rowBuf = new byte[rowBytes];
            for (int y = 0; y < tileH; y++)
            {
                ReadFull(z, rowBuf, rowBytes);
                int bit = 0;
                for (int x = 0; x < tileW; x++)
                {
                    int byteIndex = bit >> 3;
                    int shift = 8 - bitsPerIndex - (bit & 7);
                    int index = (rowBuf[byteIndex] >> shift) & mask;
                    bit += bitsPerIndex;
                    if (index < paletteSize)
                        PutTilePixelBgra(outBgra, rw, tileX, tileY, tileW, y * tileW + x, paletteBgra.AsSpan(index * 4, 4));
                }
            }
            return;
        }

        // 128: Plain RLE
        if (subencoding == 128)
        {
            int written = 0;
            byte[] bgra = new byte[4];
            while (written < total)
            {
                uint pixel = ReadCPixel(z, format, cpixelSize, pixelBuf);
                int run = ReadRunLength(z);
                format.WriteBgra32(pixel, bgra, 0);
                for (int i = 0; i < run && written < total; i++)
                    PutTilePixelBgra(outBgra, rw, tileX, tileY, tileW, written++, bgra);
            }
            return;
        }

        // 130-255: Palette RLE
        if (subencoding >= 130)
        {
            int paletteSize = subencoding - 128;
            byte[] paletteBgra = ReadPaletteBgra(z, format, cpixelSize, pixelBuf, paletteSize);
            int written = 0;
            while (written < total)
            {
                int index = ReadByteStrict(z);
                int run = 1;
                if ((index & 0x80) != 0)
                {
                    index &= 0x7F;
                    run = ReadRunLength(z);
                }
                if (index >= paletteSize) index = paletteSize - 1;
                var bgra = paletteBgra.AsSpan(index * 4, 4);
                for (int i = 0; i < run && written < total; i++)
                    PutTilePixelBgra(outBgra, rw, tileX, tileY, tileW, written++, bgra);
            }
            return;
        }

        // 17-127、129 未使用
        throw new NotSupportedException($"不支持的 ZRLE 子编码: {subencoding}");
    }

    private static byte[] ReadPaletteBgra(Stream z, PixelFormat format, int cpixelSize, byte[] pixelBuf, int paletteSize)
    {
        byte[] paletteBgra = new byte[paletteSize * 4];
        for (int i = 0; i < paletteSize; i++)
        {
            uint pixel = ReadCPixel(z, format, cpixelSize, pixelBuf);
            format.WriteBgra32(pixel, paletteBgra, i * 4);
        }
        return paletteBgra;
    }

    /// <summary>
    /// 计算 CPIXEL 字节数。32bpp 且有效颜色位都落在低 24 位时为 3 字节，否则为 PIXEL 原始字节数。
    /// 客户端协商的目标格式（R16/G8/B0, max255）属于“低 3 字节”情形。
    /// </summary>
    private static int ComputeCPixelSize(PixelFormat f)
    {
        if (f.BitsPerPixel == 32 && f.Depth <= 24)
        {
            int maxBit = 0;
            maxBit = Math.Max(maxBit, BitLength(f.RedMax) + f.RedShift);
            maxBit = Math.Max(maxBit, BitLength(f.GreenMax) + f.GreenShift);
            maxBit = Math.Max(maxBit, BitLength(f.BlueMax) + f.BlueShift);
            if (maxBit <= 24) return 3; // 有效位都在低 24 位 → 低 3 字节 CPIXEL
        }
        return f.BytesPerPixel;
    }

    private static int BitLength(int value)
    {
        int bits = 0;
        while (value > 0) { bits++; value >>= 1; }
        return bits;
    }

    /// <summary>
    /// 读取一个 CPIXEL 并返回与目标格式一致的像素数值。
    /// </summary>
    private static uint ReadCPixel(Stream z, PixelFormat format, int cpixelSize, byte[] tmp)
    {
        ReadFull(z, tmp, cpixelSize);
        if (cpixelSize == 3)
        {
            // 低 3 字节情形：大端 [高..低]，组装为 0x00RRGGBB 与格式位移一致
            return ((uint)tmp[0] << 16) | ((uint)tmp[1] << 8) | tmp[2];
        }
        return format.ReadPixel(tmp, 0);
    }

    /// <summary>
    /// 读取 ZRLE 游程长度：1 + Σ(连续的255) + 末字节(&lt;255)。
    /// </summary>
    private static int ReadRunLength(Stream z)
    {
        int run = 1;
        int b;
        while ((b = ReadByteStrict(z)) == 255)
            run += 255;
        run += b;
        return run;
    }

    private static void PutTilePixel(byte[] outBgra, int rw, int tileX, int tileY, int tileW, int linear, PixelFormat format, uint pixel)
    {
        int x = linear % tileW;
        int y = linear / tileW;
        format.WriteBgra32(pixel, outBgra, ((tileY + y) * rw + (tileX + x)) * 4);
    }

    private static void PutTilePixelBgra(byte[] outBgra, int rw, int tileX, int tileY, int tileW, int linear, ReadOnlySpan<byte> bgra)
    {
        int x = linear % tileW;
        int y = linear / tileW;
        int o = ((tileY + y) * rw + (tileX + x)) * 4;
        outBgra[o] = bgra[0];
        outBgra[o + 1] = bgra[1];
        outBgra[o + 2] = bgra[2];
        outBgra[o + 3] = bgra[3];
    }

    private static int ReadByteStrict(Stream z)
    {
        int b = z.ReadByte();
        if (b < 0) throw new IOException("ZRLE: 解压数据提前结束");
        return b;
    }

    private static void ReadFull(Stream z, byte[] buffer, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = z.Read(buffer, got, count - got);
            if (n <= 0) throw new IOException("ZRLE: 解压数据不足");
            got += n;
        }
    }

    /// <summary>释放持久化的解压流。</summary>
    public void Dispose()
    {
        _inflate?.Dispose();
        _feed?.Dispose();
        _inflate = null;
        _feed = null;
    }

    /// <summary>
    /// 可增量喂入压缩字节的只读流。供持久化 <see cref="ZLibStream"/> 作为输入源，
    /// 解压时按需从已喂入的数据中读取；数据不足时返回0（调用方保证在解码本矩形前已喂入足量数据）。
    /// </summary>
    private sealed class FeedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _current;
        private int _pos;

        public void Feed(byte[] data)
        {
            if (data.Length > 0) _chunks.Enqueue(data);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0)
            {
                if (_current == null || _pos >= _current.Length)
                {
                    if (_chunks.Count == 0) break;
                    _current = _chunks.Dequeue();
                    _pos = 0;
                }
                int n = Math.Min(count, _current.Length - _pos);
                Array.Copy(_current, _pos, buffer, offset, n);
                _pos += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
