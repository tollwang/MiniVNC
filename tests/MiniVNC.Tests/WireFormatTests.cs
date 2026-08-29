using MiniVNC.Protocol;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 客户端消息的线格式，逐字节比对 RFB 规范。
/// 这些消息是拼装成一个缓冲后一次写出的（为了让一次鼠标移动只发一个 TCP 包），
/// 所以字节布局必须由测试钉死——写错一个填充字节，服务器侧就会整条流错位。
/// </summary>
public static class WireFormatTests
{
    public static async Task RunAsync()
    {
        Section("客户端报文线格式");

        var (client, server, dispose) = await OpenLoopbackAsync();
        try
        {
            var proto = new RfbProtocol(client);

            // PointerEvent: u8 type=5 | u8 buttonMask | u16 x | u16 y
            proto.WritePointerEvent(0x05, 0x1234, 0x0ABC);
            Check("PointerEvent", Hex(await ReadExactAsync(server, 6)) == "05-05-12-34-0A-BC");

            // KeyEvent: u8 type=4 | u8 down | u16 pad | u32 keysym
            proto.WriteKeyEvent(true, 0xFFEB);
            Check("KeyEvent（按下 Command 键）", Hex(await ReadExactAsync(server, 8)) == "04-01-00-00-00-00-FF-EB");

            proto.WriteKeyEvent(false, 0x0061);
            Check("KeyEvent（松开 'a'）", Hex(await ReadExactAsync(server, 8)) == "04-00-00-00-00-00-00-61");

            // FramebufferUpdateRequest: u8 type=3 | u8 incremental | u16 x,y,w,h
            proto.WriteFramebufferUpdateRequest(true, 0, 0, 1920, 1080);
            Check("FramebufferUpdateRequest（增量，1920x1080）",
                Hex(await ReadExactAsync(server, 10)) == "03-01-00-00-00-00-07-80-04-38");

            proto.WriteFramebufferUpdateRequest(false, 0, 0, 1920, 1080);
            Check("FramebufferUpdateRequest（完整刷新）",
                Hex(await ReadExactAsync(server, 10)) == "03-00-00-00-00-00-07-80-04-38");

            // EnableContinuousUpdates: u8 type=150(0x96) | u8 enable | u16 x,y,w,h
            proto.WriteEnableContinuousUpdates(true, 0, 0, 1920, 1080);
            Check("EnableContinuousUpdates",
                Hex(await ReadExactAsync(server, 10)) == "96-01-00-00-00-00-07-80-04-38");

            // SetPixelFormat: u8 type=0 | 3 字节填充 | 16 字节像素格式
            var pf = new PixelFormat(32, 24, true, true, 255, 255, 255, 16, 8, 0);
            proto.WriteSetPixelFormat(pf);
            byte[] spf = await ReadExactAsync(server, 20);
            Check("SetPixelFormat",
                spf[0] == 0 && spf[1] == 0 && spf[2] == 0 && spf[3] == 0
                && spf.AsSpan(4, 16).SequenceEqual(pf.ToByteArray()), Hex(spf));

            // SetEncodings: u8 type=2 | u8 pad | u16 count | count × s32（伪编码是负数，按补码发）
            int[] encodings = { 16, 5, 1, 0, -239, -223, -313 };
            proto.WriteSetEncodings(encodings);
            byte[] se = await ReadExactAsync(server, 4 + encodings.Length * 4);
            bool ok = se[0] == 2 && se[1] == 0 && Be16(se, 2) == encodings.Length;
            for (int i = 0; i < encodings.Length; i++)
            {
                int v = (se[4 + i * 4] << 24) | (se[5 + i * 4] << 16) | (se[6 + i * 4] << 8) | se[7 + i * 4];
                if (v != encodings[i]) ok = false;
            }
            Check("SetEncodings（含负数伪编码的补码编码）", ok, Hex(se));

            // ClientCutText: u8 type=6 | 3 字节填充 | u32 长度 | UTF-8 字节
            proto.WriteCutText("你好 hi 🙂");
            byte[] head = await ReadExactAsync(server, 8);
            int len = (head[4] << 24) | (head[5] << 16) | (head[6] << 8) | head[7];
            byte[] body = await ReadExactAsync(server, len);
            Check("ClientCutText（中文 + Emoji 按 UTF-8 发送）",
                head[0] == 6 && head[1] == 0 && head[2] == 0 && head[3] == 0
                && System.Text.Encoding.UTF8.GetString(body) == "你好 hi 🙂",
                $"len={len}");

            // ClientInit / 协议版本
            proto.WriteClientInit(true);
            Check("ClientInit（共享桌面）", (await ReadExactAsync(server, 1))[0] == 1);

            proto.WriteSecurityType(30);
            Check("安全类型选择", (await ReadExactAsync(server, 1))[0] == 30);
        }
        finally
        {
            dispose();
        }

        // 剪贴板解码：先严格 UTF-8，失败回退 Latin-1
        Check("剪贴板解码 UTF-8",
            RfbProtocol.DecodeCutText(System.Text.Encoding.UTF8.GetBytes("中文abc")) == "中文abc");
        Check("剪贴板解码非法 UTF-8 时回退 Latin-1",
            RfbProtocol.DecodeCutText(new byte[] { 0xE9, 0x74, 0xE9 }) == "été");
        Check("剪贴板解码空数据", RfbProtocol.DecodeCutText(Array.Empty<byte>()) == string.Empty);
    }
}
