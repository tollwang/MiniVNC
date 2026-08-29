using System.Net;
using System.Net.Sockets;
using MiniVNC.Core;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 用一个假 VNC 服务器把 <see cref="VncClient"/> 的完整流程跑通：
/// 握手 → 无认证 → 初始化 → 消息循环。覆盖那些只有在真实时序下才暴露的行为：
/// 连续更新的启用与停用回退、DesktopSize 重建帧缓冲、断开不再空等超时。
/// </summary>
public static class SessionTests
{
    private const int InitialW = 800, InitialH = 600;
    private const int NewW = 1280, NewH = 720;

    public static async Task RunAsync()
    {
        Section("客户端完整会话（假 VNC 服务器）");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        using var client = new VncClient();
        var resizes = new List<(int W, int H)>();
        client.DesktopResized += (s, e) => resizes.Add((e.Width, e.Height));

        var connecting = Task.Run(async () =>
        {
            await client.ConnectAsync("127.0.0.1", port);
            await client.AuthenticateAsync("", "");
            await client.InitializeAsync();
            client.StartUpdateLoop();
        });

        using var server = await accept;
        var s = server.GetStream();

        try
        {
            // ---- 握手 ----
            await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n"));
            Check("客户端回应协议版本 3.8",
                System.Text.Encoding.ASCII.GetString(await ReadExactAsync(s, 12)) == "RFB 003.008\n");

            // ---- 安全类型：只提供 None(1) ----
            await s.WriteAsync(new byte[] { 1, 1 });
            Check("客户端选择无认证", (await ReadExactAsync(s, 1))[0] == 1);
            await s.WriteAsync(new byte[] { 0, 0, 0, 0 });     // SecurityResult = 成功

            // ---- 初始化 ----
            Check("客户端发送 ClientInit(shared=1)", (await ReadExactAsync(s, 1))[0] == 1);
            await s.WriteAsync(ServerInit(InitialW, InitialH, "FakeMac"));

            await ReadExactAsync(s, 20);                        // SetPixelFormat
            byte[] encHead = await ReadExactAsync(s, 4);
            int encCount = Be16(encHead, 2);
            byte[] encBody = await ReadExactAsync(s, encCount * 4);
            var encodings = new List<int>();
            for (int i = 0; i < encCount; i++)
                encodings.Add((encBody[i * 4] << 24) | (encBody[i * 4 + 1] << 16)
                            | (encBody[i * 4 + 2] << 8) | encBody[i * 4 + 3]);

            Check("协商编码包含 ZRLE/Hextile/Raw",
                encodings.Contains(16) && encodings.Contains(5) && encodings.Contains(0));
            Check("协商伪编码 Cursor(-239) / DesktopSize(-223) / ContinuousUpdates(-313)",
                encodings.Contains(-239) && encodings.Contains(-223) && encodings.Contains(-313),
                string.Join(",", encodings));

            await connecting;
            Check("初始化后帧缓冲尺寸正确",
                client.FramebufferWidth == InitialW && client.FramebufferHeight == InitialH
                && client.Framebuffer?.Width == InitialW && client.Framebuffer?.Height == InitialH);
            Check("桌面名称已读取", client.ServerName == "FakeMac", client.ServerName);

            // ---- StartUpdateLoop 发出的首个完整刷新请求 ----
            byte[] req = await ReadExactAsync(s, 10);
            Check("启动时请求一次完整刷新（incremental=0）",
                req[0] == 3 && req[1] == 0 && Be16(req, 6) == InitialW && Be16(req, 8) == InitialH, Hex(req));

            // ---- 连续更新：首次 EndOfContinuousUpdates 表示服务器支持 ----
            await s.WriteAsync(new byte[] { 150 });
            byte[] enable = await ReadExactAsync(s, 10);
            Check("收到 EndOfContinuousUpdates → 发出 EnableContinuousUpdates(enable=1)",
                enable[0] == 150 && enable[1] == 1 && Be16(enable, 6) == InitialW, Hex(enable));

            // 连续更新开启后，收到帧不应再逐帧请求（下面 DesktopSize 的两个报文即可证明没有多余请求）

            // ---- DesktopSize：远端分辨率变化 ----
            var upd = new List<byte> { 0, 0, 0, 1 };            // FramebufferUpdate, pad, 1 个矩形
            upd.AddRange(RectHeader(0, 0, NewW, NewH, -223));    // DesktopSize，无像素数据
            await s.WriteAsync(upd.ToArray());

            byte[] after1 = await ReadExactAsync(s, 10);
            byte[] after2 = await ReadExactAsync(s, 10);
            Check("分辨率变化 → 按新区域重开连续更新",
                after1[0] == 150 && after1[1] == 1 && Be16(after1, 6) == NewW && Be16(after1, 8) == NewH, Hex(after1));
            Check("分辨率变化 → 请求一次完整刷新（新尺寸）",
                after2[0] == 3 && after2[1] == 0 && Be16(after2, 6) == NewW && Be16(after2, 8) == NewH, Hex(after2));
            Check("帧缓冲已按新尺寸重建",
                client.FramebufferWidth == NewW && client.Framebuffer?.Width == NewW
                && client.FramebufferHeight == NewH && client.Framebuffer?.Height == NewH,
                $"{client.Framebuffer?.Width}x{client.Framebuffer?.Height}");
            Check("触发一次 DesktopResized 事件且尺寸正确",
                resizes.Count == 1 && resizes[0] == (NewW, NewH));

            // ---- 新尺寸下继续解码：写到右下角（这个坐标在原尺寸下是越界的）----
            var pix = new List<byte> { 0, 0, 0, 1 };
            pix.AddRange(RectHeader(NewW - 2, NewH - 1, 2, 1, 0));   // Raw 2x1
            pix.AddRange(new byte[] { 0, 11, 22, 33, 0, 44, 55, 66 });
            await s.WriteAsync(pix.ToArray());
            await Task.Delay(300);

            byte[] snap = Snapshot(client.Framebuffer!);
            Check("新尺寸下 Raw 解码落点正确",
                Pixel(snap, NewW, NewW - 2, NewH - 1) == (33, 22, 11, 255) &&
                Pixel(snap, NewW, NewW - 1, NewH - 1) == (66, 55, 44, 255));

            // ---- 连续更新被服务器停用：再次收到 EndOfContinuousUpdates ----
            await s.WriteAsync(new byte[] { 150 });
            byte[] fallback = await ReadExactAsync(s, 10);
            Check("再次收到 EndOfContinuousUpdates → 退回请求-应答并补发增量请求",
                fallback[0] == 3 && fallback[1] == 1 && Be16(fallback, 6) == NewW, Hex(fallback));

            // 退回之后必须恢复"收到一帧就再请求一帧"，否则画面会永久冻结
            var tiny = new List<byte> { 0, 0, 0, 1 };
            tiny.AddRange(RectHeader(0, 0, 1, 1, 0));
            tiny.AddRange(new byte[] { 0, 1, 2, 3 });
            await s.WriteAsync(tiny.ToArray());
            byte[] next = await ReadExactAsync(s, 10);
            Check("退回后每收到一帧继续请求增量更新", next[0] == 3 && next[1] == 1, Hex(next));

            // ---- 服务器剪贴板 ----
            var cut = new List<byte> { 3, 0, 0, 0 };
            byte[] text = System.Text.Encoding.UTF8.GetBytes("来自 Mac 的文本");
            cut.AddRange(new byte[] { 0, 0, 0, (byte)text.Length });
            cut.AddRange(text);
            string? received = null;
            client.ServerClipboardChanged += (o, t) => received = t;
            await s.WriteAsync(cut.ToArray());
            await Task.Delay(300);
            Check("接收服务器剪贴板（UTF-8 中文）", received == "来自 Mac 的文本", received);

            // ---- 断开：修复前会因"先等待后关流"卡满 2 秒 ----
            var sw = System.Diagnostics.Stopwatch.StartNew();
            client.Disconnect();
            sw.Stop();
            Check("空闲连接上 Disconnect 不再空等超时", sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds}ms");
            Check("断开后 IsConnected 为 false", !client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>构造 ServerInit 报文：宽(2) 高(2) 像素格式(16) 名称长度(4) 名称。</summary>
    private static byte[] ServerInit(int w, int h, string name)
    {
        var init = new List<byte>
        {
            (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h,
            32, 24, 1, 1,                        // bpp, depth, bigEndian, trueColor
            0, 255, 0, 255, 0, 255,              // R/G/B max
            16, 8, 0,                            // R/G/B shift
            0, 0, 0                              // 填充
        };
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        init.AddRange(new byte[] { 0, 0, 0, (byte)nameBytes.Length });
        init.AddRange(nameBytes);
        return init.ToArray();
    }
}
