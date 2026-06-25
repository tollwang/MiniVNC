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
        long count = (long)rect.Width * rect.Height; // 用 long，避免 ushort*ushort 溢出 int
        long dataSize = count * bpp;

        // 限制单次读取，防止异常/恶意数据导致超大分配/溢出
        if (dataSize < 0 || dataSize > 256L * 1024 * 1024)
            throw new InvalidOperationException($"Raw 编码数据大小非法: {dataSize} 字节");

        int n = (int)count; // 经上限校验后必定在 int 范围内
        byte[] bgra = new byte[n * 4];
        if (n == 0) return bgra;

        byte[] src = await stream.ReadExactlyAsync((int)dataSize, ct);
        for (int i = 0; i < n; i++)
        {
            uint pixel = format.ReadPixel(src, i * bpp);
            format.WriteBgra32(pixel, bgra, i * 4);
        }
        return bgra;
    }
}
