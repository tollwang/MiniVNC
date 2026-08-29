using System.IO;
using System.Text;
using MiniVNC.Network;

namespace MiniVNC.Protocol;

/// <summary>
/// RFB 协议状态机，实现 VNC 的握手、安全协商、初始化与消息读写。
///
/// 流程：协议版本协商 → 安全类型协商 → 认证（挑战-响应）→ 客户端初始化 → 服务器初始化 → 正常消息交换。
/// 本类的公共 API 与 <see cref="MiniVNC.Core.VncClient"/> 的调用约定保持一致。
/// </summary>
public sealed class RfbProtocol : IDisposable
{
    private readonly VncStream _stream;
    private bool _disposed;

    /// <summary>底层网络流。</summary>
    public VncStream Stream => _stream;

    /// <summary>最近一次读取到的服务器初始化信息（在 <see cref="ReadServerInitAsync"/> 后有效）。</summary>
    public ServerInitInfo? ServerInit { get; private set; }

    /// <summary>
    /// 使用现有网络流构造协议处理器。
    /// </summary>
    public RfbProtocol(VncStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    #region 协议版本

    /// <summary>读取服务器协议版本字符串（12字节，如 "RFB 003.008\n"）。</summary>
    public async Task<string> ReadVersionAsync(CancellationToken ct = default)
    {
        byte[] bytes = await _stream.ReadExactlyAsync(12, ct);
        string version = Encoding.ASCII.GetString(bytes);
        if (!version.StartsWith("RFB ", StringComparison.Ordinal))
            throw new IOException($"无效的协议版本字符串: '{version}'");
        return version.TrimEnd('\n', '\r', ' ');
    }

    /// <summary>写入客户端协议版本字符串（应以 '\n' 结尾，如 "RFB 003.008\n"）。</summary>
    public void WriteVersion(string version)
    {
        _stream.Write(Encoding.ASCII.GetBytes(version));
    }

    #endregion

    #region 安全类型

    /// <summary>
    /// 读取服务器提供的安全类型列表（RFB 3.7+：数量字节 + 列表）。
    /// 数量为0表示服务器拒绝连接，随后为原因字符串。
    /// </summary>
    /// <returns>服务器支持的安全类型字节数组。</returns>
    /// <exception cref="InvalidOperationException">服务器拒绝连接时抛出，包含原因。</exception>
    public async Task<byte[]> ReadSecurityTypesAsync(CancellationToken ct = default)
    {
        byte count = await _stream.ReadByteAsync(ct);
        if (count == 0)
        {
            uint reasonLength = await _stream.ReadUInt32Async(ct);
            string reason = await ReadStringAsync(reasonLength, ct);
            throw new InvalidOperationException($"服务器拒绝连接: {reason}");
        }
        return await _stream.ReadExactlyAsync(count, ct);
    }

    /// <summary>写入客户端选择的安全类型字节。</summary>
    public void WriteSecurityType(byte securityType)
    {
        _stream.WriteByte(securityType);
    }

    #endregion

    #region VNC 认证

    /// <summary>读取16字节的 VNC 认证挑战。</summary>
    public async Task<byte[]> ReadChallengeAsync(CancellationToken ct = default)
    {
        return await _stream.ReadExactlyAsync(16, ct);
    }

    /// <summary>发送16字节的挑战响应（DES 加密结果）。</summary>
    public void WriteChallengeResponse(byte[] response)
    {
        if (response is null || response.Length != 16)
            throw new ArgumentException("挑战响应必须为16字节", nameof(response));
        _stream.Write(response);
    }

    /// <summary>读取安全认证结果（0=成功）。</summary>
    public async Task<uint> ReadSecurityResultAsync(CancellationToken ct = default)
    {
        return await _stream.ReadUInt32Async(ct);
    }

    /// <summary>
    /// 读取认证失败原因字符串（RFB 3.8 在失败时发送 4字节长度 + 文本）。
    /// 读取失败或服务器未发送时返回 null。
    /// </summary>
    public async Task<string?> ReadSecurityResultErrorAsync(CancellationToken ct = default)
    {
        try
        {
            uint length = await _stream.ReadUInt32Async(ct);
            return await ReadStringAsync(length, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消必须向上传播，不能吞成 null
        }
        catch
        {
            return null; // 服务器未发送原因串/读取失败：返回 null 由调用方给出通用提示
        }
    }

    #endregion

    #region Apple/ARD 认证（安全类型30）

    /// <summary>
    /// 读取 Apple Diffie-Hellman 认证参数：generator(2) + keyLength(2) + prime(keyLength) + serverPublicKey(keyLength)。
    /// </summary>
    public async Task<(int Generator, byte[] Prime, byte[] ServerPublicKey)> ReadAppleDhParamsAsync(CancellationToken ct = default)
    {
        ushort generator = await _stream.ReadUInt16Async(ct);
        ushort keyLength = await _stream.ReadUInt16Async(ct);
        // macOS 屏幕共享实际使用 512 位(64 字节)DH 群，下限必须能接受 64 字节，
        // 否则会把真实的 Mac 服务器误判为“过弱”而拒绝，导致根本连不上（首要连接阻断点）。
        // 上限防止异常/恶意服务器用超大模数让 ModPow 极慢造成卡死；LAN 自用下 512 位安全性可接受。
        if (keyLength < 32 || keyLength > 1024)
            throw new IOException($"ARD 认证密钥长度异常: {keyLength} 字节");

        byte[] prime = await _stream.ReadExactlyAsync(keyLength, ct);
        byte[] serverPublicKey = await _stream.ReadExactlyAsync(keyLength, ct);
        return (generator, prime, serverPublicKey);
    }

    /// <summary>
    /// 发送 ARD 认证响应：加密凭据(128) + 客户端公钥(keyLength)。
    /// </summary>
    public void WriteAppleDhResponse(byte[] encryptedCredentials, byte[] clientPublicKey)
    {
        _stream.Write(encryptedCredentials);
        _stream.Write(clientPublicKey);
    }

    #endregion

    #region 初始化

    /// <summary>发送客户端初始化（共享标志）。</summary>
    public void WriteClientInit(bool shared)
    {
        _stream.WriteByte(shared ? (byte)1 : (byte)0);
    }

    /// <summary>读取服务器初始化（帧缓冲尺寸、像素格式、桌面名称）。</summary>
    public async Task<ServerInitInfo> ReadServerInitAsync(CancellationToken ct = default)
    {
        ushort width = await _stream.ReadUInt16Async(ct);
        ushort height = await _stream.ReadUInt16Async(ct);

        byte[] pixelFormatBytes = await _stream.ReadExactlyAsync(16, ct);
        PixelFormat pixelFormat = PixelFormat.FromByteArray(pixelFormatBytes);

        uint nameLength = await _stream.ReadUInt32Async(ct);
        string desktopName = await ReadStringAsync(nameLength, ct);

        ServerInit = new ServerInitInfo
        {
            FramebufferWidth = width,
            FramebufferHeight = height,
            PixelFormat = pixelFormat,
            DesktopName = desktopName
        };
        return ServerInit;
    }

    #endregion

    #region 客户端消息发送

    // 客户端消息一律先在内存里拼装完整报文、再一次 Write 发出。
    // socket 开了 NoDelay（关 Nagle），逐字段写会让一条 6 字节的鼠标消息变成 4 个 TCP 包
    // ——4 倍的系统调用与包数，高频鼠标移动时尤其浪费。

    /// <summary>发送 SetPixelFormat 消息。</summary>
    public void WriteSetPixelFormat(PixelFormat format)
    {
        byte[] msg = new byte[20];
        msg[0] = (byte)ClientMessageType.SetPixelFormat;
        // msg[1..3] 填充，保持 0
        format.ToByteArray().CopyTo(msg, 4);
        _stream.Write(msg);
    }

    /// <summary>发送 SetEncodings 消息（按偏好顺序）。</summary>
    public void WriteSetEncodings(int[] encodings)
    {
        byte[] msg = new byte[4 + encodings.Length * 4];
        msg[0] = (byte)ClientMessageType.SetEncodings;
        // msg[1] 填充
        WriteU16(msg, 2, (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++)
            WriteU32(msg, 4 + i * 4, (uint)encodings[i]);
        _stream.Write(msg);
    }

    /// <summary>发送 FramebufferUpdateRequest 消息。</summary>
    public void WriteFramebufferUpdateRequest(bool incremental, ushort x, ushort y, ushort width, ushort height)
    {
        _stream.Write(BuildRectMessage(
            (byte)ClientMessageType.FramebufferUpdateRequest, incremental ? (byte)1 : (byte)0, x, y, width, height));
    }

    /// <summary>发送 KeyEvent 消息。</summary>
    public void WriteKeyEvent(bool pressed, uint keysym)
    {
        byte[] msg = new byte[8];
        msg[0] = (byte)ClientMessageType.KeyEvent;
        msg[1] = pressed ? (byte)1 : (byte)0;
        // msg[2..3] 填充
        WriteU32(msg, 4, keysym);
        _stream.Write(msg);
    }

    /// <summary>
    /// 发送 EnableContinuousUpdates 消息（扩展，类型150）：对指定区域开启/停用服务器连续推送。
    /// 报文：u8 类型(150) + u8 enable + u16 x + u16 y + u16 w + u16 h。
    /// </summary>
    public void WriteEnableContinuousUpdates(bool enable, ushort x, ushort y, ushort width, ushort height)
    {
        _stream.Write(BuildRectMessage(
            (byte)ClientMessageType.EnableContinuousUpdates, enable ? (byte)1 : (byte)0, x, y, width, height));
    }

    /// <summary>发送 PointerEvent 消息。</summary>
    public void WritePointerEvent(byte buttonMask, ushort x, ushort y)
    {
        byte[] msg = new byte[6];
        msg[0] = (byte)ClientMessageType.PointerEvent;
        msg[1] = buttonMask;
        WriteU16(msg, 2, x);
        WriteU16(msg, 4, y);
        _stream.Write(msg);
    }

    /// <summary>拼装"类型 + 1字节标志 + x/y/w/h"这一族 10 字节报文。</summary>
    private static byte[] BuildRectMessage(byte type, byte flag, ushort x, ushort y, ushort width, ushort height)
    {
        byte[] msg = new byte[10];
        msg[0] = type;
        msg[1] = flag;
        WriteU16(msg, 2, x);
        WriteU16(msg, 4, y);
        WriteU16(msg, 6, width);
        WriteU16(msg, 8, height);
        return msg;
    }

    private static void WriteU16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    /// <summary>
    /// 发送 ClientCutText 消息（剪贴板文本）。
    /// 本工具面向 macOS：macOS 屏幕共享的剪贴板实际按 UTF-8 收发（Apple 对 RFB "Latin-1" 规范的偏离），
    /// 故一律用 UTF-8 编码——纯 ASCII 与 Latin-1 字节相同；中文/Emoji/西欧重音字符均能正确到达 Mac。
    /// （若改连严格 Latin-1 的非 Mac 服务器，西欧字符可能乱码，属可接受的取舍。）
    /// </summary>
    public void WriteCutText(string text)
    {
        byte[] bytes = EncodeCutText(text ?? string.Empty);
        byte[] msg = new byte[8 + bytes.Length];
        msg[0] = (byte)ClientMessageType.ClientCutText;
        // msg[1..3] 填充
        WriteU32(msg, 4, (uint)bytes.Length);
        bytes.CopyTo(msg, 8);
        _stream.Write(msg);
    }

    /// <summary>严格 UTF-8（遇非法字节抛异常），用于"先试 UTF-8 再回退 Latin-1"的剪贴板解码。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 编码剪贴板文本为 UTF-8。面向 macOS（其屏幕共享按 UTF-8 收发剪贴板）；
    /// 纯 ASCII 时 UTF-8 字节与 Latin-1 完全相同，不影响标准服务器。
    /// </summary>
    private static byte[] EncodeCutText(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// 自适应解码剪贴板字节：先按严格 UTF-8 解析（中文/Emoji 可正确还原），
    /// 不是合法 UTF-8 则回退 Latin-1（西欧字符仍可读）。
    /// </summary>
    public static string DecodeCutText(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    #endregion

    #region 服务器消息

    /// <summary>读取一个服务器消息类型字节。</summary>
    public async Task<ServerMessageType> ReadServerMessageTypeAsync(CancellationToken ct = default)
    {
        byte messageType = await _stream.ReadByteAsync(ct);
        return (ServerMessageType)messageType;
    }

    #endregion

    #region 辅助

    /// <summary>读取指定长度（UTF-8）字符串，含长度上限保护。</summary>
    private async Task<string> ReadStringAsync(uint length, CancellationToken ct)
    {
        if (length == 0) return string.Empty;
        if (length > 4u * 1024 * 1024)
            throw new InvalidOperationException($"字符串长度异常: {length} 字节");
        byte[] bytes = await _stream.ReadExactlyAsync((int)length, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    #endregion

    /// <summary>释放底层网络流。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
