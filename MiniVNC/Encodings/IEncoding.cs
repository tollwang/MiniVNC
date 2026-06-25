using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// 定义VNC RFB协议中编码解码器的接口。
/// 每种编码类型（Raw、Hextile、ZRLE等）都实现此接口来解码服务器发送的像素数据。
/// </summary>
public interface IEncoding
{
    /// <summary>
    /// 获取此编码器支持的编码类型标识符。
    /// 对应RFB协议中定义的编码常量（如Raw=0, CopyRect=1, Hextile=5, ZRLE=16）。
    /// </summary>
    int EncodingType { get; }

    /// <summary>
    /// 从网络流中读取并解码编码的像素数据，更新帧缓冲区中的对应区域。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="framebuffer">要更新的帧缓冲区实例。</param>
    void Decode(VncStream stream, FramebufferRect rect, Framebuffer framebuffer);

    /// <summary>
    /// 异步从网络流中读取并解码编码的像素数据。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="pixelFormat">当前使用的像素格式。</param>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>解码后的像素数据字节数组。</returns>
    Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat pixelFormat, CancellationToken ct);
}
