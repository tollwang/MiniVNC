using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniVNC.Core;

/// <summary>
/// VNC连接配置模型 - 表示单个VNC服务器连接设置。
/// 支持序列化/反序列化为JSON格式进行持久化存储。
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>
    /// 连接配置文件的存储路径。
    /// 位于用户的ApplicationData目录下的MiniVNC文件夹中。
    /// </summary>
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MiniVNC", "connections.json");

    /// <summary>
    /// JSON序列化选项。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 连接配置的唯一标识符。
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 连接显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 连接备注说明（可选，显示在连接列表中）。
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// VNC服务器主机名或IP地址。
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// VNC服务器端口号。默认值为5900。
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 5900;

    /// <summary>
    /// 账户用户名。macOS 屏幕共享的 Apple/ARD 认证（安全类型30）需要；标准 VNC 密码认证可留空。
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// VNC连接密码。以加密形式存储（使用Windows DPAPI）。
    /// </summary>
    [JsonIgnore] // 不直接序列化密码
    public string? Password
    {
        get => _password;
        set => _password = value;
    }

    /// <summary>
    /// 加密后的密码，用于JSON序列化/反序列化。
    /// </summary>
    [JsonPropertyName("password")]
    public string? EncryptedPassword
    {
        get => string.IsNullOrEmpty(_password) ? null : EncryptPassword(_password);
        set => _password = string.IsNullOrEmpty(value) ? null : DecryptPassword(value);
    }

    private string? _password;

    /// <summary>
    /// 是否以仅查看模式连接。仅查看模式下不发送输入事件。
    /// </summary>
    [JsonPropertyName("viewOnly")]
    public bool ViewOnly { get; set; } = false;

    /// <summary>
    /// 图像质量级别（1-9）。较高值提供更好的图像质量但消耗更多带宽。
    /// </summary>
    [JsonPropertyName("quality")]
    public int Quality { get; set; } = 6;

    /// <summary>
    /// 颜色深度（位/像素）：32=高清全彩；16=流畅模式（RGB565，带宽减半）。默认32。
    /// </summary>
    [JsonPropertyName("colorDepth")]
    public int ColorDepth { get; set; } = 32;

    /// <summary>
    /// 连接断开时是否自动重新连接。
    /// </summary>
    [JsonPropertyName("autoReconnect")]
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// 此连接配置的创建时间。
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 此连接配置的最后修改时间。
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 使用DPAPI加密密码。加密失败时返回 null（绝不回退写入明文密码到磁盘）。
    /// </summary>
    /// <param name="password">明文密码</param>
    /// <returns>Base64编码的加密密码；失败返回 null</returns>
    private static string? EncryptPassword(string password)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(password);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            // 不把明文写盘；宁可不持久化密码（用户下次重新输入）
            return null;
        }
    }

    /// <summary>
    /// 使用DPAPI解密密码。解密失败时返回空字符串（绝不返回密文本身——
    /// 否则会被当作明文在下次保存时重新加密，不可逆地损坏原密码）。
    /// </summary>
    /// <param name="encrypted">Base64编码的加密密码</param>
    /// <returns>明文密码；失败返回空字符串</returns>
    private static string DecryptPassword(string encrypted)
    {
        try
        {
            byte[] data = Convert.FromBase64String(encrypted);
            byte[] decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // 本机/本用户无法解密（如跨机/跨用户拷贝的配置）：返回空，提示用户重新输入，
            // 而非把密文当明文回写造成永久损坏。
            return string.Empty;
        }
    }

    /// <summary>
    /// 从配置文件加载所有连接设置。
    /// </summary>
    /// <returns>连接设置列表。如果配置文件不存在则返回空列表。</returns>
    public static List<ConnectionSettings> LoadAll()
    {
        if (!File.Exists(ConfigPath))
        {
            return new List<ConnectionSettings>();
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<List<ConnectionSettings>>(json, JsonOptions)
                ?? new List<ConnectionSettings>();
        }
        catch (JsonException)
        {
            // 配置文件损坏：先把损坏文件备份为 .bad，避免下次保存静默覆盖、永久丢失全部连接。
            try
            {
                File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true);
            }
            catch { /* 备份失败也不要因此崩溃 */ }
            return new List<ConnectionSettings>();
        }
    }

    /// <summary>
    /// 保存所有连接设置到配置文件。
    /// </summary>
    /// <param name="settings">要保存的连接设置列表</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/>为null</exception>
    /// <exception cref="IOException">写入文件时发生I/O错误</exception>
    public static void SaveAll(List<ConnectionSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? directory = Path.GetDirectoryName(ConfigPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 更新所有配置的修改时间
        foreach (var setting in settings)
        {
            setting.UpdatedAt = DateTime.UtcNow;
        }

        string json = JsonSerializer.Serialize(settings, JsonOptions);

        // 原子写：先写临时文件再替换，避免崩溃/断电中途留下半截文件导致下次加载判损坏并清空。
        string tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(ConfigPath))
        {
            // File.Replace 原子替换，并把原文件留存为 .bak 兜底
            File.Replace(tempPath, ConfigPath, ConfigPath + ".bak");
        }
        else
        {
            File.Move(tempPath, ConfigPath);
        }
    }

    /// <summary>
    /// 添加或更新单个连接设置。
    /// </summary>
    /// <param name="setting">要添加或更新的连接设置</param>
    /// <exception cref="ArgumentNullException"><paramref name="setting"/>为null</exception>
    public static void Save(ConnectionSettings setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        var settings = LoadAll();
        int existingIndex = settings.FindIndex(s => s.Id == setting.Id);

        if (existingIndex >= 0)
        {
            settings[existingIndex] = setting;
        }
        else
        {
            settings.Add(setting);
        }

        SaveAll(settings);
    }

    /// <summary>
    /// 删除指定ID的连接设置。
    /// </summary>
    /// <param name="id">要删除的连接设置ID</param>
    /// <returns>是否成功删除</returns>
    public static bool Delete(Guid id)
    {
        var settings = LoadAll();
        int index = settings.FindIndex(s => s.Id == id);

        if (index >= 0)
        {
            settings.RemoveAt(index);
            SaveAll(settings);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 根据ID查找连接设置。
    /// </summary>
    /// <param name="id">连接设置ID</param>
    /// <returns>找到的连接设置，未找到则返回null</returns>
    public static ConnectionSettings? FindById(Guid id)
    {
        return LoadAll().FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// 创建此连接设置的深拷贝。
    /// </summary>
    /// <returns>新的 <see cref="ConnectionSettings"/> 实例</returns>
    public ConnectionSettings Clone()
    {
        return new ConnectionSettings
        {
            Id = Guid.NewGuid(),
            Name = this.Name,
            Description = this.Description,
            Host = this.Host,
            Port = this.Port,
            Username = this.Username,
            Password = this.Password,
            ViewOnly = this.ViewOnly,
            Quality = this.Quality,
            ColorDepth = this.ColorDepth,
            AutoReconnect = this.AutoReconnect
        };
    }

    /// <summary>
    /// 返回此连接设置的友好显示名称。
    /// </summary>
    /// <returns>格式为 "名称 (主机:端口)" 的字符串</returns>
    public override string ToString()
    {
        return $"{Name} ({Host}:{Port})";
    }
}
