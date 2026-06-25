using System.IO.Compression;
using MiniVNC.Network;
using MiniVNC.Protocol;

namespace MiniVNC.Encodings;

/// <summary>
/// ZRLE（Zlib Run-Length Encoding）编码解码器。编码类型为16。
///
/// ZRLE是VNC协议中效率最高的编码之一，结合了Zlib压缩和多种子编码方式。
/// 它将矩形区域分割为64×64像素的瓦片（tile），每个瓦片使用不同的子编码类型：
///
/// 子编码类型：
/// 0: Raw               - 原始像素数据（经过Zlib压缩）
/// 1: Solid             - 单色填充（整个瓦片一种颜色）
/// 2-16: PackedPalette  - 调色板模式，2-16色，每个像素用索引表示
/// 127: Plain RLE       - 未压缩的RLE模式
/// 128-255: Palette RLE - 调色板RLE模式，结合调色板和RLE压缩
///
/// 整个瓦片数据先经过Zlib压缩，需要先解压缩再解析子编码。
/// </summary>
public class ZrleEncoding : IEncoding
{
    /// <summary>
    /// ZRLE编码的类型标识符，值为16。
    /// </summary>
    public int EncodingType => EncodingTypes.Zrle;

    // ZRLE子编码常量
    private const byte SubencodingRaw = 0;
    private const byte SubencodingSolid = 1;
    private const byte SubencodingPackedPaletteMin = 2;
    private const byte SubencodingPackedPaletteMax = 16;
    private const byte SubencodingPlainRle = 127;
    private const byte SubencodingPaletteRleMin = 128;
    private const byte SubencodingPaletteRleMax = 255;

    // Zlib解压缩流，在连接期间保持状态
    private ZLibStream? _zlibStream;
    private MemoryStream? _compressedStream;
    private MemoryStream? _decompressedStream;

    /// <summary>
    /// 从网络流中读取ZRLE编码数据并解码到帧缓冲区。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="framebuffer">要更新的帧缓冲区实例。</param>
    public void Decode(VncStream stream, FramebufferRect rect, Framebuffer framebuffer)
    {
        int bytesPerPixel = framebuffer.BytesPerPixel;

        // 读取压缩数据长度（4字节，大端序）
        uint compressedLength = stream.ReadUInt32();

        // 读取压缩数据
        byte[] compressedData = stream.ReadExactly((int)compressedLength);

        // 解压缩数据
        byte[] tileData = DecompressZlib(compressedData);

        using MemoryStream tileStream = new MemoryStream(tileData);
        using BinaryReader reader = new BinaryReader(tileStream);

        // 按64×64瓦片迭代
        for (int tileY = rect.Y; tileY < rect.Y + rect.Height; tileY += 64)
        {
            for (int tileX = rect.X; tileX < rect.X + rect.Width; tileX += 64)
            {
                int tileW = Math.Min(64, rect.X + rect.Width - tileX);
                int tileH = Math.Min(64, rect.Y + rect.Height - tileY);

                DecodeTile(reader, framebuffer, tileX, tileY, tileW, tileH, bytesPerPixel);
            }
        }
    }

    /// <summary>
    /// 解码单个瓦片的数据。
    /// </summary>
    private static void DecodeTile(BinaryReader reader, Framebuffer framebuffer,
        int tileX, int tileY, int tileW, int tileH, int bytesPerPixel)
    {
        byte subencoding = reader.ReadByte();

        // 0: Raw - 读取原始像素数据
        if (subencoding == SubencodingRaw)
        {
            int dataSize = tileW * tileH * bytesPerPixel;
            byte[] pixelData = reader.ReadBytes(dataSize);
            framebuffer.UpdateRect(tileX, tileY, tileW, tileH, pixelData);
            return;
        }

        // 1: Solid - 单色填充
        if (subencoding == SubencodingSolid)
        {
            uint color = ReadPixel(reader, bytesPerPixel);
            FillRect(framebuffer, tileX, tileY, tileW, tileH, color);
            return;
        }

        // 2-16: PackedPalette - 调色板模式
        if (subencoding >= SubencodingPackedPaletteMin && subencoding <= SubencodingPackedPaletteMax)
        {
            int paletteSize = subencoding; // 调色板颜色数
            DecodePackedPalette(reader, framebuffer, tileX, tileY, tileW, tileH, bytesPerPixel, paletteSize);
            return;
        }

        // 127: Plain RLE - 未压缩RLE
        if (subencoding == SubencodingPlainRle)
        {
            DecodePlainRle(reader, framebuffer, tileX, tileY, tileW, tileH, bytesPerPixel);
            return;
        }

        // 128-255: Palette RLE - 调色板RLE
        if (subencoding >= SubencodingPaletteRleMin && subencoding <= SubencodingPaletteRleMax)
        {
            int paletteSize = subencoding - 128; // 调色板颜色数
            DecodePaletteRle(reader, framebuffer, tileX, tileY, tileW, tileH, bytesPerPixel, paletteSize);
            return;
        }

        throw new NotSupportedException($"Unsupported ZRLE subencoding: {subencoding}");
    }

    /// <summary>
    /// 解码PackedPalette子编码的瓦片。
    /// 调色板颜色数为2-16，每个像素用log2(paletteSize)位表示索引。
    /// </summary>
    private static void DecodePackedPalette(BinaryReader reader, Framebuffer framebuffer,
        int tileX, int tileY, int tileW, int tileH, int bytesPerPixel, int paletteSize)
    {
        // 读取调色板
        uint[] palette = new uint[paletteSize];
        for (int i = 0; i < paletteSize; i++)
        {
            palette[i] = ReadPixel(reader, bytesPerPixel);
        }

        // 计算每个像素需要的位数
        int bitsPerIndex = paletteSize switch
        {
            2 => 1,
            3 or 4 => 2,
            5 or 6 or 7 or 8 => 3,
            9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 => 4,
            _ => throw new NotSupportedException($"Unsupported palette size: {paletteSize}")
        };

        // 读取打包的像素索引数据
        int totalPixels = tileW * tileH;
        int packedBytes = (totalPixels * bitsPerIndex + 7) / 8;
        byte[] packedData = reader.ReadBytes(packedBytes);

        // 解压像素索引并填充
        int bitPos = 0;
        for (int y = 0; y < tileH; y++)
        {
            for (int x = 0; x < tileW; x++)
            {
                int byteIndex = bitPos / 8;
                int bitOffset = 7 - (bitPos % 8); // 高位在前
                int index = 0;

                for (int b = 0; b < bitsPerIndex; b++)
                {
                    int currentBit = (packedData[byteIndex + (bitOffset - b) / 8] >> ((bitOffset - b) % 8)) & 1;
                    index = (index << 1) | currentBit;
                }

                bitPos += bitsPerIndex;

                if (index < paletteSize)
                {
                    framebuffer.WritePixel(tileX + x, tileY + y, palette[index]);
                }
            }
        }
    }

    /// <summary>
    /// 解码Plain RLE子编码的瓦片。
    /// 使用游程长度编码，相同颜色的连续像素被编码为（颜色，长度）对。
    /// </summary>
    private static void DecodePlainRle(BinaryReader reader, Framebuffer framebuffer,
        int tileX, int tileY, int tileW, int tileH, int bytesPerPixel)
    {
        int totalPixels = tileW * tileH;
        int pixelsWritten = 0;

        while (pixelsWritten < totalPixels)
        {
            uint color = ReadPixel(reader, bytesPerPixel);
            int runLength = ReadRunLength(reader);

            for (int i = 0; i < runLength && pixelsWritten < totalPixels; i++)
            {
                int x = pixelsWritten % tileW;
                int y = pixelsWritten / tileW;
                framebuffer.WritePixel(tileX + x, tileY + y, color);
                pixelsWritten++;
            }
        }
    }

    /// <summary>
    /// 解码Palette RLE子编码的瓦片。
    /// 先读取调色板，然后使用RLE编码的调色板索引。
    /// </summary>
    private static void DecodePaletteRle(BinaryReader reader, Framebuffer framebuffer,
        int tileX, int tileY, int tileW, int tileH, int bytesPerPixel, int paletteSize)
    {
        // 读取调色板
        uint[] palette = new uint[paletteSize];
        for (int i = 0; i < paletteSize; i++)
        {
            palette[i] = ReadPixel(reader, bytesPerPixel);
        }

        int totalPixels = tileW * tileH;
        int pixelsWritten = 0;

        while (pixelsWritten < totalPixels)
        {
            byte indexByte = reader.ReadByte();

            if ((indexByte & 0x80) != 0)
            {
                // 高位置1：RLE模式，索引 = indexByte & 0x7F
                int index = indexByte & 0x7F;
                int runLength = ReadRunLength(reader);

                for (int i = 0; i < runLength && pixelsWritten < totalPixels; i++)
                {
                    int x = pixelsWritten % tileW;
                    int y = pixelsWritten / tileW;
                    framebuffer.WritePixel(tileX + x, tileY + y, palette[index]);
                    pixelsWritten++;
                }
            }
            else
            {
                // 高位置0：单个像素
                int index = indexByte;
                int x = pixelsWritten % tileW;
                int y = pixelsWritten / tileW;
                framebuffer.WritePixel(tileX + x, tileY + y, palette[index]);
                pixelsWritten++;
            }
        }
    }

    /// <summary>
    /// 从BinaryReader读取RLE长度值。
    /// 长度编码：1-255直接使用该值，>=256时使用3字节（0xFF + 2字节大端序长度）。
    /// </summary>
    private static int ReadRunLength(BinaryReader reader)
    {
        int length = reader.ReadByte();
        if (length == 0xFF)
        {
            length = (reader.ReadByte() << 8) | reader.ReadByte();
        }
        return length;
    }

    /// <summary>
    /// 从BinaryReader读取一个像素值（大端序）。
    /// </summary>
    private static uint ReadPixel(BinaryReader reader, int bytesPerPixel)
    {
        byte[] bytes = reader.ReadBytes(bytesPerPixel);
        return bytesPerPixel switch
        {
            4 => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3],
            3 => ((uint)bytes[0] << 16) | ((uint)bytes[1] << 8) | bytes[2],
            2 => (uint)((bytes[0] << 8) | bytes[1]),
            1 => bytes[0],
            _ => throw new NotSupportedException($"Unsupported bytes per pixel: {bytesPerPixel}")
        };
    }

    /// <summary>
    /// 用指定像素值填充帧缓冲区中的矩形区域。
    /// </summary>
    private static void FillRect(Framebuffer framebuffer, int x, int y, int w, int h, uint pixel)
    {
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                framebuffer.WritePixel(x + col, y + row, pixel);
            }
        }
    }

    /// <summary>
    /// 使用Zlib解压缩数据。
    /// </summary>
    /// <param name="compressedData">Zlib压缩的字节数据。</param>
    /// <returns>解压缩后的字节数据。</returns>
    private static byte[] DecompressZlib(byte[] compressedData)
    {
        using MemoryStream inputStream = new MemoryStream(compressedData);
        using MemoryStream outputStream = new MemoryStream();
        using (ZLibStream zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress))
        {
            zlibStream.CopyTo(outputStream);
        }
        return outputStream.ToArray();
    }

    /// <summary>
    /// 异步从网络流中读取ZRLE编码数据。
    /// 使用临时帧缓冲区进行同步解码，然后返回像素数据。
    /// </summary>
    /// <param name="stream">VNC网络流，用于读取编码数据。</param>
    /// <param name="rect">描述需要更新的帧缓冲区域的位置和大小。</param>
    /// <param name="pixelFormat">当前使用的像素格式。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>解码后的像素数据字节数组。</returns>
    public Task<byte[]> DecodeAsync(VncStream stream, FramebufferRect rect, PixelFormat pixelFormat, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var tempFramebuffer = new Framebuffer(rect.Width, rect.Height, pixelFormat);
            var adjustedRect = rect with { X = 0, Y = 0 };
            Decode(stream, adjustedRect, tempFramebuffer);
            return tempFramebuffer.Pixels;
        }, ct);
    }
}
