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
        await MainSessionAsync();
        await UnknownMessageAsync();
    }

    private static async Task MainSessionAsync()
    {
        Section("客户端完整会话（假 VNC 服务器）");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        using var client = new VncClient();
        var resizes = new List<(int W, int H)>();
        var cursors = new List<CursorUpdateEventArgs>();
        client.DesktopResized += (s, e) => resizes.Add((e.Width, e.Height));
        client.CursorChanged += (s, e) => cursors.Add(e);

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

            // ---- Cursor 伪编码(-239)：x/y 是热点，w/h 是光标尺寸，随后是像素 + 1bpp 透明掩码 ----
            // 2x2 光标，掩码位=1 表示不透明（MSB 在前）：
            //   行0 掩码 1000_0000 → 左像素可见、右像素透明
            //   行1 掩码 0100_0000 → 左像素透明、右像素可见
            var cur = new List<byte> { 0, 0, 0, 1 };
            cur.AddRange(RectHeader(1, 1, 2, 2, -239));         // 热点 (1,1)
            cur.AddRange(new byte[] { 0, 255, 0, 0 });          // (0,0) 红
            cur.AddRange(new byte[] { 0, 0, 255, 0 });          // (1,0) 绿
            cur.AddRange(new byte[] { 0, 0, 0, 255 });          // (0,1) 蓝
            cur.AddRange(new byte[] { 0, 10, 20, 30 });         // (1,1) 自定义
            cur.Add(0b1000_0000);
            cur.Add(0b0100_0000);
            await s.WriteAsync(cur.ToArray());
            await Task.Delay(300);

            Check("Cursor 伪编码触发一次 CursorChanged", cursors.Count == 1, $"收到 {cursors.Count} 次");
            if (cursors.Count == 1)
            {
                var c = cursors[0];
                byte[] p = c.Bgra;
                Check("光标尺寸与热点正确",
                    c.Width == 2 && c.Height == 2 && c.HotspotX == 1 && c.HotspotY == 1);
                Check("光标像素转为 BGRA 且掩码位=0 的像素置为透明",
                    p[0] == 0 && p[1] == 0 && p[2] == 255 && p[3] == 255 &&      // (0,0) 红，不透明
                    p[7] == 0 &&                                                 // (1,0) 透明
                    p[11] == 0 &&                                                // (0,1) 透明
                    p[12] == 30 && p[13] == 20 && p[14] == 10 && p[15] == 255,   // (1,1) 不透明
                    Hex(p));
            }

            // 完全相同的光标重复推送应被去重（否则会频繁重建 HCURSOR）
            await s.WriteAsync(cur.ToArray());
            await Task.Delay(300);
            Check("重复推送相同光标被去重", cursors.Count == 1, $"收到 {cursors.Count} 次");

            // 空光标 = 请求隐藏，应再触发一次事件（尺寸 0）
            var hide = new List<byte> { 0, 0, 0, 1 };
            hide.AddRange(RectHeader(0, 0, 0, 0, -239));
            await s.WriteAsync(hide.ToArray());
            await Task.Delay(300);
            Check("空光标触发隐藏事件",
                cursors.Count == 2 && cursors[^1].Width == 0 && cursors[^1].Height == 0);

            // ---- CopyRect：在消息循环里内联处理（不走 IEncoding）----
            var paint = new List<byte> { 0, 0, 0, 1 };
            paint.AddRange(RectHeader(0, 0, 2, 1, 0));           // Raw 2x1 写到左上角
            paint.AddRange(new byte[] { 0, 7, 8, 9, 0, 7, 8, 9 });
            await s.WriteAsync(paint.ToArray());
            await Task.Delay(200);

            var copy = new List<byte> { 0, 0, 0, 1 };
            copy.AddRange(RectHeader(100, 50, 2, 1, 1));         // CopyRect 目标 (100,50)
            copy.AddRange(new byte[] { 0, 0, 0, 0 });            // 源坐标 (0,0)
            await s.WriteAsync(copy.ToArray());
            await Task.Delay(300);

            byte[] afterCopy = Snapshot(client.Framebuffer!);
            Check("CopyRect 在帧缓冲内正确搬移像素",
                Pixel(afterCopy, NewW, 100, 50) == (9, 8, 7, 255) &&
                Pixel(afterCopy, NewW, 101, 50) == (9, 8, 7, 255));

            // ---- SetColorMapEntries：真彩色下忽略，但必须把数据读干净，否则整条流错位 ----
            var cmap = new List<byte> { 1, 0, 0, 1, 0, 3 };      // 类型1, 填充, firstColor=1, numColors=3
            cmap.AddRange(new byte[3 * 6]);                       // 3 个颜色 × R/G/B 各 2 字节
            await s.WriteAsync(cmap.ToArray());

            // 紧跟一帧正常更新：能正确解码就说明上面的跳过没有多读也没有少读
            var afterCmap = new List<byte> { 0, 0, 0, 1 };
            afterCmap.AddRange(RectHeader(5, 5, 1, 1, 0));
            afterCmap.AddRange(new byte[] { 0, 1, 2, 3 });
            await s.WriteAsync(afterCmap.ToArray());
            await Task.Delay(300);

            Check("忽略 SetColorMapEntries 后流未错位",
                Pixel(Snapshot(client.Framebuffer!), NewW, 5, 5) == (3, 2, 1, 255));

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

    /// <summary>
    /// 未知的服务器消息类型必须导致断开：流已经无法继续解析，
    /// 继续读下去只会把垃圾当协议数据，还不如断开触发重连。
    /// </summary>
    private static async Task UnknownMessageAsync()
    {
        Section("未知消息类型的处理");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        using var client = new VncClient();
        bool disconnected = false;
        client.Disconnected += (s, e) => disconnected = true;

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
            await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n"));
            await ReadExactAsync(s, 12);
            await s.WriteAsync(new byte[] { 1, 1 });
            await ReadExactAsync(s, 1);
            await s.WriteAsync(new byte[] { 0, 0, 0, 0 });
            await ReadExactAsync(s, 1);
            await s.WriteAsync(ServerInit(320, 240, "X"));
            await ReadExactAsync(s, 20);
            byte[] h = await ReadExactAsync(s, 4);
            await ReadExactAsync(s, Be16(h, 2) * 4);
            await connecting;
            await ReadExactAsync(s, 10);                 // 首个完整刷新请求

            Check("连接已建立", client.IsConnected);

            await s.WriteAsync(new byte[] { 99 });       // RFB 未定义的消息类型
            await Task.Delay(500);

            Check("收到未知消息类型 → 断开连接", !client.IsConnected);
            Check("断开时触发 Disconnected 事件", disconnected);
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
