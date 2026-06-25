using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// Raw 编码解码器（编码类型0）。直接传输未压缩的原始像素数据，逐像素转换为 BGRA32。
/// </summary>
public sealed class RawEncoding : IEncoding
{
    /// <summary>Raw 编码类型标识符。</summary>
    public int EncodingType => EncodingTypes.Raw;

    /// <summary>
    /// 异步解码 Raw 编码并返回 BGRA32 数据。
    /// </summary>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct)
    {
        int bpp = format.BytesPerPixel;
        int count = rect.Width * rect.Height;
        long dataSize = (long)count * bpp;

        // 限制单次读取，防止异常/恶意数据导致超大分配
        if (dataSize < 0 || dataSize > 64L * 1024 * 1024)
            throw new InvalidOperationException($"Raw 编码数据大小非法: {dataSize} 字节");

        byte[] bgra = new byte[count * 4];
        if (count == 0) return bgra;

        byte[] src = await stream.ReadExactlyAsync((int)dataSize, ct);
        for (int i = 0; i < count; i++)
        {
            uint pixel = format.ReadPixel(src, i * bpp);
            format.WriteBgra32(pixel, bgra, i * 4);
        }
        return bgra;
    }
}
