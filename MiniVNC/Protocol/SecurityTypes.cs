namespace MiniVNC.Protocol;

/// <summary>
/// RFB协议定义的安全认证类型枚举。
/// 用于在协议握手阶段协商客户端与服务器之间的认证方式。
/// </summary>
public enum SecurityType : byte
{
    /// <summary>
    /// 无效的安全类型。当服务器不支持任何客户端支持的安全类型时使用。
    /// </summary>
    Invalid = 0,

    /// <summary>
    /// 无认证。不需要密码，直接通过认证阶段。
    /// 仅在受信任的网络环境中使用。
    /// </summary>
    None = 1,

    /// <summary>
    /// VNC标准认证。使用DES挑战-响应机制的密码认证。
    /// 服务器发送16字节随机挑战，客户端使用DES加密的响应进行回复。
    /// </summary>
    VncAuthentication = 2,

    /// <summary>
    /// Apple远程桌面认证。macOS屏幕共享服务可能使用的扩展认证类型。
    /// 基于Diffie-Hellman密钥交换的认证机制。
    /// </summary>
    AppleRemoteDesktop = 30
}

/// <summary>
/// 表示安全认证的结果状态。
/// </summary>
public enum SecurityResult : uint
{
    /// <summary>
    /// 认证成功，可以进入初始化阶段。
    /// </summary>
    Ok = 0,

    /// <summary>
    /// 认证失败，服务器将在结果后发送失败原因字符串。
    /// </summary>
    Failed = 1,

    /// <summary>
    /// 认证失败（RFB 3.8+），表示需要切换到其他安全类型进行重试。
    /// </summary>
    TooManyAttempts = 2
}
