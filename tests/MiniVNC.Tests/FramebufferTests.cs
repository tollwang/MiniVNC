using MiniVNC.Encodings;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 帧缓冲写入与 CopyRect。重点是**越界必须裁剪而不是抛异常**——
/// 抛异常会被消息循环当成协议错误从而断开整条会话，而矩形宽高已用于解码、流并未错位。
/// </summary>
public static class FramebufferTests
{
    public static void Run()
    {
        Section("帧缓冲");

        // ---- 正常写入：逐像素落点正确 ----
        var fb = new Framebuffer(4, 2);
        byte[] src = new byte[2 * 2 * 4];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i + 1);
        fb.UpdateRectBgra32(2, 0, 2, 2, src);

        byte[] snap = Snapshot(fb);
        Check("未越界时逐像素写入正确",
            Pixel(snap, 4, 2, 0) == (1, 2, 3, 4) &&
            Pixel(snap, 4, 3, 0) == (5, 6, 7, 8) &&
            Pixel(snap, 4, 2, 1) == (9, 10, 11, 12) &&
            Pixel(snap, 4, 3, 1) == (13, 14, 15, 16) &&
            Pixel(snap, 4, 0, 0) == (0, 0, 0, 0),
            Hex(snap));

        // ---- 越界裁剪 ----
        var fb2 = new Framebuffer(100, 50);
        byte[] block = new byte[20 * 20 * 4];
        for (int i = 0; i < block.Length; i++) block[i] = 0x7F;

        CheckNoThrow("右下越界 → 裁剪而非抛异常", () => fb2.UpdateRectBgra32(90, 40, 20, 20, block));

        byte[] snap2 = Snapshot(fb2);
        Check("裁剪后落点正确且不污染相邻像素",
            Pixel(snap2, 100, 90, 40).B == 0x7F &&      // 裁剪区左上角已写
            Pixel(snap2, 100, 99, 49).B == 0x7F &&      // 右下角最后一个像素已写
            Pixel(snap2, 100, 89, 40).B == 0x00 &&      // 左邻未被污染
            Pixel(snap2, 100, 90, 39).B == 0x00);       // 上邻未被污染

        CheckNoThrow("完全在界外的矩形被忽略", () => fb2.UpdateRectBgra32(200, 200, 20, 20, block));
        CheckNoThrow("负数尺寸被忽略", () => fb2.UpdateRectBgra32(0, 0, -5, -5, block));

        // 源数据不足仍应报错（这是调用方的 bug，不是服务器的）
        bool threw = false;
        try { fb2.UpdateRectBgra32(0, 0, 20, 20, new byte[8]); }
        catch (ArgumentException) { threw = true; }
        Check("源数据长度不足 → 抛 ArgumentException", threw);

        // ---- CopyRect ----
        var fb3 = new Framebuffer(8, 4);
        byte[] filled = new byte[4 * 4 * 4];
        for (int i = 0; i < filled.Length; i++) filled[i] = 0x33;
        fb3.UpdateRectBgra32(0, 0, 4, 4, filled);

        CheckNoThrow("CopyRect 目标越界 → 裁剪而非抛异常", () => fb3.CopyRect(0, 0, 6, 0, 4, 4));
        byte[] snap3 = Snapshot(fb3);
        Check("CopyRect 裁剪后只复制界内部分",
            Pixel(snap3, 8, 6, 0).B == 0x33 &&
            Pixel(snap3, 8, 7, 0).B == 0x33 &&
            Pixel(snap3, 8, 5, 0).B == 0x00);

        // 重叠复制：向右移一列，源不能被自己覆盖
        var fb4 = new Framebuffer(4, 1);
        byte[] row = new byte[4 * 4];
        for (int x = 0; x < 4; x++) row[x * 4] = (byte)(x + 1);   // B 通道分别是 1,2,3,4
        fb4.UpdateRectBgra32(0, 0, 4, 1, row);
        fb4.CopyRect(0, 0, 1, 0, 3, 1);                            // 把 [1,2,3] 复制到 x=1..3
        byte[] snap4 = Snapshot(fb4);
        Check("重叠区域向右复制不自我覆盖",
            Pixel(snap4, 4, 1, 0).B == 1 && Pixel(snap4, 4, 2, 0).B == 2 && Pixel(snap4, 4, 3, 0).B == 3,
            Hex(snap4));

        // ---- 构造参数校验 ----
        bool badSize = false;
        try { _ = new Framebuffer(0, 100); } catch (ArgumentOutOfRangeException) { badSize = true; }
        Check("零尺寸帧缓冲被拒绝", badSize);

        bool tooBig = false;
        try { _ = new Framebuffer(20000, 20000); } catch (ArgumentOutOfRangeException) { tooBig = true; }
        Check("超大尺寸帧缓冲被拒绝（防溢出/OOM）", tooBig);
    }
}
