using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// Raw编码解码器（编码类型0）。
/// 直接传输未压缩的原始像素数据。
/// </summary>
public class RawEncoding : IEncoding
{
    /// <summary>
    /// Raw编码类型标识符。
    /// </summary>
    public int EncodingType => EncodingTypes.Raw;

    /// <summary>
    /// 异步解码Raw编码的像素数据。
    /// </summary>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct = default)
    {
        int bytesPerPixel = (format.BitsPerPixel + 7) / 8;
        int dataSize = rect.Width * rect.Height * bytesPerPixel;

        // 限制单次读取最大16MB，防止恶意数据导致OOM
        if (dataSize > 16 * 1024 * 1024)
            throw new InvalidOperationException($"Raw编码数据过大: {dataSize}字节");

        byte[] pixelData = await stream.ReadExactlyAsync(dataSize, ct);
        return ConvertToBgra32(pixelData, rect.Width * rect.Height, format);
    }

    /// <summary>
    /// 将像素数据从服务器格式转换为Bgra32格式。
    /// </summary>
    private static byte[] ConvertToBgra32(byte[] pixelData, int pixelCount, PixelFormat format)
    {
        byte[] bgra32 = new byte[pixelCount * 4];
        int bpp = (format.BitsPerPixel + 7) / 8;

        for (int i = 0; i < pixelCount; i++)
        {
            int srcOffset = i * bpp;
            int dstOffset = i * 4;

            uint pixel = bpp switch
            {
                4 => format.BigEndian
                    ? ((uint)pixelData[srcOffset] << 24) | ((uint)pixelData[srcOffset + 1] << 16) | ((uint)pixelData[srcOffset + 2] << 8) | pixelData[srcOffset + 3]
                    : ((uint)pixelData[srcOffset + 3] << 24) | ((uint)pixelData[srcOffset + 2] << 16) | ((uint)pixelData[srcOffset + 1] << 8) | pixelData[srcOffset],
                3 => ((uint)pixelData[srcOffset] << 16) | ((uint)pixelData[srcOffset + 1] << 8) | pixelData[srcOffset + 2],
                2 => format.BigEndian
                    ? (uint)((pixelData[srcOffset] << 8) | pixelData[srcOffset + 1])
                    : (uint)((pixelData[srcOffset + 1] << 8) | pixelData[srcOffset]),
                1 => pixelData[srcOffset],
                _ => 0
            };

            byte red = (byte)((pixel >> format.RedShift) & format.RedMax);
            byte green = (byte)((pixel >> format.GreenShift) & format.GreenMax);
            byte blue = (byte)((pixel >> format.BlueShift) & format.BlueMax);

            // 扩展到8位
            if (format.RedMax > 0 && format.RedMax < 255) red = (byte)(red * 255 / format.RedMax);
            if (format.GreenMax > 0 && format.GreenMax < 255) green = (byte)(green * 255 / format.GreenMax);
            if (format.BlueMax > 0 && format.BlueMax < 255) blue = (byte)(blue * 255 / format.BlueMax);

            bgra32[dstOffset] = blue;
            bgra32[dstOffset + 1] = green;
            bgra32[dstOffset + 2] = red;
            bgra32[dstOffset + 3] = 0xFF;
        }

        return bgra32;
    }

    /// <summary>
    /// 异步从网络流中读取Raw编码的像素数据。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="pixelFormat">当前使用的像素格式。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>原始像素数据字节数组。</returns>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat pixelFormat, CancellationToken ct)
    {
        int bytesPerPixel = pixelFormat.BitsPerPixel / 8;
        int dataSize = rect.Width * rect.Height * bytesPerPixel;
        return await stream.ReadExactlyAsync(dataSize, ct);
    }
}
