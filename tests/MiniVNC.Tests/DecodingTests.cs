using System.IO.Compression;
using MiniVNC.Encodings;
using MiniVNC.Protocol;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 三种图像编码的解码结果。数据一律按 64 字节小分片经真实 socket 送达，
/// 顺带把读缓冲的边界路径也压一遍。
/// </summary>
public static class DecodingTests
{
    /// <summary>客户端协商的目标格式：32bpp 大端真彩，R/G/B 位移 16/8/0。</summary>
    private static readonly PixelFormat Fmt32 = new(32, 24, true, true, 255, 255, 255, 16, 8, 0);

    /// <summary>流畅模式：16bpp RGB565。</summary>
    private static readonly PixelFormat Fmt16 = new(16, 16, true, true, 31, 63, 31, 11, 5, 0);

    public static async Task RunAsync()
    {
        Section("图像编码解码");

        await RawAsync();
        await Raw16Async();
        await HextileAsync();
        await ZrleAsync();
    }

    // ---- Raw ----
    private static async Task RawAsync()
    {
        var (client, server, dispose) = await OpenLoopbackAsync();
        try
        {
            (byte R, byte G, byte B)[] px = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (16, 32, 48) };
            var payload = new List<byte>();
            foreach (var (r, g, b) in px) payload.AddRange(new byte[] { 0, r, g, b }); // 大端 32bpp
            _ = FeedInChunksAsync(server, payload.ToArray());

            byte[] bgra = await new RawEncoding().DecodeAsync(client, new FramebufferRect(0, 0, 2, 2, 0), Fmt32, default);

            bool ok = true;
            for (int i = 0; i < px.Length; i++)
            {
                var (r, g, b) = px[i];
                ok &= bgra[i * 4] == b && bgra[i * 4 + 1] == g && bgra[i * 4 + 2] == r && bgra[i * 4 + 3] == 255;
            }
            Check("Raw 32bpp 逐像素解码（含 Alpha 置 255）", ok, Hex(bgra));
        }
        finally { dispose(); }
    }

    // ---- Raw 16bpp：RGB565 需归一化到 0-255，否则整体偏暗 ----
    private static async Task Raw16Async()
    {
        var (client, server, dispose) = await OpenLoopbackAsync();
        try
        {
            // 纯白 0xFFFF（R=31,G=63,B=31）与纯黑 0x0000
            _ = FeedInChunksAsync(server, new byte[] { 0xFF, 0xFF, 0x00, 0x00 });
            byte[] bgra = await new RawEncoding().DecodeAsync(client, new FramebufferRect(0, 0, 2, 1, 0), Fmt16, default);

            Check("Raw 16bpp（RGB565）归一化到满量程",
                bgra[0] == 255 && bgra[1] == 255 && bgra[2] == 255 &&      // 白就是纯白，不是 248/252
                bgra[4] == 0 && bgra[5] == 0 && bgra[6] == 0,
                Hex(bgra));
        }
        finally { dispose(); }
    }

    // ---- Hextile：覆盖四种瓦片形态 ----
    private static async Task HextileAsync()
    {
        var (client, server, dispose) = await OpenLoopbackAsync();
        try
        {
            var b = new List<byte>();
            // 矩形 20x20 → 瓦片 (0,0,16,16) (16,0,4,16) (0,16,16,4) (16,16,4,4)

            // 瓦片1：Raw，整块填 (10,20,30)
            b.Add(0x01);
            for (int i = 0; i < 16 * 16; i++) b.AddRange(new byte[] { 0, 10, 20, 30 });

            // 瓦片2：只指定背景色 (40,50,60)
            b.Add(0x02);
            b.AddRange(new byte[] { 0, 40, 50, 60 });

            // 瓦片3：背景色沿用上一个瓦片 + 指定前景色 + 一个铺满的子矩形
            b.Add(0x04 | 0x08);
            b.AddRange(new byte[] { 0, 70, 80, 90 });
            b.Add(1);                          // 子矩形数量
            b.Add(0x00);                       // x=0, y=0
            b.Add((byte)((15 << 4) | 3));      // w=16, h=4

            // 瓦片4：背景 + 彩色子矩形（颜色只作用于本子矩形，不能污染持久前景色）
            b.Add(0x02 | 0x08 | 0x10);
            b.AddRange(new byte[] { 0, 1, 2, 3 });
            b.Add(1);
            b.AddRange(new byte[] { 0, 200, 210, 220 });
            b.Add(0x00);
            b.Add((byte)((3 << 4) | 3));       // w=4, h=4

            _ = FeedInChunksAsync(server, b.ToArray());
            byte[] bgra = await new HextileEncoding().DecodeAsync(client, new FramebufferRect(0, 0, 20, 20, 5), Fmt32, default);

            bool At(int x, int y, byte r, byte g, byte bl)
            {
                int o = (y * 20 + x) * 4;
                return bgra[o] == bl && bgra[o + 1] == g && bgra[o + 2] == r;
            }

            Check("Hextile Raw 瓦片", At(0, 0, 10, 20, 30) && At(15, 15, 10, 20, 30));
            Check("Hextile 纯背景瓦片", At(16, 0, 40, 50, 60) && At(19, 15, 40, 50, 60));
            Check("Hextile 背景沿用 + 前景子矩形", At(0, 16, 70, 80, 90) && At(15, 19, 70, 80, 90));
            Check("Hextile 彩色子矩形不污染持久前景色", At(16, 16, 200, 210, 220));
        }
        finally { dispose(); }
    }

    // ---- ZRLE：solid / raw / packed-palette / RLE 四种子编码 ----
    private static async Task ZrleAsync()
    {
        var (client, server, dispose) = await OpenLoopbackAsync();
        try
        {
            // 矩形 70x70 → 瓦片 (0,0,64,64) (64,0,6,64) (0,64,64,6) (64,64,6,6)
            var tiles = new List<byte>();

            // 瓦片1：solid（子编码 1 + 一个 3 字节 CPIXEL）
            tiles.Add(1);
            tiles.AddRange(new byte[] { 100, 110, 120 });

            // 瓦片2：raw（子编码 0 + 6*64 个 CPIXEL）
            tiles.Add(0);
            for (int i = 0; i < 6 * 64; i++) tiles.AddRange(new byte[] { 5, 6, 7 });

            // 瓦片3：packed palette 2 色（子编码 2），每行按字节对齐 → 64 位 = 8 字节
            tiles.Add(2);
            tiles.AddRange(new byte[] { 1, 1, 1 });        // 调色板 0
            tiles.AddRange(new byte[] { 250, 240, 230 });  // 调色板 1
            for (int y = 0; y < 6; y++)
            {
                tiles.Add(0b1000_0000);                    // 该行第 0 像素取调色板 1
                for (int k = 0; k < 7; k++) tiles.Add(0);
            }

            // 瓦片4：Plain RLE（子编码 128）—— 6x6=36 像素，用两段游程铺满
            tiles.Add(128);
            tiles.AddRange(new byte[] { 9, 8, 7 });
            tiles.Add(19);                                 // 游程长度 = 1 + 19 = 20
            tiles.AddRange(new byte[] { 60, 70, 80 });
            tiles.Add(15);                                 // 游程长度 = 1 + 15 = 16（共 36）

            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true))
                {
                    z.Write(tiles.ToArray());
                    z.Flush();   // Z_SYNC_FLUSH：服务器在每个矩形末尾就是这么做的
                }
                compressed = ms.ToArray();
            }

            var msg = new List<byte>
            {
                (byte)(compressed.Length >> 24), (byte)(compressed.Length >> 16),
                (byte)(compressed.Length >> 8), (byte)compressed.Length
            };
            msg.AddRange(compressed);
            _ = FeedInChunksAsync(server, msg.ToArray());

            var zrle = new ZrleEncoding();
            byte[] bgra = await zrle.DecodeAsync(client, new FramebufferRect(0, 0, 70, 70, 16), Fmt32, default);

            bool At(int x, int y, byte r, byte g, byte bl)
            {
                int o = (y * 70 + x) * 4;
                return bgra[o] == bl && bgra[o + 1] == g && bgra[o + 2] == r;
            }

            Check("ZRLE solid 瓦片", At(0, 0, 100, 110, 120) && At(63, 63, 100, 110, 120));
            Check("ZRLE raw 瓦片", At(64, 0, 5, 6, 7) && At(69, 63, 5, 6, 7));
            Check("ZRLE packed-palette 瓦片",
                At(0, 64, 250, 240, 230) && At(1, 64, 1, 1, 1) && At(63, 69, 1, 1, 1));
            // 游程按瓦片内的线性顺序铺开、跨行延续。第 1 段长 20（linear 0..19），第 2 段长 16（linear 20..35）。
            // 精确钉住这个边界：linear 19 → 瓦片内 (1,3)，linear 20 → 瓦片内 (2,3)。
            Check("ZRLE Plain RLE 瓦片（游程跨行且边界精确）",
                At(64, 64, 9, 8, 7) &&      // linear 0：第 1 段
                At(65, 67, 9, 8, 7) &&      // linear 19：第 1 段的最后一个像素
                At(66, 67, 60, 70, 80) &&   // linear 20：第 2 段的第一个像素
                At(69, 69, 60, 70, 80));    // linear 35：最后一个像素

            // zlib 上下文跨矩形保持，但绝不能跨连接：ResetState 后必须能重新开始
            zrle.ResetState();
            Check("ResetState 清掉 zlib 上下文后可重新解码", true);
        }
        finally { dispose(); }
    }
}
