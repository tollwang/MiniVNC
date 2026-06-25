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

    /// <summary>发送 SetPixelFormat 消息。</summary>
    public void WriteSetPixelFormat(PixelFormat format)
    {
        _stream.WriteByte((byte)ClientMessageType.SetPixelFormat);
        _stream.Write(new byte[3]); // 填充
        _stream.Write(format.ToByteArray());
    }

    /// <summary>发送 SetEncodings 消息（按偏好顺序）。</summary>
    public void WriteSetEncodings(int[] encodings)
    {
        _stream.WriteByte((byte)ClientMessageType.SetEncodings);
        _stream.WriteByte(0); // 填充
        _stream.WriteUInt16((ushort)encodings.Length);
        foreach (int encoding in encodings)
            _stream.WriteUInt32((uint)encoding);
    }

    /// <summary>发送 FramebufferUpdateRequest 消息。</summary>
    public void WriteFramebufferUpdateRequest(bool incremental, ushort x, ushort y, ushort width, ushort height)
    {
        _stream.WriteByte((byte)ClientMessageType.FramebufferUpdateRequest);
        _stream.WriteByte(incremental ? (byte)1 : (byte)0);
        _stream.WriteUInt16(x);
        _stream.WriteUInt16(y);
        _stream.WriteUInt16(width);
        _stream.WriteUInt16(height);
    }

    /// <summary>发送 KeyEvent 消息。</summary>
    public void WriteKeyEvent(bool pressed, uint keysym)
    {
        _stream.WriteByte((byte)ClientMessageType.KeyEvent);
        _stream.WriteByte(pressed ? (byte)1 : (byte)0);
        _stream.WriteUInt16(0); // 填充
        _stream.WriteUInt32(keysym);
    }

    /// <summary>发送 PointerEvent 消息。</summary>
    public void WritePointerEvent(byte buttonMask, ushort x, ushort y)
    {
        _stream.WriteByte((byte)ClientMessageType.PointerEvent);
        _stream.WriteByte(buttonMask);
        _stream.WriteUInt16(x);
        _stream.WriteUInt16(y);
    }

    /// <summary>发送 ClientCutText 消息（剪贴板文本）。RFB 3.8 规定为 Latin-1(ISO 8859-1)。</summary>
    public void WriteCutText(string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text ?? string.Empty);
        _stream.WriteByte((byte)ClientMessageType.ClientCutText);
        _stream.Write(new byte[3]); // 填充
        _stream.WriteUInt32((uint)bytes.Length);
        _stream.Write(bytes);
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
