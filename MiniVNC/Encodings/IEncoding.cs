using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// VNC 编码解码器接口。每种编码（Raw、Hextile 等）实现此接口，
/// 从网络流读取该编码的数据并解码为 <b>BGRA32</b> 像素（每像素4字节，B,G,R,A）。
/// </summary>
public interface IEncoding
{
    /// <summary>
    /// 此解码器对应的编码类型标识符（如 Raw=0, CopyRect=1, Hextile=5, ZRLE=16）。
    /// </summary>
    int EncodingType { get; }

    /// <summary>
    /// 异步读取并解码一个矩形区域，返回长度为 <c>rect.Width * rect.Height * 4</c> 的 BGRA32 数据。
    /// </summary>
    /// <param name="stream">VNC网络流。</param>
    /// <param name="rect">矩形位置与尺寸。</param>
    /// <param name="format">服务器像素格式（由客户端 SetPixelFormat 协商决定）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>BGRA32 像素数据。</returns>
    Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct);
}
