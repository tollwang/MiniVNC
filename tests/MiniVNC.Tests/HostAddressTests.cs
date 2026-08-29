using MiniVNC.Core;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>主机地址解析：host / host:port / IPv6 / 非法输入。</summary>
public static class HostAddressTests
{
    public static void Run()
    {
        Section("主机地址解析");

        Case("纯主机名", "mac.example.com", true, "mac.example.com", null, true);
        Case("host:port 合写", "mac.example.com:5901", true, "mac.example.com", 5901, true);
        Case("IPv4", "192.168.1.5", true, "192.168.1.5", null, true);
        Case("IPv4:port", "192.168.1.5:5901", true, "192.168.1.5", 5901, true);
        Case("裸 IPv6（多冒号，整串当地址）", "::1", true, "::1", null, true);
        Case("方括号 IPv6", "[::1]", true, "::1", null, true);
        Case("方括号 IPv6 带端口", "[::1]:5901", true, "::1", 5901, true);
        Case("带区域 ID 的 IPv6", "fe80::1%eth0", true, "fe80::1%eth0", null, true);
        Case("首尾空白被裁剪", "  nas.local  ", true, "nas.local", null, true);

        // 非法输入：解析失败或校验不通过，都不应该被当成可用主机
        Case("端口非数字", "host:abc", false, null, null, false);
        Case("端口为空", "host:", false, null, null, false);
        Case("端口越界", "host:70000", false, null, null, false);
        Case("端口为 0", "[::1]:0", false, null, null, false);
        Case("缺少主机", ":5901", false, null, null, false);
        Case("方括号未闭合", "[::1", false, null, null, false);
        Case("空字符串", "", false, null, null, false);
        Case("仅空白", "   ", false, null, null, false);
        Case("含空格", "bad host", true, "bad host", null, false);   // 能拆但校验不过
        Case("含斜杠（如误贴 URL）", "vnc://mac.example.com:5901", true, null, null, false);
        Case("含 @", "user@host", true, "user@host", null, false);

        Check("默认端口常量为 5900", HostAddress.DefaultPort == 5900);

        // 超长主机名（DNS 名称上限 255）
        Check("超长主机名被拒绝", !HostAddress.IsValidHost(new string('a', 256)));
        Check("255 字符主机名可接受", HostAddress.IsValidHost(new string('a', 255)));
    }

    /// <summary>
    /// 一条用例：验证 TryParse 的返回值、拆出的 host/port，以及最终 IsValidHost 的判定。
    /// <paramref name="expectHost"/> 传 null 表示不校验拆分结果（只关心最终是否被接受）。
    /// </summary>
    private static void Case(string name, string? input, bool expectParsed,
                             string? expectHost, int? expectPort, bool expectValid)
    {
        bool parsed = HostAddress.TryParse(input, out string host, out int? port);
        bool valid = parsed && HostAddress.IsValidHost(host);

        bool ok = parsed == expectParsed && valid == expectValid && port == expectPort;
        if (expectHost != null) ok &= host == expectHost;

        Check(name, ok, ok ? null : $"得到 parsed={parsed} host='{host}' port={port?.ToString() ?? "-"} valid={valid}");
    }
}
