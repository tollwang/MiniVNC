using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace MiniVNC.Utils;

/// <summary>
/// 位图工具类 - 提供像素格式转换和WPF <see cref="WriteableBitmap"/> 操作。
/// </summary>
public static class BitmapUtils
{
    /// <summary>
    /// 标准的32bpp BGRA像素格式常量。
    /// </summary>
    public static readonly PixelFormat Bgra32Format = PixelFormats.Bgra32;

    /// <summary>
    /// 标准的32bpp RGB像素格式常量。
    /// </summary>
    public static readonly PixelFormat Bgr32Format = PixelFormats.Bgr32;

    /// <summary>
    /// 创建指定尺寸的WPF <see cref="WriteableBitmap"/>。
    /// </summary>
    /// <param name="width">位图宽度（像素）</param>
    /// <param name="height">位图高度（像素）</param>
    /// <returns>新创建的WriteableBitmap实例</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/>或<paramref name="height"/>小于等于0</exception>
    public static WriteableBitmap CreateBitmap(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "宽度必须大于0");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "高度必须大于0");
        }

        return new WriteableBitmap(
            width,
            height,
            96.0,
            96.0,
            Bgra32Format,
            null);
    }

    /// <summary>
    /// 将帧缓冲数据写入 <see cref="WriteableBitmap"/> 的后备缓冲区。
    /// 数据以Bgra32格式写入，支持跨行stride处理。
    /// </summary>
    /// <param name="bitmap">目标WriteableBitmap</param>
    /// <param name="pixels">源像素数据数组（32bpp，每像素4字节）</param>
    /// <param name="x">目标区域左上角X坐标</param>
    /// <param name="y">目标区域左上角Y坐标</param>
    /// <param name="width">要写入的区域宽度</param>
    /// <param name="height">要写入的区域高度</param>
    /// <exception cref="ArgumentNullException"><paramref name="bitmap"/>或<paramref name="pixels"/>为null</exception>
    /// <exception cref="ArgumentException">像素数据长度不足</exception>
    /// <exception cref="ArgumentOutOfRangeException">坐标或尺寸超出位图边界</exception>
    public static void WriteToBitmap(WriteableBitmap bitmap, byte[] pixels, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(pixels);

        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException("坐标和尺寸必须为正数");
        }

        if (x + width > bitmap.PixelWidth || y + height > bitmap.PixelHeight)
        {
            throw new ArgumentOutOfRangeException("写入区域超出位图边界");
        }

        int expectedLength = width * height * 4;
        if (pixels.Length < expectedLength)
        {
            throw new ArgumentException(
                $"像素数据长度不足: 需要{expectedLength}字节，实际{pixels.Length}字节",
                nameof(pixels));
        }

        int bitmapStride = bitmap.BackBufferStride;

        bitmap.Lock();
        try
        {
            unsafe
            {
                byte* backBufferPtr = (byte*)bitmap.BackBuffer.ToPointer();

                for (int row = 0; row < height; row++)
                {
                    byte* destRow = backBufferPtr + ((y + row) * bitmapStride) + (x * 4);
                    int sourceOffset = row * width * 4;

                    for (int col = 0; col < width * 4; col++)
                    {
                        destRow[col] = pixels[sourceOffset + col];
                    }
                }
            }

            bitmap.AddDirtyRect(new Int32Rect(x, y, width, height));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    /// <summary>
    /// 将整个像素数组写入 <see cref="WriteableBitmap"/> 的后备缓冲区。
    /// 像素数据必须覆盖完整的位图尺寸。
    /// </summary>
    /// <param name="bitmap">目标WriteableBitmap</param>
    /// <param name="pixels">源像素数据数组（32bpp，每像素4字节）</param>
    /// <exception cref="ArgumentNullException"><paramref name="bitmap"/>或<paramref name="pixels"/>为null</exception>
    /// <exception cref="ArgumentException">像素数据长度不匹配</exception>
    public static void WriteToBitmap(WriteableBitmap bitmap, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(pixels);

        int expectedLength = bitmap.PixelWidth * bitmap.PixelHeight * 4;

        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"像素数据长度不匹配: 需要{expectedLength}字节，实际{pixels.Length}字节",
                nameof(pixels));
        }

        WriteToBitmap(bitmap, pixels, 0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    /// <summary>
    /// 将像素数据转换为32bpp BGRA格式（大端序兼容）。
    /// VNC服务器通常以大端序发送像素数据，此方法将其转换为WPF可用的格式。
    /// </summary>
    /// <param name="pixels">源像素数据</param>
    /// <param name="sourceFormat">源像素格式信息</param>
    /// <returns>转换后的BGRA32像素数据</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pixels"/>或<paramref name="sourceFormat"/>为null</exception>
    /// <exception cref="NotSupportedException">不支持的像素格式</exception>
    public static byte[] ConvertToBgra32(byte[] pixels, PixelFormatInfo sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(sourceFormat);

        if (sourceFormat.BitsPerPixel == 32 &&
            sourceFormat.RedMax == 255 &&
            sourceFormat.GreenMax == 255 &&
            sourceFormat.BlueMax == 255 &&
            sourceFormat.RedShift == 16 &&
            sourceFormat.GreenShift == 8 &&
            sourceFormat.BlueShift == 0)
        {
            // 已经是目标格式，添加Alpha通道
            return AddAlphaChannel(pixels, sourceFormat.BigEndian);
        }

        // 通用格式转换
        return ConvertPixelFormat(pixels, sourceFormat, new PixelFormatInfo(32, 24, true, true, 255, 255, 255, 16, 8, 0));
    }

    /// <summary>
    /// 为32bpp RGB数据添加完全不透明的Alpha通道（255），转换为BGRA32。
    /// </summary>
    /// <param name="rgbPixels">源RGB像素数据</param>
    /// <param name="isBigEndian">源数据是否为大端序</param>
    /// <returns>BGRA32格式像素数据</returns>
    private static byte[] AddAlphaChannel(byte[] rgbPixels, bool isBigEndian)
    {
        int pixelCount = rgbPixels.Length / 4;
        byte[] result = new byte[pixelCount * 4];

        for (int i = 0; i < pixelCount; i++)
        {
            int srcIdx = i * 4;
            int dstIdx = i * 4;

            if (isBigEndian)
            {
                // 大端序 ARGB -> BGRA
                result[dstIdx + 0] = rgbPixels[srcIdx + 3]; // B
                result[dstIdx + 1] = rgbPixels[srcIdx + 2]; // G
                result[dstIdx + 2] = rgbPixels[srcIdx + 1]; // R
                result[dstIdx + 3] = 0xFF;                    // A
            }
            else
            {
                // 小端序 BGR -> BGRA
                result[dstIdx + 0] = rgbPixels[srcIdx + 0]; // B
                result[dstIdx + 1] = rgbPixels[srcIdx + 1]; // G
                result[dstIdx + 2] = rgbPixels[srcIdx + 2]; // R
                result[dstIdx + 3] = 0xFF;                    // A
            }
        }

        return result;
    }

    /// <summary>
    /// 在两个像素格式之间转换像素数据。
    /// </summary>
    /// <param name="pixels">源像素数据</param>
    /// <param name="source">源像素格式</param>
    /// <param name="target">目标像素格式</param>
    /// <returns>转换后的像素数据</returns>
    private static byte[] ConvertPixelFormat(byte[] pixels, PixelFormatInfo source, PixelFormatInfo target)
    {
        int bytesPerSourcePixel = (source.BitsPerPixel + 7) / 8;
        int bytesPerTargetPixel = (target.BitsPerPixel + 7) / 8;
        int pixelCount = pixels.Length / bytesPerSourcePixel;

        byte[] result = new byte[pixelCount * bytesPerTargetPixel];

        for (int i = 0; i < pixelCount; i++)
        {
            int srcIdx = i * bytesPerSourcePixel;

            // 读取源像素值
            uint pixelValue = ReadPixelValue(pixels, srcIdx, source);

            // 提取RGB分量
            byte r = (byte)((pixelValue >> source.RedShift) & source.RedMax);
            byte g = (byte)((pixelValue >> source.GreenShift) & source.GreenMax);
            byte b = (byte)((pixelValue >> source.BlueShift) & source.BlueMax);

            // 归一化到8位
            if (source.RedMax != 255) r = ScaleTo8Bit(r, source.RedMax);
            if (source.GreenMax != 255) g = ScaleTo8Bit(g, source.GreenMax);
            if (source.BlueMax != 255) b = ScaleTo8Bit(b, source.BlueMax);

            // 写入目标像素（BGRA32）
            int dstIdx = i * bytesPerTargetPixel;
            result[dstIdx + 0] = b;
            result[dstIdx + 1] = g;
            result[dstIdx + 2] = r;
            result[dstIdx + 3] = 0xFF; // Alpha
        }

        return result;
    }

    /// <summary>
    /// 从字节数组中读取指定格式的像素值。
    /// </summary>
    private static uint ReadPixelValue(byte[] pixels, int offset, PixelFormatInfo format)
    {
        int bytesPerPixel = (format.BitsPerPixel + 7) / 8;

        return bytesPerPixel switch
        {
            1 => pixels[offset],
            2 => format.BigEndian
                ? (uint)((pixels[offset] << 8) | pixels[offset + 1])
                : (uint)(pixels[offset] | (pixels[offset + 1] << 8)),
            3 => format.BigEndian
                ? (uint)((pixels[offset] << 16) | (pixels[offset + 1] << 8) | pixels[offset + 2])
                : (uint)(pixels[offset] | (pixels[offset + 1] << 8) | (pixels[offset + 2] << 16)),
            4 => format.BigEndian
                ? (uint)((pixels[offset] << 24) | (pixels[offset + 1] << 16) | (pixels[offset + 2] << 8) | pixels[offset + 3])
                : (uint)(pixels[offset] | (pixels[offset + 1] << 8) | (pixels[offset + 2] << 16) | (pixels[offset + 3] << 24)),
            _ => throw new NotSupportedException($"不支持的像素位数: {format.BitsPerPixel}")
        };
    }

    /// <summary>
    /// 将颜色分量从指定最大值范围缩放到8位（0-255）。
    /// </summary>
    /// <param name="value">颜色分量值</param>
    /// <param name="max">最大值</param>
    /// <returns>缩放后的8位值</returns>
    private static byte ScaleTo8Bit(byte value, ushort max)
    {
        if (max == 255) return value;
        if (max == 0) return 0;

        return (byte)((value * 255 + max / 2) / max);
    }
}

/// <summary>
/// 像素格式信息 - 描述VNC像素格式的结构。
/// </summary>
/// <param name="bitsPerPixel">每像素位数</param>
/// <param name="depth">色彩深度</param>
/// <param name="bigEndian">是否大端序</param>
/// <param name="trueColor">是否真彩色</param>
/// <param name="redMax">红色最大值</param>
/// <param name="greenMax">绿色最大值</param>
/// <param name="blueMax">蓝色最大值</param>
/// <param name="redShift">红色位移</param>
/// <param name="greenShift">绿色位移</param>
/// <param name="blueShift">蓝色位移</param>
public sealed record PixelFormatInfo(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColor,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift);
