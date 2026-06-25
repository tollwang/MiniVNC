using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// Hextile编码解码器。编码类型为5。
/// 
/// Hextile是Mac屏幕共享默认使用的编码方式。
/// 它将矩形区域分割为16×16像素的瓦片（tile），每个瓦片独立编码。
/// 每个瓦片使用子编码标志决定如何压缩，支持以下特性：
/// 
/// - 背景色指定：瓦片使用单一背景色填充
/// - 前景色指定：子矩形使用单一前景色
/// - 任意子矩形：瓦片包含一个或多个子矩形区域
/// - 彩色子矩形：每个子矩形有自己的颜色
/// 
/// Hextile子编码标志位（按位或组合）：
/// bit 0: Raw               - 直接传输原始像素
/// bit 1: BackgroundSpecified - 传输新背景色
/// bit 2: ForegroundSpecified - 传输新前景色
/// bit 3: AnySubrects       - 瓦片包含子矩形
/// bit 4: ColoredSubrects   - 子矩形各自有颜色
/// </summary>
public class HextileEncoding : IEncoding
{
    /// <summary>
    /// Hextile编码的类型标识符，值为5。
    /// </summary>
    public int EncodingType => EncodingTypes.Hextile;

    // 子编码标志位
    private const byte RawFlag = 0x01;
    private const byte BackgroundSpecifiedFlag = 0x02;
    private const byte ForegroundSpecifiedFlag = 0x04;
    private const byte AnySubrectsFlag = 0x08;
    private const byte ColoredSubrectsFlag = 0x10;

    /// <summary>
    /// 从网络流中读取Hextile编码数据并解码到帧缓冲区。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="framebuffer">要更新的帧缓冲区实例。</param>
    public void Decode(VncStream stream, FramebufferRect rect, Framebuffer framebuffer)
    {
        int bytesPerPixel = framebuffer.BytesPerPixel;
        uint backgroundPixel = 0;
        uint foregroundPixel = 0;

        // 按16×16瓦片迭代
        for (int tileY = rect.Y; tileY < rect.Y + rect.Height; tileY += 16)
        {
            for (int tileX = rect.X; tileX < rect.X + rect.Width; tileX += 16)
            {
                int tileW = Math.Min(16, rect.X + rect.Width - tileX);
                int tileH = Math.Min(16, rect.Y + rect.Height - tileY);

                // 读取子编码标志
                byte subencoding = stream.ReadByte();

                // Raw模式：直接读取原始像素
                if ((subencoding & RawFlag) != 0)
                {
                    ReadRawTile(stream, framebuffer, tileX, tileY, tileW, tileH);
                    continue;
                }

                // BackgroundSpecified：读取新背景色
                if ((subencoding & BackgroundSpecifiedFlag) != 0)
                {
                    backgroundPixel = ReadPixelValue(stream, bytesPerPixel);
                }

                // 用背景色填充整个瓦片
                FillRect(framebuffer, tileX, tileY, tileW, tileH, backgroundPixel);

                // AnySubrects：瓦片包含子矩形
                if ((subencoding & AnySubrectsFlag) != 0)
                {
                    byte numSubrects = stream.ReadByte();

                    // 判断子矩形是否有各自的颜色
                    bool coloredSubrects = (subencoding & ColoredSubrectsFlag) != 0;

                    for (int i = 0; i < numSubrects; i++)
                    {
                        // 如果是彩色子矩形，先读取颜色
                        if (coloredSubrects)
                        {
                            foregroundPixel = ReadPixelValue(stream, bytesPerPixel);
                        }
                        else if ((subencoding & ForegroundSpecifiedFlag) != 0 && i == 0)
                        {
                            // ForegroundSpecified且第一个子矩形
                            foregroundPixel = ReadPixelValue(stream, bytesPerPixel);
                        }

                        // 读取子矩形位置和尺寸（压缩格式）
                        // 格式：xy(1字节) + wh(1字节)
                        // x = high nibble * 16, y = low nibble * 16
                        // w = high nibble + 1, h = low nibble + 1
                        byte xy = stream.ReadByte();
                        byte wh = stream.ReadByte();

                        int subX = tileX + ((xy >> 4) & 0x0F);
                        int subY = tileY + (xy & 0x0F);
                        int subW = ((wh >> 4) & 0x0F) + 1;
                        int subH = (wh & 0x0F) + 1;

                        FillRect(framebuffer, subX, subY, subW, subH, foregroundPixel);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 读取Raw模式的瓦片像素数据。
    /// </summary>
    private static void ReadRawTile(VncStream stream, Framebuffer framebuffer, int x, int y, int w, int h)
    {
        int bytesPerPixel = framebuffer.BytesPerPixel;
        int dataSize = w * h * bytesPerPixel;
        byte[] pixelData = stream.ReadExactly(dataSize);
        framebuffer.UpdateRect(x, y, w, h, pixelData);
    }

    /// <summary>
    /// 从流中读取一个像素值。
    /// </summary>
    private static uint ReadPixelValue(VncStream stream, int bytesPerPixel)
    {
        byte[] bytes = stream.ReadExactly(bytesPerPixel);

        return bytesPerPixel switch
        {
            4 => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3],
            3 => ((uint)bytes[0] << 16) | ((uint)bytes[1] << 8) | bytes[2],
            2 => (uint)((bytes[0] << 8) | bytes[1]),
            1 => bytes[0],
            _ => throw new NotSupportedException($"Unsupported bytes per pixel: {bytesPerPixel}")
        };
    }

    /// <summary>
    /// 用指定像素值填充帧缓冲区中的矩形区域。
    /// </summary>
    private static void FillRect(Framebuffer framebuffer, int x, int y, int w, int h, uint pixel)
    {
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                framebuffer.WritePixel(x + col, y + row, pixel);
            }
        }
    }

    /// <summary>
    /// 异步从网络流中读取Hextile编码数据。
    /// 使用临时帧缓冲区进行同步解码，然后返回像素数据。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="pixelFormat">当前使用的像素格式。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>解码后的像素数据字节数组。</returns>
    public Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat pixelFormat, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var tempFramebuffer = new Framebuffer(rect.Width, rect.Height, pixelFormat);
            var adjustedRect = rect with { X = 0, Y = 0 };
            Decode(stream, adjustedRect, tempFramebuffer);
            return tempFramebuffer.Pixels;
        }, ct);
    }
}
