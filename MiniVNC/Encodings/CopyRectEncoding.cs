using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// CopyRect编码解码器（编码类型1）。
/// 从帧缓冲区的已有区域复制像素到目标区域。
/// </summary>
public class CopyRectEncoding : IEncoding
{
    /// <summary>
    /// CopyRect编码类型标识符。
    /// </summary>
    public int EncodingType => EncodingTypes.CopyRect;

    /// <summary>
    /// 异步解码CopyRect编码。
    /// 从流中读取源坐标，返回空数据（复制操作在VncClient中处理）。
    /// </summary>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct = default)
    {
        // CopyRect数据只有4字节：源X(2字节) + 源Y(2字节)
        byte[] coords = await stream.ReadExactlyAsync(4, ct);

        // 返回空数据，实际复制由VncClient处理
        return new byte[rect.Width * rect.Height * 4];
    }

    /// <summary>
    /// 异步从网络流中读取CopyRect编码数据。
    /// CopyRect不传输像素数据，而是返回源坐标信息。
    /// 返回的数组包含4字节：前2字节为源X坐标，后2字节为源Y坐标（大端序）。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="pixelFormat">当前使用的像素格式。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含源坐标的4字节数组。</returns>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat pixelFormat, CancellationToken ct)
    {
        byte[] coords = new byte[4];
        ushort srcX = await stream.ReadUInt16Async(ct);
        ushort srcY = await stream.ReadUInt16Async(ct);
        coords[0] = (byte)(srcX >> 8);
        coords[1] = (byte)srcX;
        coords[2] = (byte)(srcY >> 8);
        coords[3] = (byte)srcY;
        return coords;
    }
}
