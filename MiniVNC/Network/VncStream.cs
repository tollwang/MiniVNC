using System.IO;
using System.Net.Sockets;

namespace MiniVNC.Network;

/// <summary>
/// VNC TCP连接流，封装 <see cref="NetworkStream"/> 提供大端序读写能力。
/// 所有协议数据均按RFB规范使用网络字节序（大端序）进行传输。
/// </summary>
public class VncStream : IDisposable
{
    private readonly TcpClient _tcpClient;
    private NetworkStream _stream;
    private readonly byte[] _buffer;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VncStream"/> 实例，不立即建立连接。
    /// 调用 <see cref="ConnectAsync"/> 方法来建立连接。
    /// </summary>
    public VncStream()
    {
        _tcpClient = new TcpClient();
        _buffer = new byte[65536];
        _stream = null!;
    }

    /// <summary>
    /// 初始化 <see cref="VncStream"/> 实例并建立到指定主机和端口的TCP连接。
    /// </summary>
    /// <param name="host">VNC服务器主机名或IP地址。</param>
    /// <param name="port">VNC服务器端口号，通常为5900。</param>
    /// <exception cref="SocketException">连接失败时抛出。</exception>
    public VncStream(string host, int port)
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(host, port);
        _stream = _tcpClient.GetStream();
        _buffer = new byte[65536];
    }

    /// <summary>
    /// 异步建立到指定主机和端口的TCP连接。
    /// </summary>
    /// <param name="host">VNC服务器主机名或IP地址。</param>
    /// <param name="port">VNC服务器端口号，通常为5900。</param>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <exception cref="SocketException">连接失败时抛出。</exception>
    /// <exception cref="OperationCanceledException">操作被取消时抛出。</exception>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await _tcpClient.ConnectAsync(host, port, ct);
        _stream = _tcpClient.GetStream();
    }

    /// <summary>
    /// 获取底层的 <see cref="NetworkStream"/> 实例。
    /// </summary>
    public Stream BaseStream => _stream;

    #region 同步读写

    /// <summary>
    /// 将字节数组完整写入流中。
    /// </summary>
    /// <param name="data">要写入的字节数据。</param>
    public void Write(byte[] data) => _stream.Write(data, 0, data.Length);

    /// <summary>
    /// 将单个字节写入流中。
    /// </summary>
    /// <param name="value">要写入的字节值。</param>
    public void WriteByte(byte value) => _stream.WriteByte(value);

    /// <summary>
    /// 从流中读取指定数量的字节到缓冲区。
    /// </summary>
    /// <param name="buffer">目标字节缓冲区。</param>
    /// <param name="offset">缓冲区中的起始偏移量。</param>
    /// <param name="count">要读取的最大字节数。</param>
    /// <returns>实际读取到的字节数。</returns>
    public int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    /// <summary>
    /// 从流中精确读取指定数量的字节，循环读取直到缓冲区填满。
    /// </summary>
    /// <param name="count">需要读取的精确字节数。</param>
    /// <returns>包含精确 <paramref name="count"/> 个字节的数组。</returns>
    /// <exception cref="IOException">流在读取完成前关闭时抛出。</exception>
    public byte[] ReadExactly(int count)
    {
        byte[] result = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _stream.Read(result, totalRead, count - totalRead);
            if (read == 0)
                throw new IOException($"Expected {count} bytes but only received {totalRead} before stream closed.");
            totalRead += read;
        }
        return result;
    }

    #endregion

    #region 大端序读取

    /// <summary>
    /// 从流中读取2字节的无符号短整型（大端序）。
    /// </summary>
    /// <returns>以大端序解析的 <see cref="ushort"/> 值。</returns>
    public ushort ReadUInt16()
    {
        byte[] b = ReadExactly(2);
        return (ushort)((b[0] << 8) | b[1]);
    }

    /// <summary>
    /// 从流中读取4字节的无符号整型（大端序）。
    /// </summary>
    /// <returns>以大端序解析的 <see cref="uint"/> 值。</returns>
    public uint ReadUInt32()
    {
        byte[] b = ReadExactly(4);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>
    /// 从流中读取单个字节。
    /// </summary>
    /// <returns>读取到的字节值（0-255）。</returns>
    /// <exception cref="IOException">流已关闭时抛出。</exception>
    public byte ReadByte()
    {
        int value = _stream.ReadByte();
        if (value == -1)
            throw new IOException("End of stream reached when reading a byte.");
        return (byte)value;
    }

    #endregion

    #region 大端序写入

    /// <summary>
    /// 将无符号短整型以2字节大端序格式写入流。
    /// </summary>
    /// <param name="value">要写入的 <see cref="ushort"/> 值。</param>
    public void WriteUInt16(ushort value)
    {
        Write(new[] { (byte)(value >> 8), (byte)value });
    }

    /// <summary>
    /// 将无符号整型以4字节大端序格式写入流。
    /// </summary>
    /// <param name="value">要写入的 <see cref="uint"/> 值。</param>
    public void WriteUInt32(uint value)
    {
        Write(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }

    #endregion

    #region 异步读写

    /// <summary>
    /// 异步从流中读取单个字节。
    /// </summary>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>读取到的字节值（0-255）。</returns>
    /// <exception cref="IOException">流在读取完成前关闭时抛出。</exception>
    public async Task<byte> ReadByteAsync(CancellationToken ct = default)
    {
        byte[] b = await ReadExactlyAsync(1, ct);
        return b[0];
    }

    /// <summary>
    /// 异步将字节数组完整写入流中。
    /// </summary>
    /// <param name="data">要写入的字节数据。</param>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>表示异步操作完成的任务。</returns>
    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
        => await _stream.WriteAsync(data.AsMemory(0, data.Length), ct);

    /// <summary>
    /// 异步从流中精确读取指定数量的字节，循环读取直到缓冲区填满。
    /// </summary>
    /// <param name="count">需要读取的精确字节数。</param>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>包含精确 <paramref name="count"/> 个字节的数组。</returns>
    /// <exception cref="IOException">流在读取完成前关闭时抛出。</exception>
    /// <exception cref="OperationCanceledException">操作被取消时抛出。</exception>
    public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct = default)
    {
        byte[] result = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await _stream.ReadAsync(result.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0)
                throw new IOException($"Expected {count} bytes but only received {totalRead} before stream closed.");
            totalRead += read;
        }
        return result;
    }

    /// <summary>
    /// 异步从流中读取2字节的无符号短整型（大端序）。
    /// </summary>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>以大端序解析的 <see cref="ushort"/> 值。</returns>
    public async Task<ushort> ReadUInt16Async(CancellationToken ct = default)
    {
        byte[] b = await ReadExactlyAsync(2, ct);
        return (ushort)((b[0] << 8) | b[1]);
    }

    /// <summary>
    /// 异步从流中读取4字节的无符号整型（大端序）。
    /// </summary>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>以大端序解析的 <see cref="uint"/> 值。</returns>
    public async Task<uint> ReadUInt32Async(CancellationToken ct = default)
    {
        byte[] b = await ReadExactlyAsync(4, ct);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>
    /// 异步将无符号短整型以2字节大端序格式写入流。
    /// </summary>
    /// <param name="value">要写入的 <see cref="ushort"/> 值。</param>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>表示异步操作完成的任务。</returns>
    public async Task WriteUInt16Async(ushort value, CancellationToken ct = default)
    {
        await WriteAsync(new[] { (byte)(value >> 8), (byte)value }, ct);
    }

    /// <summary>
    /// 异步将无符号整型以4字节大端序格式写入流。
    /// </summary>
    /// <param name="value">要写入的 <see cref="uint"/> 值。</param>
    /// <param name="ct">取消令牌，用于取消异步操作。</param>
    /// <returns>表示异步操作完成的任务。</returns>
    public async Task WriteUInt32Async(uint value, CancellationToken ct = default)
    {
        await WriteAsync(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        }, ct);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// 释放 <see cref="VncStream"/> 使用的所有资源，包括关闭网络流和TCP连接。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Close();
        _tcpClient.Close();
    }

    #endregion
}
