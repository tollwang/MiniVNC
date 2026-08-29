using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MiniVNC.Encodings;

namespace MiniVNC.Tests;

/// <summary>
/// 极简测试运行器。刻意不引入 xunit 等测试框架——整个仓库零 NuGet 依赖，
/// 而这些测试本质上是串行的集成测试（要开真实 socket），一个带退出码的控制台程序足够了。
/// </summary>
public static class TestRunner
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new();

    /// <summary>打印一个分组标题。</summary>
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");
    }

    /// <summary>断言一个条件，记录通过/失败。<paramref name="detail"/> 在失败时尤其有用。</summary>
    public static void Check(string name, bool ok, string? detail = null)
    {
        string line = $"{(ok ? "PASS" : "FAIL")}  {name}{(detail != null ? "  " + detail : "")}";
        Console.WriteLine(line);
        if (ok) _passed++;
        else { _failed++; _failures.Add(name + (detail != null ? "  " + detail : "")); }
    }

    /// <summary>把一段可能抛异常的操作记为一项断言（不抛即通过）。</summary>
    public static void CheckNoThrow(string name, Action action)
    {
        try { action(); Check(name, true); }
        catch (Exception ex) { Check(name, false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>输出汇总，返回进程退出码（0=全通过）。</summary>
    public static int Summarize()
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        if (_failed == 0)
        {
            Console.WriteLine($"全部通过：{_passed} 项");
            return 0;
        }
        Console.WriteLine($"{_passed} 项通过，{_failed} 项失败：");
        foreach (var f in _failures) Console.WriteLine("  · " + f);
        return 1;
    }

    // ---- 公用辅助 ----

    /// <summary>字节数组转可读十六进制。</summary>
    public static string Hex(byte[] b) => BitConverter.ToString(b);

    /// <summary>把帧缓冲内容读回托管数组（绕开 CopyTo 的非托管指针参数，无需 unsafe）。</summary>
    public static byte[] Snapshot(Framebuffer fb)
    {
        int stride = fb.Width * Framebuffer.BytesPerPixel;
        int size = stride * fb.Height;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            fb.CopyTo(buffer, stride);
            byte[] managed = new byte[size];
            Marshal.Copy(buffer, managed, 0, size);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>取帧缓冲某像素的 (B,G,R,A)。</summary>
    public static (byte B, byte G, byte R, byte A) Pixel(byte[] snapshot, int width, int x, int y)
    {
        int o = (y * width + x) * 4;
        return (snapshot[o], snapshot[o + 1], snapshot[o + 2], snapshot[o + 3]);
    }

    /// <summary>
    /// 建一对回环 TCP 连接：返回客户端侧的 <see cref="VncStream"/>、服务器侧的裸流，以及清理方法。
    /// </summary>
    public static async Task<(Network.VncStream Client, NetworkStream Server, Action Dispose)> OpenLoopbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptTcpClientAsync();
        var client = new Network.VncStream();
        await client.ConnectAsync("127.0.0.1", port);
        var server = await accept;

        return (client, server.GetStream(), () =>
        {
            try { server.Close(); } catch { }
            try { listener.Stop(); } catch { }
            try { client.Dispose(); } catch { }
        });
    }

    /// <summary>把数据按小分片写入，逼出读缓冲与分帧的边界情况。</summary>
    public static async Task FeedInChunksAsync(NetworkStream s, byte[] data, int chunkSize = 64)
    {
        for (int off = 0; off < data.Length; off += chunkSize)
        {
            await s.WriteAsync(data.AsMemory(off, Math.Min(chunkSize, data.Length - off)));
            await s.FlushAsync();
        }
    }

    /// <summary>从流中精确读取 n 字节，带超时（避免测试挂死）。</summary>
    public static async Task<byte[]> ReadExactAsync(NetworkStream s, int n, int timeoutMs = 5000)
    {
        byte[] buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            var read = s.ReadAsync(buf.AsMemory(got, n - got)).AsTask();
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)) != read)
                throw new TimeoutException($"等待 {n} 字节超时（已收到 {got}）");
            int r = read.Result;
            if (r == 0) throw new IOException("对端已关闭连接");
            got += r;
        }
        return buf;
    }

    /// <summary>大端序读 16 位。</summary>
    public static int Be16(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];

    /// <summary>构造一个帧缓冲更新矩形头（x, y, w, h, encoding）。</summary>
    public static byte[] RectHeader(int x, int y, int w, int h, int encoding) => new byte[]
    {
        (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y,
        (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h,
        (byte)(encoding >> 24), (byte)(encoding >> 16), (byte)(encoding >> 8), (byte)encoding
    };
}
