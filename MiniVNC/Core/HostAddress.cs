using System.Net;

namespace MiniVNC.Core;

/// <summary>
/// 主机地址输入的解析与校验。
/// 用户常把端口和主机写在一起（<c>mac.example.com:5901</c>），而 <see cref="System.Net.Sockets.TcpClient"/>
/// 只接受纯主机名/IP，整串传下去必然解析失败，所以在进入连接流程前统一在这里拆分。
/// </summary>
public static class HostAddress
{
    /// <summary>默认 VNC 端口。</summary>
    public const int DefaultPort = 5900;

    /// <summary>
    /// 从用户输入中拆出主机与可选端口。
    /// 支持 <c>host</c>、<c>host:port</c>、IPv6 字面量 <c>::1</c>、带方括号的 <c>[::1]:5901</c>。
    /// 不做主机名有效性判断（交给 <see cref="IsValidHost"/>），也不做 DNS 解析
    /// （域名解析由 <c>TcpClient.ConnectAsync(string, int, CancellationToken)</c> 内部完成）。
    /// </summary>
    /// <param name="input">用户输入的原始文本。</param>
    /// <param name="host">拆分出的主机名或 IP（已去空白）。</param>
    /// <param name="port">输入中显式带的端口；未指定时为 <c>null</c>。</param>
    /// <returns>输入非空且格式可解析时返回 true。</returns>
    public static bool TryParse(string? input, out string host, out int? port)
    {
        host = string.Empty;
        port = null;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0) return false;

        // [IPv6] 或 [IPv6]:port —— 方括号内整体是地址
        if (text[0] == '[')
        {
            int close = text.IndexOf(']');
            if (close < 0) return false;               // 括号未闭合
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length == 0) return host.Length > 0;
            if (rest[0] != ':') return false;          // ] 后只允许跟 :port
            return TryParsePort(rest[1..], out port) && host.Length > 0;
        }

        int colon = text.IndexOf(':');
        if (colon < 0)
        {
            host = text;
            return true;
        }

        // 多个冒号且没加方括号：只可能是裸 IPv6 字面量，整串当地址（无法区分末段是端口还是地址段）
        if (text.IndexOf(':', colon + 1) >= 0)
        {
            host = text;
            return true;
        }

        // 唯一冒号：host:port。冒号后不是合法端口时视为无效输入，不再静默把整串当主机名
        host = text[..colon];
        return host.Length > 0 && TryParsePort(text[(colon + 1)..], out port);
    }

    /// <summary>
    /// 校验主机名或 IP 的格式（不含端口，先经 <see cref="TryParse"/> 拆分）。
    /// 只做粗筛：IP 字面量直接放行，主机名限长度且不含空白与冒号。
    /// </summary>
    public static bool IsValidHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (IPAddress.TryParse(host, out _)) return true;   // IPv4 / IPv6 字面量
        if (host.Length >= 256) return false;               // DNS 名称上限 255
        foreach (var c in host)
        {
            if (char.IsWhiteSpace(c) || c == ':' || c == '/' || c == '\\' || c == '@') return false;
        }
        return true;
    }

    private static bool TryParsePort(string text, out int? port)
    {
        port = null;
        if (!int.TryParse(text, out int p) || p <= 0 || p > 65535) return false;
        port = p;
        return true;
    }
}
