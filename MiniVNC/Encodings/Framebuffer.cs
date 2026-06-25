using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// 表示VNC帧缓冲区，存储远程桌面的像素数据。
/// 提供像素更新、区域复制和格式转换功能。
/// </summary>
public class Framebuffer
{
    private readonly byte[] _pixels;

    /// <summary>
    /// 帧缓冲区的宽度（像素）。
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// 帧缓冲区的高度（像素）。
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// 当前使用的像素格式。
    /// </summary>
    public PixelFormat Format { get; }

    /// <summary>
    /// 每个像素占用的字节数。
    /// </summary>
    public int BytesPerPixel => (Format.BitsPerPixel + 7) / 8;

    /// <summary>
    /// 帧缓冲区的原始像素数据数组。
    /// 数据按行优先顺序存储，每行包含 <see cref="Width"/> x <see cref="BytesPerPixel"/> 字节。
    /// </summary>
    public byte[] Pixels => _pixels;

    /// <summary>
    /// 初始化 <see cref="Framebuffer"/> 实例。
    /// </summary>
    /// <param name="width">帧缓冲区宽度（像素）。</param>
    /// <param name="height">帧缓冲区高度（像素）。</param>
    /// <param name="format">像素格式定义。</param>
    public Framebuffer(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
        _pixels = new byte[width * height * ((format.BitsPerPixel + 7) / 8)];
    }

    /// <summary>
    /// 更新指定矩形区域的像素数据。
    /// </summary>
    /// <param name="x">矩形左上角的X坐标。</param>
    /// <param name="y">矩形左上角的Y坐标。</param>
    /// <param name="w">矩形的宽度（像素）。</param>
    /// <param name="h">矩形的高度（像素）。</param>
    /// <param name="pixelData">要写入的像素数据，长度应为 w x h x <see cref="BytesPerPixel"/>。</param>
    /// <exception cref="ArgumentException">像素数据长度与区域大小不匹配时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">坐标或尺寸超出帧缓冲区范围时抛出。</exception>
    public void UpdateRect(int x, int y, int w, int h, byte[] pixelData)
    {
        if (x < 0 || y < 0 || w < 0 || h < 0)
            throw new ArgumentOutOfRangeException("Rectangle coordinates and dimensions must be non-negative.");
        if (x + w > Width || y + h > Height)
            throw new ArgumentOutOfRangeException("Rectangle exceeds framebuffer bounds.");

        int expectedLength = w * h * BytesPerPixel;
        if (pixelData.Length < expectedLength)
            throw new ArgumentException($"Expected {expectedLength} bytes of pixel data, got {pixelData.Length}.", nameof(pixelData));

        int stride = Width * BytesPerPixel;
        int srcStride = w * BytesPerPixel;

        for (int row = 0; row < h; row++)
        {
            int srcOffset = row * srcStride;
            int dstOffset = ((y + row) * Width + x) * BytesPerPixel;
            Array.Copy(pixelData, srcOffset, _pixels, dstOffset, srcStride);
        }
    }

    /// <summary>
    /// 从帧缓冲区的已有区域复制像素到目标区域（CopyRect编码使用）。
    /// </summary>
    /// <param name="srcX">源区域左上角的X坐标。</param>
    /// <param name="srcY">源区域左上角的Y坐标。</param>
    /// <param name="dstX">目标区域左上角的X坐标。</param>
    /// <param name="dstY">目标区域左上角的Y坐标。</param>
    /// <param name="w">要复制的宽度（像素）。</param>
    /// <param name="h">要复制的高度（像素）。</param>
    /// <exception cref="ArgumentOutOfRangeException">坐标或尺寸超出帧缓冲区范围时抛出。</exception>
    public void CopyRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        if (srcX < 0 || srcY < 0 || dstX < 0 || dstY < 0 || w < 0 || h < 0)
            throw new ArgumentOutOfRangeException("Coordinates and dimensions must be non-negative.");
        if (srcX + w > Width || srcY + h > Height || dstX + w > Width || dstY + h > Height)
            throw new ArgumentOutOfRangeException("Rectangle exceeds framebuffer bounds.");

        // 判断复制方向，避免覆盖源数据
        bool copyForward = srcY > dstY || (srcY == dstY && srcX >= dstX);

        int stride = w * BytesPerPixel;

        if (copyForward)
        {
            for (int row = 0; row < h; row++)
            {
                int srcOffset = ((srcY + row) * Width + srcX) * BytesPerPixel;
                int dstOffset = ((dstY + row) * Width + dstX) * BytesPerPixel;
                Array.Copy(_pixels, srcOffset, _pixels, dstOffset, stride);
            }
        }
        else
        {
            // 反向复制，避免覆盖
            for (int row = h - 1; row >= 0; row--)
            {
                int srcOffset = ((srcY + row) * Width + srcX) * BytesPerPixel;
                int dstOffset = ((dstY + row) * Width + dstX) * BytesPerPixel;
                Array.Copy(_pixels, srcOffset, _pixels, dstOffset, stride);
            }
        }
    }

    /// <summary>
    /// 将像素数据转换为WPF <c>WriteableBitmap</c> 使用的Bgra32格式。
    /// 假设源像素格式为32bpp RGB（大端序），转换为Bgra32（Blue-Green-Red-Alpha）。
    /// </summary>
    /// <returns>Bgra32格式的字节数组，长度为 Width x Height x 4。</returns>
    public byte[] ConvertToBgra32()
    {
        byte[] bgra32 = new byte[Width * Height * 4];
        int bpp = BytesPerPixel;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int srcOffset = (y * Width + x) * bpp;
                int dstOffset = (y * Width + x) * 4;

                // 读取像素值
                uint pixel;
                if (bpp == 4)
                {
                    if (Format.BigEndian)
                    {
                        pixel = ((uint)_pixels[srcOffset] << 24) |
                                ((uint)_pixels[srcOffset + 1] << 16) |
                                ((uint)_pixels[srcOffset + 2] << 8) |
                                _pixels[srcOffset + 3];
                    }
                    else
                    {
                        pixel = ((uint)_pixels[srcOffset + 3] << 24) |
                                ((uint)_pixels[srcOffset + 2] << 16) |
                                ((uint)_pixels[srcOffset + 1] << 8) |
                                _pixels[srcOffset];
                    }
                }
                else if (bpp == 2)
                {
                    pixel = Format.BigEndian
                        ? (uint)((_pixels[srcOffset] << 8) | _pixels[srcOffset + 1])
                        : (uint)((_pixels[srcOffset + 1] << 8) | _pixels[srcOffset]);
                }
                else
                {
                    pixel = _pixels[srcOffset];
                }

                // 提取RGB分量
                byte red = (byte)((pixel >> Format.RedShift) & Format.RedMax);
                byte green = (byte)((pixel >> Format.GreenShift) & Format.GreenMax);
                byte blue = (byte)((pixel >> Format.BlueShift) & Format.BlueMax);

                // 如有需要，将颜色值扩展到8位
                if (Format.RedMax > 0 && Format.RedMax < 255) red = (byte)(red * 255 / Format.RedMax);
                if (Format.GreenMax > 0 && Format.GreenMax < 255) green = (byte)(green * 255 / Format.GreenMax);
                if (Format.BlueMax > 0 && Format.BlueMax < 255) blue = (byte)(blue * 255 / Format.BlueMax);

                // 写入Bgra32格式 (B, G, R, A)
                bgra32[dstOffset] = blue;
                bgra32[dstOffset + 1] = green;
                bgra32[dstOffset + 2] = red;
                bgra32[dstOffset + 3] = 0xFF; // Alpha = 255
            }
        }

        return bgra32;
    }

    /// <summary>
    /// 从帧缓冲区读取指定位置的单个像素值。
    /// </summary>
    /// <param name="x">像素的X坐标。</param>
    /// <param name="y">像素的Y坐标。</param>
    /// <returns>32位无符号整数表示的像素值。</returns>
    public uint ReadPixel(int x, int y)
    {
        int offset = (y * Width + x) * BytesPerPixel;

        if (BytesPerPixel == 4)
        {
            return Format.BigEndian
                ? ((uint)_pixels[offset] << 24) | ((uint)_pixels[offset + 1] << 16) | ((uint)_pixels[offset + 2] << 8) | _pixels[offset + 3]
                : ((uint)_pixels[offset + 3] << 24) | ((uint)_pixels[offset + 2] << 16) | ((uint)_pixels[offset + 1] << 8) | _pixels[offset];
        }
        else if (BytesPerPixel == 2)
        {
            return Format.BigEndian
                ? (uint)((_pixels[offset] << 8) | _pixels[offset + 1])
                : (uint)((_pixels[offset + 1] << 8) | _pixels[offset]);
        }
        else
        {
            return _pixels[offset];
        }
    }

    /// <summary>
    /// 将单个像素值写入帧缓冲区的指定位置。
    /// </summary>
    /// <param name="x">像素的X坐标。</param>
    /// <param name="y">像素的Y坐标。</param>
    /// <param name="pixel">要写入的32位像素值。</param>
    public void WritePixel(int x, int y, uint pixel)
    {
        int offset = (y * Width + x) * BytesPerPixel;

        if (BytesPerPixel == 4)
        {
            if (Format.BigEndian)
            {
                _pixels[offset] = (byte)(pixel >> 24);
                _pixels[offset + 1] = (byte)(pixel >> 16);
                _pixels[offset + 2] = (byte)(pixel >> 8);
                _pixels[offset + 3] = (byte)pixel;
            }
            else
            {
                _pixels[offset + 3] = (byte)(pixel >> 24);
                _pixels[offset + 2] = (byte)(pixel >> 16);
                _pixels[offset + 1] = (byte)(pixel >> 8);
                _pixels[offset] = (byte)pixel;
            }
        }
        else if (BytesPerPixel == 2)
        {
            if (Format.BigEndian)
            {
                _pixels[offset] = (byte)(pixel >> 8);
                _pixels[offset + 1] = (byte)pixel;
            }
            else
            {
                _pixels[offset + 1] = (byte)(pixel >> 8);
                _pixels[offset] = (byte)pixel;
            }
        }
        else
        {
            _pixels[offset] = (byte)pixel;
        }
    }
}
