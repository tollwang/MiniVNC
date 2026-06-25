using System.Text;
using MiniVNC.Network;

namespace MiniVNC.Protocol;

/// <summary>
/// RFB协议状态机，实现VNC RFB协议的完整握手、安全协商、初始化和消息读写。
/// 
/// RFB协议流程：
/// 1. 协议版本协商（双方交换版本号）
/// 2. 安全类型协商（选择认证方式）
    /// 3. 安全认证（挑战-响应认证）
/// 4. 客户端初始化（发送共享标志）
/// 5. 服务器初始化（接收帧缓冲参数）
/// 6. 进入正常操作阶段（交换消息）
/// </summary>
public class RfbProtocol : IDisposable
{
    private readonly VncStream _stream;
    private bool _disposed;

    /// <summary>
    /// 获取RFB协议的当前版本号。默认值为3.8。
    /// </summary>
    public Version ProtocolVersion { get; } = new(3, 8);

    /// <summary>
    /// 获取或设置服务器的帧缓冲初始化信息。
    /// 仅在完成 <see cref="ReadServerInitAsync"/> 后有效。
    /// </summary>
    public ServerInitInfo? ServerInit { get; private set; }

    /// <summary>
    /// 获取底层 <see cref="VncStream"/> 实例，用于直接网络通信。
    /// </summary>
    public VncStream Stream => _stream;

    /// <summary>
    /// 初始化 <see cref="RfbProtocol"/> 实例并建立到VNC服务器的连接。
    /// </summary>
    /// <param name="host">VNC服务器主机名或IP地址。</param>
    /// <param name="port">VNC服务器端口号。</param>
    public RfbProtocol(string host, int port)
    {
        _stream = new VncStream(host, port);
    }

    /// <summary>
    /// 使用现有的 <see cref="VncStream"/> 初始化 <see cref="RfbProtocol"/> 实例。
    /// </summary>
    /// <param name="stream">已连接的VNC网络流。</param>
    public RfbProtocol(VncStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    #region 协议版本协商

    /// <summary>
    /// 异步读取服务器发送的RFB协议版本号。
    /// 版本号格式为 "RFB 003.008\n"（12字节）。
    /// </summary>
    /// <returns>服务器使用的RFB协议版本。</returns>
    /// <exception cref="IOException">版本号格式无效或连接已关闭。</exception>
    public async Task<Version> ReadProtocolVersionAsync()
    {
        byte[] versionBytes = await _stream.ReadExactlyAsync(12);
        string versionString = Encoding.ASCII.GetString(versionBytes);

        // 期望格式: "RFB 003.008\n"
        if (!versionString.StartsWith("RFB ") || versionString.Length < 12)
            throw new IOException($"Invalid protocol version string: '{versionString}'");

        // 解析主次版本号
        if (!int.TryParse(versionString.Substring(4, 3), out int major))
            major = 3;
        if (!int.TryParse(versionString.Substring(8, 3), out int minor))
            minor = 8;

        return new Version(major, minor);
    }

    /// <summary>
    /// 向服务器写入客户端支持的RFB协议版本号。
    /// 格式为 "RFB 003.008\n"（12字节）。
    /// </summary>
    /// <param name="version">要发送的协议版本号。</param>
    public void WriteProtocolVersion(Version version)
    {
        string versionString = $"RFB {version.Major:D3}.{version.Minor:D3}\n";
        byte[] bytes = Encoding.ASCII.GetBytes(versionString);
        _stream.Write(bytes);
    }

    #endregion

    #region 安全类型协商

    /// <summary>
    /// 异步读取服务器提供的安全类型列表。
    /// 在RFB 3.7+中，服务器发送安全类型数量和列表供客户端选择。
    /// </summary>
    /// <returns>服务器支持的安全类型数组。如果数量为0，表示连接被拒绝，随后会收到原因字符串。</returns>
    /// <exception cref="IOException">服务器拒绝连接时抛出，包含拒绝原因。</exception>
    public async Task<SecurityType[]> ReadSecurityTypesAsync()
    {
        byte count = await ReadByteAsync();

        if (count == 0)
        {
            // 服务器拒绝连接，读取原因字符串
            uint reasonLength = await _stream.ReadUInt32Async();
            byte[] reasonBytes = await _stream.ReadExactlyAsync((int)reasonLength);
            string reason = Encoding.UTF8.GetString(reasonBytes);
            throw new IOException($"Server refused connection: {reason}");
        }

        byte[] types = await _stream.ReadExactlyAsync(count);
        return types.Select(t => (SecurityType)t).ToArray();
    }

    /// <summary>
    /// 向服务器写入客户端选择的安全类型。
    /// </summary>
    /// <param name="securityType">选定的安全类型字节值。</param>
    public void WriteSecurityType(byte securityType)
    {
        _stream.WriteByte(securityType);
    }

    /// <summary>
    /// 向服务器写入客户端选择的安全类型。
    /// </summary>
    /// <param name="securityType">选定的安全类型枚举值。</param>
    public void WriteSecurityType(SecurityType securityType)
    {
        _stream.WriteByte((byte)securityType);
    }

    #endregion

    #region VNC认证挑战-响应

    /// <summary>
    /// 异步读取VNC认证挑战数据。
    /// 服务器发送16字节的随机挑战，客户端需要使用DES加密的密码进行响应。
    /// </summary>
    /// <returns>16字节的随机挑战数据。</returns>
    public async Task<byte[]> ReadChallengeAsync()
    {
        return await _stream.ReadExactlyAsync(16);
    }

    /// <summary>
    /// 向服务器发送VNC认证挑战响应。
    /// 响应是挑战数据经过DES加密后的结果。
    /// </summary>
    /// <param name="response">DES加密后的16字节响应数据。</param>
    public void WriteChallengeResponse(byte[] response)
    {
        if (response.Length != 16)
            throw new ArgumentException("Challenge response must be exactly 16 bytes.", nameof(response));
        _stream.Write(response);
    }

    /// <summary>
    /// 使用密码生成VNC认证响应。
    /// 将密码填充到8字节（不足补零），使用DES加密挑战数据。
    /// </summary>
    /// <param name="challenge">服务器发送的16字节挑战数据。</param>
    /// <param name="password">VNC连接密码，最长8字符。</param>
    /// <returns>16字节的认证响应数据。</returns>
    public static byte[] GenerateChallengeResponse(byte[] challenge, string password)
    {
        if (challenge.Length != 16)
            throw new ArgumentException("Challenge must be 16 bytes.", nameof(challenge));

        // 将密码转换为8字节密钥（不足补零）
        byte[] key = new byte[8];
        byte[] passwordBytes = Encoding.ASCII.GetBytes(password);
        int copyLength = Math.Min(passwordBytes.Length, 8);
        Array.Copy(passwordBytes, key, copyLength);

        // VNC DES使用反向位序
        for (int i = 0; i < 8; i++)
        {
            key[i] = ReverseBits(key[i]);
        }

        // 使用DES加密（ECB模式）
        byte[] response = new byte[16];
        using (var des = System.Security.Cryptography.DES.Create())
        {
            des.Key = key;
            des.Mode = System.Security.Cryptography.CipherMode.ECB;
            des.Padding = System.Security.Cryptography.PaddingMode.None;

            using var encryptor = des.CreateEncryptor();
            // 加密前8字节
            encryptor.TransformBlock(challenge, 0, 8, response, 0);
            // 加密后8字节
            encryptor.TransformBlock(challenge, 8, 8, response, 8);
        }

        return response;
    }

    /// <summary>
    /// 反转字节中的位序（bit 0 ↔ bit 7, bit 1 ↔ bit 6, ...）。
    /// VNC DES密钥使用反向位序。
    /// </summary>
    /// <param name="b">要反转位序的字节。</param>
    /// <returns>位序反转后的字节。</returns>
    private static byte ReverseBits(byte b)
    {
        byte result = 0;
        for (int i = 0; i < 8; i++)
        {
            result |= (byte)(((b >> i) & 1) << (7 - i));
        }
        return result;
    }

    #endregion

    #region 安全结果

    /// <summary>
    /// 异步读取安全认证结果。
    /// 结果为4字节无符号整数：0表示成功，1或2表示失败。
    /// </summary>
    /// <returns>安全认证结果枚举值。</returns>
    public async Task<SecurityResult> ReadSecurityResultAsync()
    {
        uint result = await _stream.ReadUInt32Async();
        return (SecurityResult)result;
    }

    #endregion

    #region 初始化

    /// <summary>
    /// 向服务器发送客户端初始化信息。
    /// 共享标志指示服务器是否应在客户端断开时保持桌面共享。
    /// </summary>
    /// <param name="shared">为 <c>true</c> 时请求共享桌面；为 <c>false</c> 时独占桌面。</param>
    public void WriteClientInit(bool shared)
    {
        _stream.WriteByte(shared ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// 异步读取服务器初始化信息。
    /// 包含帧缓冲区尺寸、像素格式和桌面名称。
    /// </summary>
    /// <returns>包含服务器初始化参数的 <see cref="ServerInitInfo"/> 对象。</returns>
    public async Task<ServerInitInfo> ReadServerInitAsync()
    {
        ushort width = await _stream.ReadUInt16Async();
        ushort height = await _stream.ReadUInt16Async();

        // 读取16字节像素格式
        byte[] pixelFormatBytes = await _stream.ReadExactlyAsync(16);
        PixelFormat pixelFormat = PixelFormat.FromByteArray(pixelFormatBytes);

        // 读取桌面名称长度和名称
        uint nameLength = await _stream.ReadUInt32Async();
        byte[] nameBytes = await _stream.ReadExactlyAsync((int)nameLength);
        string desktopName = Encoding.UTF8.GetString(nameBytes);

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

    /// <summary>
    /// 发送设置像素格式消息到服务器。
    /// 通知服务器客户端期望的像素数据格式。
    /// </summary>
    /// <param name="format">要设置的像素格式。</param>
    public void SendSetPixelFormat(PixelFormat format)
    {
        _stream.WriteByte((byte)ClientMessageType.SetPixelFormat);
        _stream.Write(new byte[3]); // 填充字节
        _stream.Write(format.ToByteArray());
    }

    /// <summary>
    /// 发送设置编码方式消息到服务器。
    /// 告知服务器客户端支持的编码类型列表，按偏好顺序排列。
    /// </summary>
    /// <param name="encodings">编码类型标识符数组。</param>
    public void SendSetEncodings(int[] encodings)
    {
        _stream.WriteByte((byte)ClientMessageType.SetEncodings);
        _stream.WriteByte(0); // 填充字节
        _stream.WriteUInt16((ushort)encodings.Length);

        foreach (int encoding in encodings)
        {
            _stream.WriteUInt32((uint)encoding);
        }
    }

    /// <summary>
    /// 发送帧缓冲更新请求消息到服务器。
    /// 请求服务器发送指定区域的帧缓冲更新。
    /// </summary>
    /// <param name="x">请求区域左上角的X坐标。</param>
    /// <param name="y">请求区域左上角的Y坐标。</param>
    /// <param name="width">请求区域的宽度。</param>
    /// <param name="height">请求区域的高度。</param>
    /// <param name="incremental">为 <c>true</c> 时请求增量更新；为 <c>false</c> 时请求完整更新。</param>
    public void SendFramebufferUpdateRequest(ushort x, ushort y, ushort width, ushort height, bool incremental)
    {
        _stream.WriteByte((byte)ClientMessageType.FramebufferUpdateRequest);
        _stream.WriteByte(incremental ? (byte)1 : (byte)0);
        _stream.WriteUInt16(x);
        _stream.WriteUInt16(y);
        _stream.WriteUInt16(width);
        _stream.WriteUInt16(height);
    }

    /// <summary>
    /// 发送按键事件消息到服务器。
    /// </summary>
    /// <param name="key">X11按键符号（keysym）。</param>
    /// <param name="pressed">为 <c>true</c> 表示按键按下；为 <c>false</c> 表示按键释放。</param>
    public void SendKeyEvent(uint key, bool pressed)
    {
        _stream.WriteByte((byte)ClientMessageType.KeyEvent);
        _stream.WriteByte(pressed ? (byte)1 : (byte)0);
        _stream.WriteUInt16(0); // 填充字节
        _stream.WriteUInt32(key);
    }

    /// <summary>
    /// 发送鼠标指针事件消息到服务器。
    /// </summary>
    /// <param name="x">鼠标指针的X坐标。</param>
    /// <param name="y">鼠标指针的Y坐标。</param>
    /// <param name="buttonMask">按钮状态掩码（bit 0=左键, bit 1=中键, bit 2=右键）。</param>
    public void SendPointerEvent(ushort x, ushort y, byte buttonMask)
    {
        _stream.WriteByte((byte)ClientMessageType.PointerEvent);
        _stream.WriteByte(buttonMask);
        _stream.WriteUInt16(x);
        _stream.WriteUInt16(y);
    }

    /// <summary>
    /// 发送客户端剪贴板文本消息到服务器。
    /// </summary>
    /// <param name="text">要发送到服务器的剪贴板文本内容。</param>
    public void SendClientCutText(string text)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        _stream.WriteByte((byte)ClientMessageType.ClientCutText);
        _stream.Write(new byte[3]); // 填充字节
        _stream.WriteUInt32((uint)textBytes.Length);
        _stream.Write(textBytes);
    }

    #endregion

    #region 服务器消息读取

    /// <summary>
    /// 异步读取服务器消息类型字节。
    /// </summary>
    /// <returns>服务器消息类型枚举值。</returns>
    public async Task<ServerMessageType> ReadServerMessageTypeAsync()
    {
        byte messageType = await ReadByteAsync();
        return (ServerMessageType)messageType;
    }

    /// <summary>
    /// 异步读取完整的帧缓冲更新消息。
    /// 包含消息类型、填充字节、矩形数量，以及每个矩形的头和编码数据。
    /// </summary>
    /// <returns>帧缓冲更新中的矩形数组。注意：此函数仅读取矩形头信息，不读取编码像素数据。</returns>
    public async Task<FramebufferRect[]> ReadFramebufferUpdateAsync()
    {
        await ReadExactlyAsync(1); // 填充字节
        ushort rectCount = await _stream.ReadUInt16Async();

        var rects = new FramebufferRect[rectCount];
        for (int i = 0; i < rectCount; i++)
        {
            ushort x = await _stream.ReadUInt16Async();
            ushort y = await _stream.ReadUInt16Async();
            ushort w = await _stream.ReadUInt16Async();
            ushort h = await _stream.ReadUInt16Async();
            int encoding = (int)await _stream.ReadUInt32Async();

            rects[i] = new FramebufferRect(x, y, w, h, encoding);
        }

        return rects;
    }

    #endregion

    #region 辅助方法

    /// <summary>
    /// 异步从流中读取单个字节。
    /// </summary>
    /// <returns>读取到的字节值。</returns>
    private async Task<byte> ReadByteAsync()
    {
        byte[] b = await _stream.ReadExactlyAsync(1);
        return b[0];
    }

    /// <summary>
    /// 异步从流中精确读取指定数量的字节。
    /// </summary>
    /// <param name="count">要读取的字节数。</param>
    /// <returns>读取到的字节数组。</returns>
    private async Task<byte[]> ReadExactlyAsync(int count)
    {
        return await _stream.ReadExactlyAsync(count);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// 释放 <see cref="RfbProtocol"/> 使用的所有资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }

    #endregion
}
