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
    /// <summary>
    /// 更新的矩形区域列表。
    /// </summary>
    public List<FramebufferRect> UpdatedRects { get; } = new();

    /// <summary>
    /// 创建 <see cref="FramebufferUpdateEventArgs"/> 的新实例。
    /// </summary>
    public FramebufferUpdateEventArgs() { }

    /// <summary>
    /// 创建 <see cref="FramebufferUpdateEventArgs"/> 的新实例并添加更新矩形。
    /// </summary>
    /// <param name="rects">更新矩形列表</param>
    /// <exception cref="ArgumentNullException"><paramref name="rects"/>为null</exception>
    public FramebufferUpdateEventArgs(IEnumerable<FramebufferRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        UpdatedRects.AddRange(rects);
    }
}

/// <summary>
/// VNC客户端主控制器 - 管理连接、认证、消息循环。
/// 这是整个VNC客户端应用的核心类，封装了完整的RFB协议交互。
/// </summary>
/// <remarks>
/// 使用示例：
/// <code>
/// var client = new VncClient();
/// client.FramebufferUpdated += (s, e) => { /* 处理帧缓冲更新 */ };
/// client.StatusChanged += (s, msg) => { /* 处理状态变化 */ };
/// await client.ConnectAsync("192.168.1.100", 5900);
/// await client.AuthenticateAsync("password");
/// await client.InitializeAsync();
/// client.StartUpdateLoop();
/// </code>
/// </remarks>
public sealed class VncClient : IDisposable
{
    // ---- 事件定义 ----

    /// <summary>
    /// 帧缓冲更新时触发。
    /// </summary>
    public event EventHandler<FramebufferUpdateEventArgs>? FramebufferUpdated;

    /// <summary>
    /// 连接状态变化时触发。
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 成功连接到服务器时触发。
    /// </summary>
    public event EventHandler? Connected;

    /// <summary>
    /// 与服务器断开连接时触发。
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// 服务器剪贴板内容变化时触发。
    /// </summary>
    public event EventHandler<string>? ServerClipboardChanged;

    /// <summary>
    /// 发生错误时触发。
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;

    // ---- 公开属性 ----

    /// <summary>
    /// 当前是否已连接到服务器。
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// 帧缓冲宽度（像素）。在 <see cref="InitializeAsync"/> 后可用。
    /// </summary>
    public int FramebufferWidth { get; private set; }

    /// <summary>
    /// 帧缓冲高度（像素）。在 <see cref="InitializeAsync"/> 后可用。
    /// </summary>
    public int FramebufferHeight { get; private set; }

    /// <summary>
    /// 服务器桌面名称。在 <see cref="InitializeAsync"/> 后可用。
    /// </summary>
    public string ServerName { get; private set; } = string.Empty;

    /// <summary>
    /// 当前像素格式。
    /// </summary>
    public PixelFormatInfo PixelFormat { get; private set; }

    /// <summary>
    /// 当前帧缓冲。在 <see cref="InitializeAsync"/> 后可用。
    /// </summary>
    public Framebuffer? Framebuffer { get; private set; }

    /// <summary>
    /// 当前连接的主机名。
    /// </summary>
    public string? CurrentHost { get; private set; }

    /// <summary>
    /// 当前连接的端口号。
    /// </summary>
    public int CurrentPort { get; private set; }

    // ---- 私有字段 ----

    /// <summary>
    /// 网络流封装。
    /// </summary>
    private VncStream? _stream;

    /// <summary>
    /// RFB协议处理器。
    /// </summary>
    private RfbProtocol? _protocol;

    /// <summary>
    /// 消息循环取消令牌源。
    /// </summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 消息循环任务。
    /// </summary>
    private Task? _messageLoopTask;

    /// <summary>
    /// 支持的编码处理器映射。
    /// </summary>
    private readonly Dictionary<int, IEncoding> _encodings;

    /// <summary>
    /// 线程同步锁。
    /// </summary>
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// 是否已释放。
    /// </summary>
    private bool _disposed;

    // ---- 构造函数 ----

    /// <summary>
    /// 创建 <see cref="VncClient"/> 的新实例。
    /// </summary>
    public VncClient()
    {
        _encodings = new Dictionary<int, IEncoding>
        {
            [0] = new RawEncoding(),
            [1] = new CopyRectEncoding(),
            [5] = new HextileEncoding(),
            [16] = new ZrleEncoding()
        };

        PixelFormat = new PixelFormatInfo(32, 24, true, true, 255, 255, 255, 16, 8, 0);
    }

    // ---- 公开方法 ----

    /// <summary>
    /// 连接到VNC服务器。
    /// </summary>
    /// <param name="host">服务器主机名或IP地址</param>
    /// <param name="port">服务器端口号（默认5900）</param>
    /// <param name="ct">取消令牌</param>
    /// <exception cref="ArgumentException"><paramref name="host"/>为空或空白</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/>不在有效范围内</exception>
    /// <exception cref="InvalidOperationException">已经处于连接状态</exception>
    /// <exception cref="SocketException">网络连接失败</exception>
    /// <exception cref="OperationCanceledException">操作被取消</exception>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("主机名不能为空", nameof(host));
        }

        if (port <= 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口号必须在1-65535之间");
        }

        await _connectionLock.WaitAsync(ct);

        try
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("已经连接到服务器，请先断开");
            }

            StatusChanged?.Invoke(this, $"正在连接 {host}:{port}...");

            // 建立TCP连接
            _stream = new VncStream(host, port);
            await _stream.ConnectAsync(ct);

            _protocol = new RfbProtocol(_stream);

            // RFB协议握手
            await PerformHandshakeAsync(ct);

            CurrentHost = host;
            CurrentPort = port;
            IsConnected = true;

            StatusChanged?.Invoke(this, "已连接");
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 清理失败的连接
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

    /// <summary>
    /// 执行VNC认证。
    /// </summary>
    /// <param name="password">VNC连接密码</param>
    /// <param name="ct">取消令牌</param>
    /// <exception cref="InvalidOperationException">未连接到服务器</exception>
    /// <exception cref="InvalidOperationException">认证失败</exception>
    /// <exception cref="NotSupportedException">服务器不支持的安全类型</exception>
    /// <exception cref="OperationCanceledException">操作被取消</exception>
    public async Task AuthenticateAsync(string password, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
        {
            throw new InvalidOperationException("未连接到服务器");
        }

        StatusChanged?.Invoke(this, "正在认证...");

        // 读取安全类型列表
        byte[] securityTypes = await _protocol.ReadSecurityTypesAsync(ct);

        if (securityTypes.Length == 0)
        {
            throw new InvalidOperationException("服务器拒绝连接：未提供安全类型");
        }

        // 优先选择VNC认证（类型2）
        if (securityTypes.Contains((byte)2))
        {
            await PerformVncAuthenticationAsync(password, ct);
        }
        else if (securityTypes.Contains((byte)1))
        {
            // 无认证（不安全，仅用于测试环境）
            await PerformNoneAuthenticationAsync(ct);
        }
        else
        {
            throw new NotSupportedException(
                $"服务器不支持任何已知的安全类型。提供的类型: {string.Join(", ", securityTypes)}");
        }

        StatusChanged?.Invoke(this, "认证成功");
    }

    /// <summary>
    /// 初始化会话 - 发送客户端初始化并接收服务器初始化信息。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <exception cref="InvalidOperationException">未连接到服务器</exception>
    /// <exception cref="OperationCanceledException">操作被取消</exception>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
        {
            throw new InvalidOperationException("未连接到服务器");
        }

        StatusChanged?.Invoke(this, "正在初始化会话...");

        // 发送共享标志（true = 共享桌面，允许多个客户端）
        _protocol.WriteClientInit(true);

        // 读取服务器初始化信息
        var serverInit = await _protocol.ReadServerInitAsync(ct);
        FramebufferWidth = serverInit.FramebufferWidth;
        FramebufferHeight = serverInit.FramebufferHeight;
        PixelFormat = serverInit.PixelFormat;
        ServerName = serverInit.DesktopName;

        // 创建帧缓冲
        Framebuffer = new Framebuffer(FramebufferWidth, FramebufferHeight, PixelFormat);

        // 设置像素格式为大端序32bpp（WPF兼容格式）
        var preferredFormat = new PixelFormatInfo(32, 24, true, true, 255, 255, 255, 16, 8, 0);
        _protocol.WriteSetPixelFormat(preferredFormat);
        PixelFormat = preferredFormat;

        // 设置编码优先级：ZRLE > Hextile > CopyRect > Raw
        int[] preferredEncodings = new[] { 16, 5, 1, 0 };
        _protocol.WriteSetEncodings(preferredEncodings);

        StatusChanged?.Invoke(this, $"会话已初始化: {FramebufferWidth}x{FramebufferHeight} - {ServerName}");
    }

    /// <summary>
    /// 启动帧缓冲更新接收循环。
    /// 此方法是同步返回的，实际的消息循环在后台任务中运行。
    /// </summary>
    /// <exception cref="InvalidOperationException">未初始化会话</exception>
    public void StartUpdateLoop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || Framebuffer == null)
        {
            throw new InvalidOperationException("会话未初始化，请先调用InitializeAsync");
        }

        // 取消之前的循环（如果有）
        _cts?.Cancel();
        _messageLoopTask?.Wait(TimeSpan.FromSeconds(5));
        _cts?.Dispose();

        _cts = new CancellationTokenSource();
        _messageLoopTask = Task.Run(() => MessageLoopAsync(_cts.Token));

        // 发送初始帧缓冲更新请求（非增量，获取完整屏幕）
        RequestFramebufferUpdate(false, 0, 0, FramebufferWidth, FramebufferHeight);
    }

    /// <summary>
    /// 发送鼠标/指针事件。
    /// </summary>
    /// <param name="x">鼠标X坐标</param>
    /// <param name="y">鼠标Y坐标</param>
    /// <param name="buttonMask">按钮掩码（位0=左键，位1=中键，位2=右键）</param>
    /// <exception cref="InvalidOperationException">未连接到服务器</exception>
    public void SendPointerEvent(int x, int y, int buttonMask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
        {
            throw new InvalidOperationException("未连接到服务器");
        }

        // 确保坐标在有效范围内
        x = Math.Clamp(x, 0, FramebufferWidth - 1);
        y = Math.Clamp(y, 0, FramebufferHeight - 1);

        _protocol.WritePointerEvent((byte)buttonMask, (ushort)x, (ushort)y);
    }

    /// <summary>
    /// 发送键盘事件。
    /// </summary>
    /// <param name="keysym">X11键符号（Keysym）</param>
    /// <param name="pressed">true为按下，false为释放</param>
    /// <exception cref="InvalidOperationException">未连接到服务器</exception>
    public void SendKeyEvent(uint keysym, bool pressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
        {
            throw new InvalidOperationException("未连接到服务器");
        }

        _protocol.WriteKeyEvent(pressed, keysym);
    }

    /// <summary>
    /// 发送剪贴板文本到服务器。
    /// </summary>
    /// <param name="text">要发送的文本</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>为null</exception>
    /// <exception cref="InvalidOperationException">未连接到服务器</exception>
    public void SendCutText(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        if (_protocol == null || !IsConnected)
        {
            throw new InvalidOperationException("未连接到服务器");
        }

        _protocol.WriteCutText(text);
    }

    /// <summary>
    /// 请求帧缓冲更新。
    /// </summary>
    /// <param name="incremental">true为增量更新（仅变化区域），false为完整更新</param>
    /// <param name="x">更新区域左上角X坐标</param>
    /// <param name="y">更新区域左上角Y坐标</param>
    /// <param name="w">更新区域宽度</param>
    /// <param name="h">更新区域高度</param>
    /// <exception cref="InvalidOperationException">未连接到服务器</exception>
    public void RequestFramebufferUpdate(bool incremental, int x, int y, int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
        {
            return; // 静默失败，因为更新请求可能在断开时排队
        }

        _protocol.WriteFramebufferUpdateRequest(
            incremental,
            (ushort)x,
            (ushort)y,
            (ushort)w,
            (ushort)h);
    }

    /// <summary>
    /// 断开与VNC服务器的连接。
    /// 此方法线程安全，可多次调用。
    /// </summary>
    public void Disconnect()
    {
        if (!IsConnected && _stream == null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略已释放的CTS
        }

        try
        {
            _messageLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 忽略任务取消异常
        }

        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // 忽略断开时的网络错误
        }

        _stream = null;
        _protocol = null;
        IsConnected = false;

        Disconnected?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, "已断开连接");
    }

    /// <summary>
    /// 释放所有资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect();

        _cts?.Dispose();
        _connectionLock.Dispose();

        foreach (var encoding in _encodings.Values)
        {
            (encoding as IDisposable)?.Dispose();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ---- 私有方法 ----

    /// <summary>
    /// 执行RFB协议握手 - 交换版本号。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        if (_protocol == null)
        {
            throw new InvalidOperationException("协议处理器未初始化");
        }

        StatusChanged?.Invoke(this, "正在握手...");

        // 读取服务器版本
        string serverVersion = await _protocol.ReadVersionAsync(ct);
        StatusChanged?.Invoke(this, $"服务器版本: {serverVersion}");

        // 发送客户端版本（3.8）
        _protocol.WriteVersion("RFB 003.008\n");
    }

    /// <summary>
    /// 执行VNC认证（安全类型2）。
    /// </summary>
    /// <param name="password">VNC密码</param>
    /// <param name="ct">取消令牌</param>
    private async Task PerformVncAuthenticationAsync(string password, CancellationToken ct)
    {
        if (_protocol == null)
        {
            throw new InvalidOperationException("协议处理器未初始化");
        }

        // 选择VNC认证
        _protocol.WriteSecurityType(2);

        // 读取16字节challenge
        byte[] challenge = await _protocol.ReadChallengeAsync(ct);

        // 使用密码加密challenge
        byte[] response = DesEncryptor.Encrypt(challenge, password);

        // 发送加密后的响应
        _protocol.WriteChallengeResponse(response);

        // 读取认证结果
        uint result = await _protocol.ReadSecurityResultAsync(ct);

        if (result != 0)
        {
            // 认证失败，读取错误消息
            string? errorMsg = await _protocol.ReadSecurityResultErrorAsync(ct);
            throw new InvalidOperationException(
                $"认证失败: {errorMsg ?? "未知错误"} (错误码: {result})");
        }
    }

    /// <summary>
    /// 执行无认证（安全类型1，不安全，仅测试使用）。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    private async Task PerformNoneAuthenticationAsync(CancellationToken ct)
    {
        if (_protocol == null)
        {
            throw new InvalidOperationException("协议处理器未初始化");
        }

        _protocol.WriteSecurityType(1);

        // 读取安全结果（即使是None也可能有结果消息）
        uint result = await _protocol.ReadSecurityResultAsync(ct);

        if (result != 0)
        {
            string? errorMsg = await _protocol.ReadSecurityResultErrorAsync(ct);
            throw new InvalidOperationException(
                $"无认证模式也被拒绝: {errorMsg ?? "未知错误"}");
        }

        StatusChanged?.Invoke(this, "警告: 使用无认证模式连接（不安全）");
    }

    /// <summary>
    /// 服务器消息接收循环。
    /// 在后台线程中持续运行，直到连接断开或取消。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                if (_protocol == null)
                {
                    break;
                }

                // 读取服务器消息类型
                var messageType = await _protocol.ReadServerMessageTypeAsync(ct);

                // 在消息处理后请求下一帧更新
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
                        // 颜色映射通常在真彩色模式下不需要处理
                        StatusChanged?.Invoke(this, "收到颜色映射更新（已忽略）");
                        break;

                    default:
                        StatusChanged?.Invoke(this, $"收到未知消息类型: {(byte)messageType}");
                        break;
                }

                // 请求下一帧增量更新
                if (needsUpdateRequest && IsConnected && _protocol != null)
                {
                    RequestFramebufferUpdate(
                        true, 0, 0, FramebufferWidth, FramebufferHeight);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("未连接"))
        {
            // 连接已断开，优雅处理
            StatusChanged?.Invoke(this, "连接已中断");
        }
        catch (IOException ex)
        {
            ErrorOccurred?.Invoke(this, new IOException("网络连接中断", ex));
        }
        catch (SocketException ex)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)ex.SocketErrorCode));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            // 确保连接状态被正确清理
            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, "连接已断开");
            }
        }
    }

    /// <summary>
    /// 处理帧缓冲更新消息。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        if (Framebuffer == null || _stream == null || _protocol == null)
        {
            return;
        }

        try
        {
            // 读取填充字节和矩形数量
            await _stream.ReadExactlyAsync(1, ct); // 填充字节
            ushort rectCount = await _stream.ReadUInt16Async(ct);
            var updatedRects = new List<FramebufferRect>(rectCount);

            for (int i = 0; i < rectCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                // 直接从流读取矩形头信息
                ushort x = await _stream.ReadUInt16Async(ct);
                ushort y = await _stream.ReadUInt16Async(ct);
                ushort w = await _stream.ReadUInt16Async(ct);
                ushort h = await _stream.ReadUInt16Async(ct);
                int encodingType = (int)await _stream.ReadUInt32Async(ct);

                // CopyRect特殊处理：在VncClient中直接处理复制操作
                if (encodingType == EncodingTypes.CopyRect)
                {
                    byte[] coords = await _stream.ReadExactlyAsync(4, ct);
                    ushort srcX = (ushort)((coords[0] << 8) | coords[1]);
                    ushort srcY = (ushort)((coords[2] << 8) | coords[3]);
                    Framebuffer.CopyRect(srcX, srcY, x, y, w, h);
                    updatedRects.Add(new FramebufferRect(x, y, w, h, encodingType));
                    continue;
                }

                // 使用对应的编码处理器解码像素数据
                if (_encodings.TryGetValue(encodingType, out IEncoding? encoding))
                {
                    var protocolRect = new MiniVNC.Protocol.FramebufferRect(x, y, w, h, encodingType);
                    var protocolFormat = new MiniVNC.Protocol.PixelFormat(
                        PixelFormat.BitsPerPixel,
                        PixelFormat.Depth,
                        PixelFormat.BigEndian,
                        PixelFormat.TrueColor,
                        PixelFormat.RedMax,
                        PixelFormat.GreenMax,
                        PixelFormat.BlueMax,
                        PixelFormat.RedShift,
                        PixelFormat.GreenShift,
                        PixelFormat.BlueShift);

                    byte[] pixelData = await encoding.DecodeAsync(_stream, protocolRect, protocolFormat, ct);

                    // 更新帧缓冲（pixelData为Bgra32格式）
                    Framebuffer.UpdateRectBgra32(x, y, w, h, pixelData);

                    updatedRects.Add(new FramebufferRect(x, y, w, h, encodingType));
                }
                else
                {
                    // 跳过不支持的编码
                    StatusChanged?.Invoke(this,
                        $"跳过不支持的编码类型: {encodingType}");

                    // 尝试跳过未知编码的数据
                    await SkipUnknownEncodingDataAsync(encodingType, w, h, ct);
                }
            }

            // 触发帧缓冲更新事件
            if (updatedRects.Count > 0)
            {
                FramebufferUpdated?.Invoke(this, new FramebufferUpdateEventArgs(updatedRects));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this,
                new InvalidOperationException("处理帧缓冲更新时出错", ex));
        }
    }

    /// <summary>
    /// 异步处理服务器剪贴板文本消息。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    private async Task HandleServerCutTextAsync(CancellationToken ct)
    {
        if (_stream == null) return;

        try
        {
            // 读取剪贴板文本：3字节填充 + 4字节长度 + 文本数据
            await _stream.ReadExactlyAsync(3, ct); // 填充字节
            uint textLength = await _stream.ReadUInt32Async(ct);
            byte[] textBytes = await _stream.ReadExactlyAsync((int)textLength, ct);
            string text = System.Text.Encoding.UTF8.GetString(textBytes);

            if (!string.IsNullOrEmpty(text))
            {
                ServerClipboardChanged?.Invoke(this, text);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this,
                new InvalidOperationException("处理剪贴板文本时出错", ex));
        }
    }

    /// <summary>
    /// 处理响铃消息。
    /// </summary>
    private void HandleBell()
    {
        try
        {
            // 播放系统提示音
            System.Console.Beep();
        }
        catch (Exception)
        {
            // 在某些平台上Beep可能不可用，忽略错误
        }
    }

    /// <summary>
    /// 跳过未知编码类型的数据。
    /// </summary>
    /// <param name="encodingType">编码类型</param>
    /// <param name="width">矩形宽度</param>
    /// <param name="height">矩形高度</param>
    /// <param name="ct">取消令牌</param>
    private async Task SkipUnknownEncodingDataAsync(int encodingType, ushort width, ushort height, CancellationToken ct)
    {
        // 对于未知编码，尝试读取原始字节数
        if (_stream == null) return;

        try
        {
            // 大多数编码至少包含width*height*bpp的像素数据
            int bytesToSkip = width * height * 4; // 假设32bpp
            await _stream.ReadExactlyAsync(bytesToSkip, ct);
        }
        catch (Exception)
        {
            // 跳过失败，可能导致后续数据混乱
        }
    }
}
