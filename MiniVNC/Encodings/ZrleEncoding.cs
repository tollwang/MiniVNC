using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// ZRLE（Zlib Run-Length Encoding）编码解码器（编码类型16）。
///
/// <para>
/// <b>当前未启用。</b> ZRLE 要求在整条会话期间维持一个<em>连续的</em> zlib 解压上下文
/// （解压字典跨矩形保持），并对 32bpp 真彩色使用 3 字节的 CPIXEL。要正确实现需要一个
/// 可增量喂入压缩字节、且能保留 inflate 状态的解压流，.NET 内置的 <c>ZLibStream</c> 难以
/// 直接、可靠地满足该语义。为避免在未经实机验证的情况下引入会“静默损坏画面”的解码逻辑，
/// 客户端默认仅协商 Hextile / CopyRect / Raw（见 <see cref="MiniVNC.Core.VncClient"/>）。
/// </para>
/// <para>
/// 该类保留以备后续实现：届时需在 VncClient 的编码表与协商列表中加入类型 16。
/// </para>
/// </summary>
public sealed class ZrleEncoding : IEncoding
{
    /// <summary>ZRLE 编码类型标识符。</summary>
    public int EncodingType => EncodingTypes.Zrle;

    /// <inheritdoc />
    public Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat format, CancellationToken ct)
        => throw new NotSupportedException(
            "ZRLE 编码尚未启用（需要持久化的 zlib 解压上下文）。客户端默认仅协商 Hextile/CopyRect/Raw。");
}
