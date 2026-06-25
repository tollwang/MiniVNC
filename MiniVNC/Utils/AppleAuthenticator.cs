using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MiniVNC.Utils;

/// <summary>
/// Apple Remote Desktop / macOS 屏幕共享认证（RFB 安全类型 30）。
///
/// <para>这是 macOS“屏幕共享”默认使用的认证方式，基于 Diffie-Hellman 密钥交换：</para>
/// <list type="number">
/// <item>服务器发送：generator(2) + keyLength(2) + prime(keyLength) + serverPublicKey(keyLength)。</item>
/// <item>客户端生成随机私钥，计算 clientPublicKey = generator^priv mod prime，
/// 共享密钥 secret = serverPublicKey^priv mod prime。</item>
/// <item>AES-128 密钥 = MD5(secret)。</item>
/// <item>构造 128 字节凭据明文：用户名(64,含null,随机填充) + 密码(64,含null,随机填充)。</item>
/// <item>AES-128-ECB 加密凭据 → 128 字节密文。</item>
/// <item>客户端发送：密文(128) + clientPublicKey(keyLength)，随后读取 SecurityResult。</item>
/// </list>
/// </summary>
public static class AppleAuthenticator
{
    /// <summary>
    /// 根据服务器的 DH 参数与凭据，生成 ARD 认证响应。
    /// </summary>
    /// <param name="generator">DH 生成元（服务器发送的 2 字节值）。</param>
    /// <param name="primeModulus">DH 素数模（大端，keyLength 字节）。</param>
    /// <param name="serverPublicKey">服务器公钥（大端，keyLength 字节）。</param>
    /// <param name="username">macOS 账户用户名（最长 63 字节）。</param>
    /// <param name="password">账户密码（最长 63 字节）。</param>
    /// <returns>(加密凭据[128], 客户端公钥[keyLength])。</returns>
    public static (byte[] EncryptedCredentials, byte[] ClientPublicKey) CreateResponse(
        int generator, byte[] primeModulus, byte[] serverPublicKey, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(primeModulus);
        ArgumentNullException.ThrowIfNull(serverPublicKey);

        int keyLength = primeModulus.Length;
        if (keyLength == 0)
            throw new ArgumentException("素数模长度为0", nameof(primeModulus));

        var prime = new BigInteger(primeModulus, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(generator);
        var serverPub = new BigInteger(serverPublicKey, isUnsigned: true, isBigEndian: true);

        // 随机私钥（keyLength 字节）
        byte[] privBytes = new byte[keyLength];
        RandomNumberGenerator.Fill(privBytes);
        var priv = new BigInteger(privBytes, isUnsigned: true, isBigEndian: true);

        BigInteger clientPub = BigInteger.ModPow(g, priv, prime);
        BigInteger secret = BigInteger.ModPow(serverPub, priv, prime);

        byte[] clientPublicKey = ToFixedBigEndian(clientPub, keyLength);
        byte[] secretBytes = ToFixedBigEndian(secret, keyLength);

        // AES-128 密钥 = MD5(共享密钥)
        byte[] aesKey = MD5.HashData(secretBytes);

        // 128 字节凭据：用户名(0..64) + 密码(64..128)，各以 null 结尾，其余随机填充
        byte[] creds = new byte[128];
        RandomNumberGenerator.Fill(creds);
        WriteCredentialField(creds, 0, username);
        WriteCredentialField(creds, 64, password);

        byte[] cipher = AesEcbEncrypt(creds, aesKey);
        return (cipher, clientPublicKey);
    }

    /// <summary>
    /// 在 64 字节字段中写入字符串（UTF-8，最长 63 字节）并以 null 结尾，不覆盖其后的随机填充。
    /// </summary>
    private static void WriteCredentialField(byte[] buffer, int offset, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        int len = Math.Min(bytes.Length, 63); // 预留 null 结尾
        Array.Copy(bytes, 0, buffer, offset, len);
        buffer[offset + len] = 0;
    }

    /// <summary>
    /// 将 <see cref="BigInteger"/> 转换为固定长度的大端无符号字节数组（左侧补零）。
    /// </summary>
    private static byte[] ToFixedBigEndian(BigInteger value, int length)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == length) return raw;

        byte[] result = new byte[length];
        if (raw.Length < length)
            Array.Copy(raw, 0, result, length - raw.Length, raw.Length);
        else
            Array.Copy(raw, raw.Length - length, result, 0, length); // 取低位 length 字节
        return result;
    }

    /// <summary>
    /// AES-128-ECB（无填充）加密。
    /// </summary>
    private static byte[] AesEcbEncrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }
}
