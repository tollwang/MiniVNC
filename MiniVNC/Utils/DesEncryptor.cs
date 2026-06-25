using System.Security.Cryptography;

namespace MiniVNC.Utils;

/// <summary>
/// VNC DES加密器 - 用于Challenge-Response认证。
/// VNC协议使用标准的DES算法，但密钥的位序是反转的（每个字节的位顺序颠倒）。
/// 密码被截断或填充至8字节，然后每个字节的位序反转后作为DES密钥。
/// </summary>
/// <remarks>
/// VNC认证流程：
/// 1. 服务器发送16字节随机challenge
/// 2. 客户端使用密码生成的DES密钥加密challenge
/// 3. 由于DES每次加密8字节，16字节challenge分两次ECB加密
/// 4. 将加密结果发送回服务器验证
/// </remarks>
public static class DesEncryptor
{
    /// <summary>
    /// 使用VNC密码加密16字节challenge。
    /// </summary>
    /// <param name="challenge">服务器发送的16字节随机挑战数据</param>
    /// <param name="password">VNC连接密码，最多8个字符</param>
    /// <returns>加密后的16字节响应数据</returns>
    /// <exception cref="ArgumentNullException"><paramref name="challenge"/>为null</exception>
    /// <exception cref="ArgumentException"><paramref name="challenge"/>长度不是16字节</exception>
    public static byte[] Encrypt(byte[] challenge, string password)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        if (challenge.Length != 16)
        {
            throw new ArgumentException("Challenge必须是16字节", nameof(challenge));
        }

        // 密码处理：不足8字节补0，超过8字节截断
        byte[] key = VncPasswordToKey(password);

        // VNC密钥需要位反转
        byte[] desKey = ReverseBits(key);

        // 使用DES分两次加密（ECB模式）
        using var des = DES.Create();
        des.Key = desKey;
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;

        using var encryptor = des.CreateEncryptor();
        byte[] result = new byte[16];
        encryptor.TransformBlock(challenge, 0, 8, result, 0);
        encryptor.TransformBlock(challenge, 8, 8, result, 8);
        return result;
    }

    /// <summary>
    /// 将VNC密码转换为8字节密钥。
    /// 密码超过8字符则截断，不足8字节以0x00填充。
    /// </summary>
    /// <param name="password">VNC连接密码</param>
    /// <returns>8字节密钥数组</returns>
    private static byte[] VncPasswordToKey(string password)
    {
        byte[] key = new byte[8];

        if (!string.IsNullOrEmpty(password))
        {
            byte[] passwordBytes = System.Text.Encoding.ASCII.GetBytes(password);
            int len = Math.Min(passwordBytes.Length, 8);
            Buffer.BlockCopy(passwordBytes, 0, key, 0, len);
        }

        // 剩余字节保持为0x00
        return key;
    }

    /// <summary>
    /// 反转每个字节的位序（VNC DES要求）。
    /// 例如：字节0x01 (00000001) 反转后变为0x80 (10000000)
    /// </summary>
    /// <param name="data">需要反转位序的字节数组</param>
    /// <returns>位序反转后的字节数组</returns>
    private static byte[] ReverseBits(byte[] data)
    {
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
}
