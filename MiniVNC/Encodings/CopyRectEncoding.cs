using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// CopyRect 编码解码器（编码类型1）。
/// 注意：实际复制操作由 <see cref="MiniVNC.Core.VncClient"/> 内联处理（读取源坐标后调用
/// <see cref="Framebuffer.CopyRect"/>），此类仅为接口完整性与潜在扩展保留。
/// </summary>
public sealed class CopyRectEncoding : IEncoding
{
    /// <summary>CopyRect 编码类型标识符。</summary>
    public int EncodingType => EncodingTypes.CopyRect;

    /// <summary>
    /// 读取源坐标（4字节）并返回占位 BGRA 数据；真正的像素复制在 VncClient 中完成。
    /// </summary>
    public async Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(4, ct); // 源X(2) + 源Y(2)
        return new byte[rect.Width * rect.Height * 4];
    }
}
