using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using MiniVNC.Utils;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 两种认证算法。ARD 那一项是真正的端到端验证：在测试里扮演服务器完成 DH 交换，
/// 再用协商出的密钥把客户端发来的密文解开，检查用户名和密码能原样还原。
/// </summary>
public static class AuthTests
{
    public static void Run()
    {
        Section("认证算法");

        VncDes();
        AppleArd();
    }

    // ---- VNC 密码认证（安全类型 2，DES）----
    private static void VncDes()
    {
        byte[] challenge = new byte[16];
        for (int i = 0; i < 16; i++) challenge[i] = (byte)(i * 17);

        byte[] r1 = DesEncryptor.Encrypt(challenge, "secret");
        byte[] r2 = DesEncryptor.Encrypt(challenge, "secret");

        Check("DES 响应为 16 字节", r1.Length == 16);
        Check("DES 相同输入产生相同输出", r1.SequenceEqual(r2));
        Check("DES 不同密码产生不同输出",
            !r1.SequenceEqual(DesEncryptor.Encrypt(challenge, "other")));

        // VNC 协议规定密码截断到 8 字节——超长部分必须被丢弃
        Check("密码超过 8 字符时被截断",
            DesEncryptor.Encrypt(challenge, "12345678").SequenceEqual(
            DesEncryptor.Encrypt(challenge, "123456789abc")));

        // 16 字节 challenge 是分两个独立 ECB 块加密的：两块明文相同则密文也相同
        byte[] twin = new byte[16];
        for (int i = 0; i < 8; i++) { twin[i] = (byte)(i + 1); twin[i + 8] = (byte)(i + 1); }
        byte[] rt = DesEncryptor.Encrypt(twin, "pw");
        Check("16 字节 challenge 按两个独立 ECB 块加密",
            rt.AsSpan(0, 8).SequenceEqual(rt.AsSpan(8, 8)));

        bool threw = false;
        try { DesEncryptor.Encrypt(new byte[8], "pw"); } catch (ArgumentException) { threw = true; }
        Check("challenge 长度不是 16 字节时报错", threw);
    }

    // ---- Apple/ARD 认证（安全类型 30，DH + AES-128）----
    private static void AppleArd()
    {
        const int keyLength = 64;      // macOS 屏幕共享实际使用 512 位 DH 群
        const string username = "toll";
        const string password = "correct horse battery staple";

        // 扮演服务器：选一个模数与私钥，算出服务器公钥。
        // 注意这里的模数不必是素数——DH 的密钥一致性 (g^a)^b == (g^b)^a 对任意模数都成立，
        // 而客户端也不做素性检查，所以用一个确定的大奇数就能完整覆盖被测代码路径。
        byte[] modulusBytes = new byte[keyLength];
        for (int i = 0; i < keyLength; i++) modulusBytes[i] = (byte)(0x80 + i * 7);
        modulusBytes[keyLength - 1] |= 1;                       // 保证是奇数
        var modulus = new BigInteger(modulusBytes, isUnsigned: true, isBigEndian: true);

        var g = new BigInteger(2);
        var serverPriv = new BigInteger(0x5F2A3B4Cu);
        BigInteger serverPub = BigInteger.ModPow(g, serverPriv, modulus);
        byte[] serverPubBytes = ToFixedBigEndian(serverPub, keyLength);

        var (cipher, clientPubBytes) = AppleAuthenticator.CreateResponse(
            2, modulusBytes, serverPubBytes, username, password);

        Check("ARD 密文为 128 字节", cipher.Length == 128);
        Check("ARD 客户端公钥长度等于密钥长度", clientPubBytes.Length == keyLength);

        // 服务器侧：用客户端公钥算出同一个共享密钥，AES 密钥 = MD5(共享密钥)
        var clientPub = new BigInteger(clientPubBytes, isUnsigned: true, isBigEndian: true);
        byte[] secret = ToFixedBigEndian(BigInteger.ModPow(clientPub, serverPriv, modulus), keyLength);
        byte[] aesKey = MD5.HashData(secret);

        byte[] plain = AesEcbDecrypt(cipher, aesKey);
        Check("ARD 凭据明文为 128 字节", plain.Length == 128);

        string gotUser = ReadField(plain, 0);
        string gotPass = ReadField(plain, 64);
        Check("ARD 端到端：服务器能解出用户名", gotUser == username, $"得到 '{gotUser}'");
        Check("ARD 端到端：服务器能解出密码", gotPass == password, $"得到 '{gotPass}'");
        Check("ARD 密码不受 8 字符限制（明显长于 8）", password.Length > 8 && gotPass == password);

        // 每次调用都用新的随机私钥与随机填充 → 两次响应必不相同（防重放）
        var (cipher2, clientPub2) = AppleAuthenticator.CreateResponse(
            2, modulusBytes, serverPubBytes, username, password);
        Check("ARD 每次响应都不同（随机私钥 + 随机填充）",
            !cipher.SequenceEqual(cipher2) && !clientPubBytes.SequenceEqual(clientPub2));

        bool threw = false;
        try { AppleAuthenticator.CreateResponse(2, Array.Empty<byte>(), serverPubBytes, username, password); }
        catch (ArgumentException) { threw = true; }
        Check("ARD 模数长度为 0 时报错", threw);
    }

    /// <summary>读取 64 字节凭据字段中以 null 结尾的字符串。</summary>
    private static string ReadField(byte[] buffer, int offset)
    {
        int end = offset;
        while (end < offset + 64 && buffer[end] != 0) end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static byte[] ToFixedBigEndian(BigInteger value, int length)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == length) return raw;
        byte[] result = new byte[length];
        if (raw.Length < length) Array.Copy(raw, 0, result, length - raw.Length, raw.Length);
        else Array.Copy(raw, raw.Length - length, result, 0, length);
        return result;
    }

    private static byte[] AesEcbDecrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }
}
