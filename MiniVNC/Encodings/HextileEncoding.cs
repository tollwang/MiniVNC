using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// Hextile 编码解码器（编码类型5），macOS 屏幕共享常用编码。
/// 将矩形分割为 16×16 瓦片，每个瓦片按子编码标志独立编码。解码结果直接输出为 BGRA32。
///
/// 子编码标志位（按位或组合）：
/// bit0 Raw、bit1 BackgroundSpecified、bit2 ForegroundSpecified、bit3 AnySubrects、bit4 ColoredSubrects。
///
/// 瓦片内字节顺序（非 Raw）：[背景色?][前景色?][子矩形数?][子矩形...]，
/// 其中前景色（若指定）必须在“子矩形数”之前读取。
/// </summary>
public sealed class HextileEncoding : IEncoding
{
    /// <summary>Hextile 编码类型标识符。</summary>
    public int EncodingType => EncodingTypes.Hextile;

    private const byte RawFlag = 0x01;
    private const byte BackgroundSpecifiedFlag = 0x02;
    private const byte ForegroundSpecifiedFlag = 0x04;
    private const byte AnySubrectsFlag = 0x08;
    private const byte ColoredSubrectsFlag = 0x10;

    /// <summary>
    /// 异步解码 Hextile 编码，返回整个矩形的 BGRA32 数据。
    /// </summary>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct)
    {
        int bpp = format.BytesPerPixel;
        int rw = rect.Width;
        int rh = rect.Height;
        byte[] outBgra = new byte[rw * rh * 4];

        uint background = 0;
        uint foreground = 0;

        for (int tileY = 0; tileY < rh; tileY += 16)
        {
            int tileH = Math.Min(16, rh - tileY);
            for (int tileX = 0; tileX < rw; tileX += 16)
            {
                ct.ThrowIfCancellationRequested();
                int tileW = Math.Min(16, rw - tileX);

                byte sub = await stream.ReadByteAsync(ct);

                // Raw 瓦片：直接读取原始像素
                if ((sub & RawFlag) != 0)
                {
                    byte[] raw = await stream.ReadExactlyAsync(tileW * tileH * bpp, ct);
                    for (int j = 0; j < tileH; j++)
                    {
                        for (int k = 0; k < tileW; k++)
                        {
                            uint p = format.ReadPixel(raw, (j * tileW + k) * bpp);
                            format.WriteBgra32(p, outBgra, ((tileY + j) * rw + (tileX + k)) * 4);
                        }
                    }
                    continue;
                }

                // 背景色
                if ((sub & BackgroundSpecifiedFlag) != 0)
                    background = await ReadPixelAsync(stream, format, ct);

                FillBgra(outBgra, rw, rh, tileX, tileY, tileW, tileH, background, format);

                // 前景色（必须在子矩形数之前读取）
                if ((sub & ForegroundSpecifiedFlag) != 0)
                    foreground = await ReadPixelAsync(stream, format, ct);

                // 子矩形
                if ((sub & AnySubrectsFlag) != 0)
                {
                    byte numSubrects = await stream.ReadByteAsync(ct);
                    bool colored = (sub & ColoredSubrectsFlag) != 0;

                    for (int s = 0; s < numSubrects; s++)
                    {
                        if (colored)
                            foreground = await ReadPixelAsync(stream, format, ct);

                        byte xy = await stream.ReadByteAsync(ct);
                        byte wh = await stream.ReadByteAsync(ct);

                        int sx = tileX + ((xy >> 4) & 0x0F);
                        int sy = tileY + (xy & 0x0F);
                        int sw = ((wh >> 4) & 0x0F) + 1;
                        int sh = (wh & 0x0F) + 1;

                        FillBgra(outBgra, rw, rh, sx, sy, sw, sh, foreground, format);
                    }
                }
            }
        }

        return outBgra;
    }

    /// <summary>
    /// 从流中异步读取一个服务器像素值。
    /// </summary>
    private static async Task<uint> ReadPixelAsync(VncStream stream, PixelFormat format, CancellationToken ct)
    {
        byte[] bytes = await stream.ReadExactlyAsync(format.BytesPerPixel, ct);
        return format.ReadPixel(bytes, 0);
    }

    /// <summary>
    /// 用单一像素颜色填充 BGRA 输出缓冲中的一个子区域（含边界裁剪）。
    /// </summary>
    private static void FillBgra(byte[] dst, int rw, int rh, int x, int y, int w, int h, uint serverPixel, PixelFormat format)
    {
        // 边界裁剪，防止异常子矩形越界
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > rw) w = rw - x;
        if (y + h > rh) h = rh - y;
        if (w <= 0 || h <= 0) return;

        byte[] one = new byte[4];
        format.WriteBgra32(serverPixel, one, 0);

        for (int j = 0; j < h; j++)
        {
            int rowOff = ((y + j) * rw + x) * 4;
            for (int i = 0; i < w; i++)
            {
                int o = rowOff + i * 4;
                dst[o] = one[0];
                dst[o + 1] = one[1];
                dst[o + 2] = one[2];
                dst[o + 3] = one[3];
            }
        }
    }
}
