using System.Runtime.InteropServices;

namespace MiniVNC.Encodings;

/// <summary>
/// VNC帧缓冲区，统一以 BGRA32（每像素4字节：B,G,R,A）格式存储远程桌面像素。
/// 所有编码解码器都将像素解码为 BGRA32 后写入此缓冲区，渲染层可直接拷贝到 WPF 的 Bgra32 位图。
/// 内部对写入与读取加锁，以保证后台消息循环与 UI 渲染线程之间的安全。
/// </summary>
public sealed class Framebuffer
{
    /// <summary>每像素字节数（固定为 BGRA32 的 4）。</summary>
    public const int BytesPerPixel = 4;

    private readonly byte[] _pixels; // 行优先 BGRA
    private readonly object _sync = new();

    /// <summary>帧缓冲宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>帧缓冲高度（像素）。</summary>
    public int Height { get; }

    /// <summary>
    /// 创建指定尺寸的 BGRA32 帧缓冲。
    /// </summary>
    /// <param name="width">宽度（像素），必须大于0。</param>
    /// <param name="height">高度（像素），必须大于0。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非正时抛出。</exception>
    /// <summary>帧缓冲尺寸上限（像素），防止恶意/异常服务器上报超大尺寸导致溢出或 OOM。</summary>
    private const int MaxDimension = 16384;

    public Framebuffer(int width, int height)
    {
        if (width <= 0 || width > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), $"帧缓冲宽度非法: {width}");
        if (height <= 0 || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(height), $"帧缓冲高度非法: {height}");

        Width = width;
        Height = height;
        // 用 long 计算并裁回 int，避免 32 位乘法回绕（已由上限保证不超过 int 范围）
        long bytes = (long)width * height * BytesPerPixel;
        _pixels = new byte[bytes];
    }

    /// <summary>
    /// 将一块 BGRA32 像素数据写入指定矩形区域。
    /// </summary>
    /// <param name="x">目标左上角X。</param>
    /// <param name="y">目标左上角Y。</param>
    /// <param name="w">区域宽度。</param>
    /// <param name="h">区域高度。</param>
    /// <param name="bgra">BGRA32 源数据，长度至少为 w*h*4。</param>
    /// <exception cref="ArgumentOutOfRangeException">区域超出帧缓冲范围。</exception>
    /// <exception cref="ArgumentException">源数据长度不足。</exception>
    public void UpdateRectBgra32(int x, int y, int w, int h, byte[] bgra)
    {
        if (x < 0 || y < 0 || w < 0 || h < 0 || x + w > Width || y + h > Height)
            throw new ArgumentOutOfRangeException(nameof(bgra), "矩形超出帧缓冲范围");

        int needed = w * h * BytesPerPixel;
        if (bgra.Length < needed)
            throw new ArgumentException($"BGRA数据不足: 需要{needed}字节, 实际{bgra.Length}字节", nameof(bgra));

        int dstStride = Width * BytesPerPixel;
        int srcStride = w * BytesPerPixel;

        lock (_sync)
        {
            for (int row = 0; row < h; row++)
            {
                Array.Copy(bgra, row * srcStride, _pixels, ((y + row) * Width + x) * BytesPerPixel, srcStride);
            }
        }
    }

    /// <summary>
    /// 从帧缓冲已有区域复制像素到目标区域（CopyRect 编码使用）。
    /// </summary>
    public void CopyRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        if (srcX < 0 || srcY < 0 || dstX < 0 || dstY < 0 || w < 0 || h < 0)
            throw new ArgumentOutOfRangeException(nameof(w), "坐标和尺寸必须为非负");
        if (srcX + w > Width || srcY + h > Height || dstX + w > Width || dstY + h > Height)
            throw new ArgumentOutOfRangeException(nameof(w), "矩形超出帧缓冲范围");

        int stride = w * BytesPerPixel;

        lock (_sync)
        {
            // 区域重叠时根据方向选择正/反向复制，避免覆盖源数据
            bool forward = srcY > dstY || (srcY == dstY && srcX >= dstX);
            if (forward)
            {
                for (int row = 0; row < h; row++)
                {
                    Array.Copy(_pixels, ((srcY + row) * Width + srcX) * BytesPerPixel,
                               _pixels, ((dstY + row) * Width + dstX) * BytesPerPixel, stride);
                }
            }
            else
            {
                for (int row = h - 1; row >= 0; row--)
                {
                    Array.Copy(_pixels, ((srcY + row) * Width + srcX) * BytesPerPixel,
                               _pixels, ((dstY + row) * Width + dstX) * BytesPerPixel, stride);
                }
            }
        }
    }

    /// <summary>
    /// 将整个帧缓冲拷贝到非托管内存（通常为 WriteableBitmap 的后备缓冲）。
    /// 按目标 stride 逐行拷贝，线程安全。
    /// </summary>
    /// <param name="dest">目标非托管内存指针。</param>
    /// <param name="destStride">目标每行字节数。</param>
    public void CopyTo(IntPtr dest, int destStride)
    {
        int srcStride = Width * BytesPerPixel;

        lock (_sync)
        {
            if (destStride == srcStride)
            {
                Marshal.Copy(_pixels, 0, dest, _pixels.Length);
            }
            else
            {
                for (int row = 0; row < Height; row++)
                {
                    Marshal.Copy(_pixels, row * srcStride, dest + row * destStride, srcStride);
                }
            }
        }
    }

    /// <summary>
    /// 仅将指定矩形区域拷贝到非托管内存（用于增量刷新，避免整屏拷贝）。
    /// 坐标会被裁剪到帧缓冲范围内。
    /// </summary>
    /// <param name="dest">目标非托管内存指针（整幅位图的后备缓冲）。</param>
    /// <param name="destStride">目标每行字节数。</param>
    /// <param name="x">区域左上角X。</param>
    /// <param name="y">区域左上角Y。</param>
    /// <param name="w">区域宽度。</param>
    /// <param name="h">区域高度。</param>
    public void CopyRegionTo(IntPtr dest, int destStride, int x, int y, int w, int h)
    {
        // 裁剪到边界
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > Width) w = Width - x;
        if (y + h > Height) h = Height - y;
        if (w <= 0 || h <= 0) return;

        int srcStride = Width * BytesPerPixel;
        int rowBytes = w * BytesPerPixel;

        lock (_sync)
        {
            for (int row = 0; row < h; row++)
            {
                int srcOffset = ((y + row) * Width + x) * BytesPerPixel;
                IntPtr destPtr = dest + (y + row) * destStride + x * BytesPerPixel;
                Marshal.Copy(_pixels, srcOffset, destPtr, rowBytes);
            }
        }
    }
}
