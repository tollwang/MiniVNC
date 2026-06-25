using System.IO;
using System.Net.Sockets;
using MiniVNC.Network;
using MiniVNC.Protocol;
using MiniVNC.Encodings;
using MiniVNC.Utils;

namespace MiniVNC.Core;

/// <summary>
/// 帧缓冲更新事件参数 - 包含服务器发送的帧缓冲更新矩形区域信息。
/// </summary>
public sealed class FramebufferUpdateEventArgs : EventArgs
{
    /// <summary>更新的矩形区域列表。</summary>
    public List<FramebufferRect> UpdatedRects { get; } = new();

    /// <summary>创建空实例。</summary>
    public FramebufferUpdateEventArgs() { }

    /// <summary>创建实例并添加更新矩形。</summary>
    public FramebufferUpdateEventArgs(IEnumerable<FramebufferRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        UpdatedRects.AddRange(rects);
    }
}

/// <summary>
/// VNC客户端主控制器 - 管理连接、认证与消息循环，封装完整的 RFB 协议交互。
/// </summary>
public sealed class VncClient : IDisposable
{
    // ---- 事件 ----

    /// <summary>帧缓冲更新时触发。</summary>
    public event EventHandler<FramebufferUpdateEventArgs>? FramebufferUpdated;

    /// <summary>连接状态变化时触发。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>成功连接到服务器时触发。</summary>
    public event EventHandler? Connected;

    /// <summary>与服务器断开连接时触发。</summary>
    public event EventHandler? Disconnected;

    /// <summary>服务器剪贴板内容变化时触发。</summary>
    public event EventHandler<string>? ServerClipboardChanged;

    /// <summary>发生错误时触发。</summary>
    public event EventHandler<Exception>? ErrorOccurred;

    // ---- 属性 ----

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>帧缓冲宽度（像素）。</summary>
    public int FramebufferWidth { get; private set; }

    /// <summary>帧缓冲高度（像素）。</summary>
    public int FramebufferHeight { get; private set; }

    /// <summary>服务器桌面名称。</summary>
    public string ServerName { get; private set; } = string.Empty;

    /// <summary>当前像素格式（客户端协商后的目标格式）。</summary>
    public PixelFormat PixelFormat { get; private set; }

    /// <summary>当前帧缓冲（BGRA32）。</summary>
    public Framebuffer? Framebuffer { get; private set; }

    /// <summary>当前连接主机。</summary>
    public string? CurrentHost { get; private set; }

    /// <summary>当前连接端口。</summary>
    public int CurrentPort { get; private set; }

    /// <summary>最近一次使用的编码名称（用于状态栏显示）。</summary>
    public string CurrentEncoding { get; private set; } = "—";

    // ---- 私有字段 ----

    private VncStream? _stream;
    private RfbProtocol? _protocol;
    private CancellationTokenSource? _cts;
    private Task? _messageLoopTask;
    private readonly Dictionary<int, IEncoding> _encodings;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// 客户端消息写锁。会话期间 UI 线程（鼠标/键盘/剪贴板）与后台消息循环
    /// （增量更新请求）都会向网络流写入，必须串行化每条完整消息，避免字节交错。
    /// </summary>
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <summary>
    /// 创建 <see cref="VncClient"/>。
    /// 注册的解码器：Raw、Hextile（CopyRect 在消息循环中内联处理）。
    /// </summary>
    public VncClient()
    {
        _encodings = new Dictionary<int, IEncoding>
        {
            [EncodingTypes.Raw] = new RawEncoding(),
            [EncodingTypes.Hextile] = new HextileEncoding()
        };

        // 默认目标像素格式：32bpp 大端真彩，R/G/B 位移 16/8/0（与 BGRA 渲染兼容）
        PixelFormat = new PixelFormat(32, 24, true, true, 255, 255, 255, 16, 8, 0);
    }

    // ---- 公开方法 ----

    /// <summary>连接到 VNC 服务器。</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("主机名不能为空", nameof(host));
        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口号必须在1-65535之间");

        await _connectionLock.WaitAsync(ct);
        try
        {
            if (IsConnected)
                throw new InvalidOperationException("已经连接到服务器，请先断开");

            StatusChanged?.Invoke(this, $"正在连接 {host}:{port}...");

            // 建立 TCP 连接（真正异步、可取消）
            _stream = new VncStream();
            await _stream.ConnectAsync(host, port, ct);

            _protocol = new RfbProtocol(_stream);

            await PerformHandshakeAsync(ct);

            CurrentHost = host;
            CurrentPort = port;
            IsConnected = true;

            StatusChanged?.Invoke(this, "已连接");
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            _stream?.Dispose();
            _stream = null;
            _protocol = null;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>执行 VNC 认证。</summary>
    public async Task AuthenticateAsync(string password, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
            throw new InvalidOperationException("未连接到服务器");

        StatusChanged?.Invoke(this, "正在认证...");

        byte[] securityTypes = await _protocol.ReadSecurityTypesAsync(ct);
        if (securityTypes.Length == 0)
            throw new InvalidOperationException("服务器拒绝连接：未提供安全类型");

        if (securityTypes.Contains((byte)2)) // VNC 认证
        {
            await PerformVncAuthenticationAsync(password, ct);
        }
        else if (securityTypes.Contains((byte)1)) // 无认证
        {
            await PerformNoneAuthenticationAsync(ct);
        }
        else
        {
            throw new NotSupportedException(
                $"服务器不支持任何已知的安全类型。提供的类型: {string.Join(", ", securityTypes)}。" +
                "若目标为 macOS 屏幕共享，请在 Mac 上启用“VNC 显示器可使用密码控制屏幕”。");
        }

        StatusChanged?.Invoke(this, "认证成功");
    }

    /// <summary>初始化会话：发送客户端初始化并接收服务器初始化信息。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
            throw new InvalidOperationException("未连接到服务器");

        StatusChanged?.Invoke(this, "正在初始化会话...");

        // 共享桌面
        _protocol.WriteClientInit(true);

        var serverInit = await _protocol.ReadServerInitAsync(ct);
        FramebufferWidth = serverInit.FramebufferWidth;
        FramebufferHeight = serverInit.FramebufferHeight;
        ServerName = serverInit.DesktopName;

        if (FramebufferWidth <= 0 || FramebufferHeight <= 0)
            throw new InvalidOperationException($"服务器返回了非法的帧缓冲尺寸: {FramebufferWidth}x{FramebufferHeight}");

        // 协商目标像素格式：32bpp 大端真彩
        var preferredFormat = new PixelFormat(32, 24, true, true, 255, 255, 255, 16, 8, 0);
        _protocol.WriteSetPixelFormat(preferredFormat);
        PixelFormat = preferredFormat;

        // 创建 BGRA32 帧缓冲
        Framebuffer = new Framebuffer(FramebufferWidth, FramebufferHeight);

        // 协商编码优先级：Hextile > CopyRect > Raw（ZRLE 暂未启用）
        int[] preferredEncodings = new[] { EncodingTypes.Hextile, EncodingTypes.CopyRect, EncodingTypes.Raw };
        _protocol.WriteSetEncodings(preferredEncodings);

        StatusChanged?.Invoke(this, $"会话已初始化: {FramebufferWidth}x{FramebufferHeight} - {ServerName}");
    }

    /// <summary>启动帧缓冲更新接收循环（消息循环在后台任务中运行）。</summary>
    public void StartUpdateLoop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || Framebuffer == null)
            throw new InvalidOperationException("会话未初始化，请先调用InitializeAsync");

        // 取消之前的循环
        _cts?.Cancel();
        try { _messageLoopTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cts?.Dispose();

        _cts = new CancellationTokenSource();
        _messageLoopTask = Task.Run(() => MessageLoopAsync(_cts.Token));

        // 初始请求完整屏幕（非增量）
        RequestFramebufferUpdate(false, 0, 0, FramebufferWidth, FramebufferHeight);
    }

    /// <summary>发送鼠标/指针事件。</summary>
    public void SendPointerEvent(int x, int y, int buttonMask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_protocol == null || !IsConnected) return;

        x = Math.Clamp(x, 0, Math.Max(0, FramebufferWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, FramebufferHeight - 1));

        try { lock (_writeLock) { _protocol.WritePointerEvent((byte)buttonMask, (ushort)x, (ushort)y); } }
        catch (Exception ex) { ErrorOccurred?.Invoke(this, ex); }
    }

    /// <summary>发送键盘事件。</summary>
    public void SendKeyEvent(uint keysym, bool pressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_protocol == null || !IsConnected) return;

        try { lock (_writeLock) { _protocol.WriteKeyEvent(pressed, keysym); } }
        catch (Exception ex) { ErrorOccurred?.Invoke(this, ex); }
    }

    /// <summary>发送剪贴板文本到服务器。</summary>
    public void SendCutText(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        if (_protocol == null || !IsConnected) return;

        try { lock (_writeLock) { _protocol.WriteCutText(text); } }
        catch (Exception ex) { ErrorOccurred?.Invoke(this, ex); }
    }

    /// <summary>请求帧缓冲更新。</summary>
    public void RequestFramebufferUpdate(bool incremental, int x, int y, int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_protocol == null || !IsConnected) return; // 静默失败

        try
        {
            lock (_writeLock)
            {
                _protocol.WriteFramebufferUpdateRequest(
                    incremental, (ushort)x, (ushort)y, (ushort)w, (ushort)h);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>断开与服务器的连接（线程安全，可多次调用）。</summary>
    public void Disconnect()
    {
        if (!IsConnected && _stream == null) return;

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        try { _messageLoopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }

        try { _stream?.Dispose(); }
        catch (Exception) { }

        _stream = null;
        _protocol = null;
        IsConnected = false;

        Disconnected?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, "已断开连接");
    }

    /// <summary>释放所有资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;

        Disconnect();
        _cts?.Dispose();
        _connectionLock.Dispose();

        foreach (var encoding in _encodings.Values)
            (encoding as IDisposable)?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ---- 私有方法 ----

    /// <summary>RFB 握手 - 交换版本号。</summary>
    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        if (_protocol == null)
            throw new InvalidOperationException("协议处理器未初始化");

        StatusChanged?.Invoke(this, "正在握手...");

        string serverVersion = await _protocol.ReadVersionAsync(ct);
        StatusChanged?.Invoke(this, $"服务器版本: {serverVersion}");

        // 客户端使用 3.8
        _protocol.WriteVersion("RFB 003.008\n");
    }

    /// <summary>VNC 认证（安全类型2）。</summary>
    private async Task PerformVncAuthenticationAsync(string password, CancellationToken ct)
    {
        if (_protocol == null)
            throw new InvalidOperationException("协议处理器未初始化");

        _protocol.WriteSecurityType(2);

        byte[] challenge = await _protocol.ReadChallengeAsync(ct);
        byte[] response = DesEncryptor.Encrypt(challenge, password);
        _protocol.WriteChallengeResponse(response);

        uint result = await _protocol.ReadSecurityResultAsync(ct);
        if (result != 0)
        {
            string? errorMsg = await _protocol.ReadSecurityResultErrorAsync(ct);
            throw new InvalidOperationException(
                $"认证失败: {errorMsg ?? "密码错误或被拒绝"} (错误码: {result})");
        }
    }

    /// <summary>无认证（安全类型1）。</summary>
    private async Task PerformNoneAuthenticationAsync(CancellationToken ct)
    {
        if (_protocol == null)
            throw new InvalidOperationException("协议处理器未初始化");

        _protocol.WriteSecurityType(1);

        uint result = await _protocol.ReadSecurityResultAsync(ct);
        if (result != 0)
        {
            string? errorMsg = await _protocol.ReadSecurityResultErrorAsync(ct);
            throw new InvalidOperationException($"无认证模式被拒绝: {errorMsg ?? "未知错误"}");
        }

        StatusChanged?.Invoke(this, "警告: 使用无认证模式连接（不安全）");
    }

    /// <summary>服务器消息接收循环（后台线程）。</summary>
    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                if (_protocol == null) break;

                var messageType = await _protocol.ReadServerMessageTypeAsync(ct);
                bool needsUpdateRequest = false;

                switch (messageType)
                {
                    case ServerMessageType.FramebufferUpdate:
                        await HandleFramebufferUpdateAsync(ct);
                        needsUpdateRequest = true;
                        break;

                    case ServerMessageType.ServerCutText:
                        await HandleServerCutTextAsync(ct);
                        break;

                    case ServerMessageType.Bell:
                        HandleBell();
                        break;

                    case ServerMessageType.SetColorMapEntries:
                        // 真彩色模式下忽略颜色映射，但仍需读取并丢弃其数据以避免流错位
                        await SkipSetColorMapEntriesAsync(ct);
                        break;

                    default:
                        // 未知消息类型会导致流无法继续解析，断开以避免读到垃圾数据
                        throw new IOException($"收到未知的服务器消息类型: {(byte)messageType}");
                }

                if (needsUpdateRequest && IsConnected && _protocol != null)
                {
                    RequestFramebufferUpdate(true, 0, 0, FramebufferWidth, FramebufferHeight);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("未连接"))
        {
            StatusChanged?.Invoke(this, "连接已中断");
        }
        catch (IOException ex)
        {
            ErrorOccurred?.Invoke(this, new IOException("网络连接中断", ex));
        }
        catch (SocketException ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, "连接已断开");
            }
        }
    }

    /// <summary>处理帧缓冲更新消息。任何解码/协议错误都会向上抛出以触发断开，避免流错位。</summary>
    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        if (Framebuffer == null || _stream == null || _protocol == null) return;

        await _stream.ReadExactlyAsync(1, ct); // 填充字节
        ushort rectCount = await _stream.ReadUInt16Async(ct);
        var updatedRects = new List<FramebufferRect>(rectCount);

        for (int i = 0; i < rectCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            ushort x = await _stream.ReadUInt16Async(ct);
            ushort y = await _stream.ReadUInt16Async(ct);
            ushort w = await _stream.ReadUInt16Async(ct);
            ushort h = await _stream.ReadUInt16Async(ct);
            int encodingType = (int)await _stream.ReadUInt32Async(ct);

            // CopyRect：内联处理（读取源坐标后在帧缓冲内复制）
            if (encodingType == EncodingTypes.CopyRect)
            {
                byte[] coords = await _stream.ReadExactlyAsync(4, ct);
                ushort srcX = (ushort)((coords[0] << 8) | coords[1]);
                ushort srcY = (ushort)((coords[2] << 8) | coords[3]);
                if (w > 0 && h > 0)
                    Framebuffer.CopyRect(srcX, srcY, x, y, w, h);
                CurrentEncoding = "CopyRect";
                updatedRects.Add(new FramebufferRect(x, y, w, h, encodingType));
                continue;
            }

            if (!_encodings.TryGetValue(encodingType, out IEncoding? encoding))
                throw new NotSupportedException($"服务器发送了未协商的编码类型: {encodingType}");

            var rect = new FramebufferRect(x, y, w, h, encodingType);
            byte[] bgra = await encoding.DecodeAsync(_stream, rect, PixelFormat, ct);
            if (w > 0 && h > 0)
                Framebuffer.UpdateRectBgra32(x, y, w, h, bgra);

            CurrentEncoding = EncodingName(encodingType);
            updatedRects.Add(rect);
        }

        if (updatedRects.Count > 0)
            FramebufferUpdated?.Invoke(this, new FramebufferUpdateEventArgs(updatedRects));
    }

    /// <summary>处理服务器剪贴板文本消息。</summary>
    private async Task HandleServerCutTextAsync(CancellationToken ct)
    {
        if (_stream == null) return;

        await _stream.ReadExactlyAsync(3, ct); // 填充
        uint textLength = await _stream.ReadUInt32Async(ct);
        if (textLength > 16u * 1024 * 1024)
            throw new IOException($"服务器剪贴板长度异常: {textLength}");

        byte[] textBytes = textLength == 0 ? Array.Empty<byte>() : await _stream.ReadExactlyAsync((int)textLength, ct);
        string text = System.Text.Encoding.UTF8.GetString(textBytes);

        if (!string.IsNullOrEmpty(text))
            ServerClipboardChanged?.Invoke(this, text);
    }

    /// <summary>读取并丢弃 SetColorMapEntries 消息体（真彩色下不使用）。</summary>
    private async Task SkipSetColorMapEntriesAsync(CancellationToken ct)
    {
        if (_stream == null) return;

        await _stream.ReadExactlyAsync(1, ct); // 填充
        await _stream.ReadUInt16Async(ct);      // first color
        ushort numColors = await _stream.ReadUInt16Async(ct);
        if (numColors > 0)
            await _stream.ReadExactlyAsync(numColors * 6, ct); // 每个颜色 R/G/B 各2字节

        StatusChanged?.Invoke(this, "收到颜色映射更新（已忽略）");
    }

    /// <summary>响铃。</summary>
    private static void HandleBell()
    {
        try { Console.Beep(); }
        catch (Exception) { /* 某些环境不可用，忽略 */ }
    }

    /// <summary>编码类型 → 名称。</summary>
    private static string EncodingName(int type) => type switch
    {
        EncodingTypes.Raw => "Raw",
        EncodingTypes.CopyRect => "CopyRect",
        EncodingTypes.Hextile => "Hextile",
        EncodingTypes.Zrle => "ZRLE",
        _ => $"#{type}"
    };
}
