using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MiniVNC.Utils;

/// <summary>
/// 字节数组扩展方法 - 提供大端序读写和位操作。
/// VNC协议使用大端序（网络字节序）传输所有多字节数值。
/// </summary>
public static class ByteExtensions
{
    /// <summary>
    /// 从大端序字节数组中读取 <see cref="ushort"/> 值。
    /// </summary>
    /// <param name="buffer">源字节数组</param>
    /// <param name="offset">读取起始偏移量</param>
    /// <returns>大端序转换后的ushort值</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移量超出数组边界</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUInt16BE(this byte[] buffer, int offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || offset + 2 > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
    }

    /// <summary>
    /// 从大端序字节数组中读取 <see cref="uint"/> 值。
    /// </summary>
    /// <param name="buffer">源字节数组</param>
    /// <param name="offset">读取起始偏移量</param>
    /// <returns>大端序转换后的uint值</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移量超出数组边界</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32BE(this byte[] buffer, int offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || offset + 4 > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
    }

    /// <summary>
    /// 以大端序格式将 <see cref="ushort"/> 值写入字节数组。
    /// </summary>
    /// <param name="buffer">目标字节数组</param>
    /// <param name="value">要写入的ushort值</param>
    /// <param name="offset">写入起始偏移量</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移量超出数组边界</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt16BE(this byte[] buffer, ushort value, int offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || offset + 2 > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), value);
    }

    /// <summary>
    /// 以大端序格式将 <see cref="uint"/> 值写入字节数组。
    /// </summary>
    /// <param name="buffer">目标字节数组</param>
    /// <param name="value">要写入的uint值</param>
    /// <param name="offset">写入起始偏移量</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移量超出数组边界</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt32BE(this byte[] buffer, uint value, int offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || offset + 4 > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    /// <summary>
    /// 反转字节数组中每个字节的位序。
    /// 位0与位7交换，位1与位6交换，以此类推。
    /// </summary>
    /// <param name="data">源字节数组</param>
    /// <returns>位序反转后的新字节数组</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/>为null</exception>
    public static byte[] ReverseBits(this byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] result = new byte[data.Length];

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            byte reversed = 0;

            for (int j = 0; j < 8; j++)
            {
                reversed |= (byte)(((b >> j) & 1) << (7 - j));
            }

            result[i] = reversed;
        }

        return result;
    }

    /// <summary>
    /// 将字节数组的指定区域复制到新数组。
    /// </summary>
    /// <param name="buffer">源字节数组</param>
    /// <param name="offset">复制起始偏移量</param>
    /// <param name="count">要复制的字节数</param>
    /// <returns>新的字节数组，包含复制的数据</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移量或数量超出数组边界</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] ReadBytes(this byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        byte[] result = new byte[count];
        Buffer.BlockCopy(buffer, offset, result, 0, count);
        return result;
    }

    /// <summary>
    /// 将源数组数据写入目标数组的指定位置。
    /// </summary>
    /// <param name="buffer">目标字节数组</param>
    /// <param name="source">源字节数组</param>
    /// <param name="offset">写入起始偏移量</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>或<paramref name="source"/>为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移量超出数组边界</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBytes(this byte[] buffer, byte[] source, int offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(source);

        if (offset < 0 || offset + source.Length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        Buffer.BlockCopy(source, 0, buffer, offset, source.Length);
    }
}
