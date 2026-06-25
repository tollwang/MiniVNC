namespace MiniVNC.Protocol;

/// <summary>
/// 表示VNC RFB协议中的像素格式结构。
/// 定义了像素数据的位深度、颜色通道分布和字节序。
/// </summary>
public readonly record struct PixelFormat(
    /// <summary>每个像素的位数（如32、16、8）。</summary>
    byte BitsPerPixel,

    /// <summary>颜色深度，实际使用的颜色位数。</summary>
    byte Depth,

    /// <summary>是否使用大端序存储像素数据。</summary>
    bool BigEndian,

    /// <summary>是否使用真彩色模式（非调色板模式）。</summary>
    bool TrueColor,

    /// <summary>红色通道的最大值。</summary>
    ushort RedMax,

    /// <summary>绿色通道的最大值。</summary>
    ushort GreenMax,

    /// <summary>蓝色通道的最大值。</summary>
    ushort BlueMax,

    /// <summary>红色通道在像素值中的位移。</summary>
    byte RedShift,

    /// <summary>绿色通道在像素值中的位移。</summary>
    byte GreenShift,

    /// <summary>蓝色通道在像素值中的位移。</summary>
    byte BlueShift
)
{
    /// <summary>
    /// 获取RFB协议默认的32位像素格式。
    /// 格式为32bpp，深度24，大端序，真彩色，各颜色通道最大值为255。
    /// </summary>
    public static PixelFormat Default => new(
        BitsPerPixel: 32,
        Depth: 24,
        BigEndian: true,
        TrueColor: true,
        RedMax: 255,
        GreenMax: 255,
        BlueMax: 255,
        RedShift: 16,
        GreenShift: 8,
        BlueShift: 0
    );

    /// <summary>
    /// 将像素格式序列化为16字节数组，用于网络传输。
    /// 布局符合RFB协议规范。
    /// </summary>
    /// <returns>16字节的序列化像素格式数据。</returns>
    public byte[] ToByteArray()
    {
        byte[] bytes = new byte[16];
        bytes[0] = BitsPerPixel;
        bytes[1] = Depth;
        bytes[2] = (byte)(BigEndian ? 1 : 0);
        bytes[3] = (byte)(TrueColor ? 1 : 0);
        bytes[4] = (byte)(RedMax >> 8);
        bytes[5] = (byte)(RedMax & 0xFF);
        bytes[6] = (byte)(GreenMax >> 8);
        bytes[7] = (byte)(GreenMax & 0xFF);
        bytes[8] = (byte)(BlueMax >> 8);
        bytes[9] = (byte)(BlueMax & 0xFF);
        bytes[10] = RedShift;
        bytes[11] = GreenShift;
        bytes[12] = BlueShift;
        // bytes[13..15] 填充字节，保持为0
        return bytes;
    }

    /// <summary>
    /// 从16字节数组反序列化为像素格式结构。
    /// </summary>
    /// <param name="bytes">16字节的像素格式数据。</param>
    /// <returns>解析后的 <see cref="PixelFormat"/> 结构。</returns>
    /// <exception cref="ArgumentException">数组长度不为16时抛出。</exception>
    public static PixelFormat FromByteArray(byte[] bytes)
    {
        if (bytes.Length < 16)
            throw new ArgumentException("Pixel format requires 16 bytes.", nameof(bytes));

        return new PixelFormat(
            BitsPerPixel: bytes[0],
            Depth: bytes[1],
            BigEndian: bytes[2] != 0,
            TrueColor: bytes[3] != 0,
            RedMax: (ushort)((bytes[4] << 8) | bytes[5]),
            GreenMax: (ushort)((bytes[6] << 8) | bytes[7]),
            BlueMax: (ushort)((bytes[8] << 8) | bytes[9]),
            RedShift: bytes[10],
            GreenShift: bytes[11],
            BlueShift: bytes[12]
        );
    }

    /// <summary>
    /// 每个像素占用的字节数（向上取整）。
    /// </summary>
    public int BytesPerPixel => (BitsPerPixel + 7) / 8;

    /// <summary>
    /// 按本像素格式从字节数组中读取一个像素的原始数值。
    /// </summary>
    /// <param name="data">源字节数组。</param>
    /// <param name="offset">起始偏移。</param>
    /// <returns>组装后的像素数值（按颜色通道位移/掩码解释）。</returns>
    public uint ReadPixel(byte[] data, int offset)
    {
        return BytesPerPixel switch
        {
            4 => BigEndian
                ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
                : ((uint)data[offset + 3] << 24) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 1] << 8) | data[offset],
            3 => BigEndian
                ? ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2]
                : ((uint)data[offset + 2] << 16) | ((uint)data[offset + 1] << 8) | data[offset],
            2 => BigEndian
                ? (uint)((data[offset] << 8) | data[offset + 1])
                : (uint)((data[offset + 1] << 8) | data[offset]),
            _ => data[offset]
        };
    }

    /// <summary>
    /// 将一个原始像素数值转换为 BGRA32（B,G,R,A=255）并写入目标数组。
    /// </summary>
    /// <param name="pixel">原始像素数值。</param>
    /// <param name="dest">目标字节数组（至少4字节空间）。</param>
    /// <param name="offset">写入偏移。</param>
    public void WriteBgra32(uint pixel, byte[] dest, int offset)
    {
        byte r = (byte)((pixel >> RedShift) & RedMax);
        byte g = (byte)((pixel >> GreenShift) & GreenMax);
        byte b = (byte)((pixel >> BlueShift) & BlueMax);

        // 非8位通道时归一化到0-255（四舍五入，避免 16bpp 等色彩系统性偏低）
        if (RedMax != 255 && RedMax != 0) r = (byte)((r * 255 + RedMax / 2) / RedMax);
        if (GreenMax != 255 && GreenMax != 0) g = (byte)((g * 255 + GreenMax / 2) / GreenMax);
        if (BlueMax != 255 && BlueMax != 0) b = (byte)((b * 255 + BlueMax / 2) / BlueMax);

        dest[offset] = b;
        dest[offset + 1] = g;
        dest[offset + 2] = r;
        dest[offset + 3] = 0xFF;
    }
}

/// <summary>
/// 表示帧缓冲更新中的矩形区域。
/// 描述需要更新的屏幕区域位置、大小和编码类型。
/// </summary>
public readonly record struct FramebufferRect(
    /// <summary>矩形左上角的X坐标。</summary>
    ushort X,

    /// <summary>矩形左上角的Y坐标。</summary>
    ushort Y,

    /// <summary>矩形的宽度（像素）。</summary>
    ushort Width,

    /// <summary>矩形的高度（像素）。</summary>
    ushort Height,

    /// <summary>编码类型标识符（如Raw=0, CopyRect=1, Hextile=5, ZRLE=16）。</summary>
    int EncodingType
);

/// <summary>
/// 包含VNC服务器初始化的完整信息。
/// 在RFB协议握手完成后由服务器发送给客户端。
/// </summary>
public class ServerInitInfo
{
    /// <summary>
    /// 帧缓冲区的宽度（像素）。
    /// </summary>
    public ushort FramebufferWidth { get; set; }

    /// <summary>
    /// 帧缓冲区的高度（像素）。
    /// </summary>
    public ushort FramebufferHeight { get; set; }

    /// <summary>
    /// 服务器使用的像素格式。
    /// </summary>
    public PixelFormat PixelFormat { get; set; }

    /// <summary>
    /// 远程桌面的名称。
    /// </summary>
    public string DesktopName { get; set; } = "";
}

/// <summary>
/// 表示从服务器接收的完整消息。
/// 包含消息类型和特定于消息类型的数据。
/// </summary>
public class ServerMessage
{
    /// <summary>
    /// 服务器消息类型。
    /// </summary>
    public ServerMessageType Type { get; set; }

    /// <summary>
    /// 帧缓冲更新中的矩形数量（仅适用于FramebufferUpdate消息）。
    /// </summary>
    public ushort RectCount { get; set; }

    /// <summary>
    /// 服务器剪贴板文本内容（仅适用于ServerCutText消息）。
    /// </summary>
    public string? Text { get; set; }
}

/// <summary>
/// RFB协议定义的服务器到客户端的消息类型。
/// </summary>
public enum ServerMessageType : byte
{
    /// <summary>
    /// 帧缓冲更新消息，包含一个或多个矩形区域的像素数据。
    /// </summary>
    FramebufferUpdate = 0,

    /// <summary>
    /// 设置颜色映射表项消息，用于调色板模式的像素格式。
    /// </summary>
    SetColorMapEntries = 1,

    /// <summary>
    /// 响铃消息，提示客户端播放提示音。
    /// </summary>
    Bell = 2,

    /// <summary>
    /// 服务器剪贴板文本消息，服务器向客户端发送剪贴板内容。
    /// </summary>
    ServerCutText = 3
}

/// <summary>
/// RFB协议定义的客户端到服务器的消息类型。
/// </summary>
public enum ClientMessageType : byte
{
    /// <summary>
    /// 设置像素格式消息。
    /// </summary>
    SetPixelFormat = 0,

    /// <summary>
    /// 设置编码方式消息，告知服务器客户端支持的编码类型。
    /// </summary>
    SetEncodings = 2,

    /// <summary>
    /// 请求帧缓冲更新消息。
    /// </summary>
    FramebufferUpdateRequest = 3,

    /// <summary>
    /// 按键事件消息，发送键盘按键的按下/释放状态。
    /// </summary>
    KeyEvent = 4,

    /// <summary>
    /// 鼠标指针事件消息，发送鼠标位置和按钮状态。
    /// </summary>
    PointerEvent = 5,

    /// <summary>
    /// 客户端剪贴板文本消息，客户端向服务器发送剪贴板内容。
    /// </summary>
    ClientCutText = 6
}

/// <summary>
/// 定义VNC RFB协议中使用的编码类型常量。
/// </summary>
public static class EncodingTypes
{
    /// <summary>原始像素编码，直接传输未压缩的像素数据。</summary>
    public const int Raw = 0;

    /// <summary>复制矩形编码，从帧缓冲区已有区域复制像素。</summary>
    public const int CopyRect = 1;

    /// <summary>矩形区域编码（RRE），使用背景色和前景色子矩形。</summary>
    public const int Rre = 2;

    /// <summary>十六进制瓦片编码（Hextile），将区域分割为16×16的瓦片进行编码。</summary>
    public const int Hextile = 5;

    /// <summary>ZRLE编码，使用Zlib压缩的多种子编码类型。</summary>
    public const int Zrle = 16;

    /// <summary>
    /// 光标伪编码(-239)。服务器借帧缓冲更新矩形推送鼠标光标的形状与热点：
    /// 矩形的 x/y 为热点坐标，w/h 为光标尺寸；随后是 w×h 像素数据 + 1bpp 透明掩码。
    /// 不修改帧缓冲，由客户端本地渲染光标——消除光标跟手延迟，并避免与服务器画入的光标重影。
    /// </summary>
    public const int Cursor = -239;
}
