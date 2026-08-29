using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 网络流的读取分帧。读侧带 64KB 缓冲、小读取复用暂存区，
/// 这两项优化都可能破坏"精确读取 N 字节"的语义，所以专门用分片到达的数据压一遍。
/// </summary>
public static class StreamTests
{
    public static async Task RunAsync()
    {
        Section("网络流读取");

        var (client, server, dispose) = await OpenLoopbackAsync();
        try
        {
            // 数据分多次、带间隔到达：每次读都不可能一次性拿满
            _ = Task.Run(async () =>
            {
                foreach (byte[] chunk in new[]
                {
                    new byte[] { 1, 2 },
                    new byte[] { 3 },
                    new byte[] { 4, 5, 6, 7 },
                    new byte[] { 8, 9, 10, 11, 12 },
                })
                {
                    await server.WriteAsync(chunk);
                    await server.FlushAsync();
                    await Task.Delay(20);
                }
            });

            byte[] a = await client.ReadExactlyAsync(3);
            ushort b = await client.ReadUInt16Async();
            byte c = await client.ReadByteAsync();
            byte[] d = await client.ReadExactlyAsync(1);
            uint e = await client.ReadUInt32Async();
            var f = await client.ReadSmallAsync(1);

            Check("分片到达时各种读取混用仍精确分帧",
                Hex(a) == "01-02-03" && b == 0x0405 && c == 6 && d[0] == 7
                && e == 0x08090A0Bu && f.Span[0] == 12,
                $"{Hex(a)} {b:X4} {c} {d[0]} {e:X8} {f.Span[0]}");

            // 读缓冲会预读：对端关闭后，缓冲里剩下的字节仍应能读出来
            await server.WriteAsync(new byte[] { 0xAA, 0xBB, 0xCC });
            await server.FlushAsync();
            await Task.Delay(50);
            server.Close();

            byte[] tail = await client.ReadExactlyAsync(3);
            Check("对端关闭后仍能读出已缓冲的字节", Hex(tail) == "AA-BB-CC", Hex(tail));

            bool threw = false;
            try { await client.ReadExactlyAsync(1); } catch (IOException) { threw = true; }
            Check("缓冲耗尽且对端已关闭 → 抛 IOException", threw);
        }
        finally
        {
            dispose();
        }

        // 暂存区读取的长度上限
        var (c2, _, dispose2) = await OpenLoopbackAsync();
        try
        {
            bool rejected = false;
            try { await c2.ReadSmallAsync(9); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check("ReadSmallAsync 超出暂存区容量被拒绝", rejected);
        }
        finally
        {
            dispose2();
        }
    }
}
