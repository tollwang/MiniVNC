using System.IO;
using System.Net.Sockets;
using System.Threading.Channels;
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
/// 光标变化事件参数（来自 Cursor 伪编码 -239）。<see cref="Bgra"/> 为 BGRA32 自上而下像素，
/// 透明像素 A=0；<see cref="Width"/>×<see cref="Height"/> 为光标尺寸；热点为 (<see cref="HotspotX"/>,<see cref="HotspotY"/>)。
/// 当 Width/Height 为 0 表示服务器要求隐藏光标（改用默认指针）。
/// </summary>
public sealed class CursorUpdateEventArgs : EventArgs
{
    /// <summary>BGRA32 像素（自上而下，逐行），透明像素 Alpha=0。</summary>
    public byte[] Bgra { get; }
    /// <summary>光标宽度（像素）。</summary>
    public int Width { get; }
    /// <summary>光标高度（像素）。</summary>
    public int Height { get; }
    /// <summary>热点 X（相对光标左上角）。</summary>
    public int HotspotX { get; }
    /// <summary>热点 Y（相对光标左上角）。</summary>
    public int HotspotY { get; }

    /// <summary>创建光标更新事件参数。</summary>
    public CursorUpdateEventArgs(byte[] bgra, int width, int height, int hotspotX, int hotspotY)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
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

    /// <summary>服务器推送光标形状变化时触发（Cursor 伪编码 -239）。</summary>
    public event EventHandler<CursorUpdateEventArgs>? CursorChanged;

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

    /// <summary>
    /// 期望的颜色深度（位/像素）：32=高清全彩；16=流畅（RGB565，带宽减半）。
    /// 须在 <see cref="InitializeAsync"/> 之前设置。
    /// </summary>
    public int PreferredColorDepth { get; set; } = 32;

    /// <summary>
    /// 仅查看模式。为 true 时不向服务器发送鼠标/键盘/剪贴板输入。
    /// </summary>
    public bool ViewOnly { get; set; }

    // ---- 私有字段 ----

    private VncStream? _stream;
    private RfbProtocol? _protocol;
    private CancellationTokenSource? _cts;
    private Task? _messageLoopTask;
    private readonly Dictionary<int, IEncoding> _encodings;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// 客户端写队列。会话期间 UI 线程（鼠标/键盘/剪贴板）与消息循环（增量更新请求）只把
    /// "写动作"入队（非阻塞），由唯一的后台写线程串行执行真正的 socket 写——
    /// 避免发送缓冲填满时同步写阻塞 UI 线程造成卡死/无响应。
    /// </summary>
    private Channel<Action>? _writeQueue;
    private Task? _writerTask;
    private bool _disposed;

    // ---- 光标去重（仅消息循环线程访问，无需加锁）----
    private byte[]? _lastCursorBgra;
    private int _lastCursorW, _lastCursorH, _lastCursorHotX, _lastCursorHotY;

    /// <summary>
    /// 服务器是否已开启连续更新（收到 EndOfContinuousUpdates 后开启）。开启后服务器主动推送增量，
    /// 客户端不再逐帧发 FramebufferUpdateRequest。仅消息循环线程访问。
    /// </summary>
    private bool _continuousUpdates;

    /// <summary>
    /// 创建 <see cref="VncClient"/>。
    /// 注册的解码器：Raw、Hextile（CopyRect 在消息循环中内联处理）。
    /// </summary>
    public VncClient()
    {
        _encodings = new Dictionary<int, IEncoding>
        {
            [EncodingTypes.Raw] = new RawEncoding(),
            [EncodingTypes.Hextile] = new HextileEncoding(),
            [EncodingTypes.Zrle] = new ZrleEncoding()
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

    /// <summary>
    /// 执行认证。支持 macOS 屏幕共享默认的 Apple/ARD 认证（类型30，需用户名+密码）、
    /// 标准 VNC 密码认证（类型2，仅密码）与无认证（类型1）。
    /// </summary>
    /// <param name="username">账户用户名（ARD 认证需要；VNC 密码认证可留空）。</param>
    /// <param name="password">连接密码。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_protocol == null || !IsConnected)
            throw new InvalidOperationException("未连接到服务器");

        StatusChanged?.Invoke(this, "正在认证...");

        byte[] securityTypes = await _protocol.ReadSecurityTypesAsync(ct);
        if (securityTypes.Length == 0)
            throw new InvalidOperationException("服务器拒绝连接：未提供安全类型");

        StatusChanged?.Invoke(this, $"服务器支持的安全类型: {string.Join(", ", securityTypes)}");

        bool hasUsername = !string.IsNullOrEmpty(username);
        bool hasApple = securityTypes.Contains((byte)30);
        bool hasVnc = securityTypes.Contains((byte)2);
        bool hasNone = securityTypes.Contains((byte)1);

        // 优先级：提供了用户名且支持 ARD → ARD；否则标准 VNC；否则 ARD；否则无认证。
        if (hasApple && (hasUsername || !hasVnc))
        {
            await PerformAppleAuthenticationAsync(username, password, ct);
        }
        else if (hasVnc)
        {
            await PerformVncAuthenticationAsync(password, ct);
        }
        else if (hasNone)
        {
            await PerformNoneAuthenticationAsync(ct);
        }
        else
        {
            throw new NotSupportedException(
                $"服务器不支持任何已知的安全类型。提供的类型: {string.Join(", ", securityTypes)}。");
        }

        StatusChanged?.Invoke(this, "认证成功");
    }

    /// <summary>向后兼容的仅密码认证重载（用户名留空）。</summary>
    public Task AuthenticateAsync(string password, CancellationToken ct = default)
        => AuthenticateAsync(string.Empty, password, ct);

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

        // 协商目标像素格式：高清=32bpp 大端真彩；流畅=16bpp RGB565（带宽减半）
        var preferredFormat = PreferredColorDepth == 16
            ? new PixelFormat(16, 16, true, true, 31, 63, 31, 11, 5, 0)
            : new PixelFormat(32, 24, true, true, 255, 255, 255, 16, 8, 0);
        _protocol.WriteSetPixelFormat(preferredFormat);
        PixelFormat = preferredFormat;

        // 创建 BGRA32 帧缓冲
        Framebuffer = new Framebuffer(FramebufferWidth, FramebufferHeight);

        // 协商编码优先级：ZRLE > Hextile > CopyRect > Raw
        // 若 ZRLE 在实测中出现问题，将 ZRLE 从列表移到末尾或移除即可回退到 Hextile。
        int[] preferredEncodings = new[]
        {
            EncodingTypes.Zrle,
            EncodingTypes.Hextile,
            EncodingTypes.CopyRect,
            EncodingTypes.Raw,
            EncodingTypes.Cursor,            // 伪编码：服务器以光标形状推送，客户端本地渲染
            EncodingTypes.ContinuousUpdates  // 伪编码：协商连续更新（服务器支持则主动推帧，省往返延迟）
        };
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
        _writeQueue?.Writer.TryComplete();
        try { _messageLoopTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        try { _writerTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts?.Dispose();

        _cts = new CancellationTokenSource();
        // 单读多写：UI 线程与消息循环都可入队，由唯一写线程消费
        _writeQueue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        _writerTask = Task.Run(() => WriterLoopAsync(_writeQueue.Reader, _cts.Token));
        _messageLoopTask = Task.Run(() => MessageLoopAsync(_cts.Token));

        // 初始请求完整屏幕（非增量）
        RequestFramebufferUpdate(false, 0, 0, FramebufferWidth, FramebufferHeight);
    }

    /// <summary>把一个"写动作"入队（非阻塞）。连接未就绪/队列已关闭则丢弃。</summary>
    private void Enqueue(Action write)
    {
        _writeQueue?.Writer.TryWrite(write);
    }

    /// <summary>发送鼠标/指针事件。仅查看模式下不发送。</summary>
    public void SendPointerEvent(int x, int y, int buttonMask)
    {
        if (_disposed) return;
        var p = _protocol;
        if (p == null || !IsConnected || ViewOnly) return;

        x = Math.Clamp(x, 0, Math.Max(0, FramebufferWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, FramebufferHeight - 1));
        byte bm = (byte)buttonMask; ushort bx = (ushort)x; ushort by = (ushort)y;
        Enqueue(() => p.WritePointerEvent(bm, bx, by));
    }

    /// <summary>发送键盘事件。仅查看模式下不发送。</summary>
    public void SendKeyEvent(uint keysym, bool pressed)
    {
        if (_disposed) return;
        var p = _protocol;
        if (p == null || !IsConnected || ViewOnly) return;

        Enqueue(() => p.WriteKeyEvent(pressed, keysym));
    }

    /// <summary>发送剪贴板文本到服务器（限制最大1MB，防止超大剪贴板整包发出）。</summary>
    public void SendCutText(string text)
    {
        if (_disposed) return;
        if (text == null) return;
        var p = _protocol;
        if (p == null || !IsConnected || ViewOnly) return;

        // 防错：限制剪贴板发送大小
        const int maxLen = 1024 * 1024;
        string t = text.Length > maxLen ? text.Substring(0, maxLen) : text;
        Enqueue(() => p.WriteCutText(t));
    }

    /// <summary>请求帧缓冲更新。</summary>
    public void RequestFramebufferUpdate(bool incremental, int x, int y, int w, int h)
    {
        if (_disposed) return;
        var p = _protocol;
        if (p == null || !IsConnected) return; // 静默失败

        bool inc = incremental; ushort bx = (ushort)x, by = (ushort)y, bw = (ushort)w, bh = (ushort)h;
        Enqueue(() => p.WriteFramebufferUpdateRequest(inc, bx, by, bw, bh));
    }

    /// <summary>对指定区域开启服务器连续更新（收到 EndOfContinuousUpdates 后调用）。</summary>
    private void EnableContinuousUpdates(int x, int y, int w, int h)
    {
        if (_disposed) return;
        var p = _protocol;
        if (p == null || !IsConnected) return;

        ushort bx = (ushort)x, by = (ushort)y, bw = (ushort)w, bh = (ushort)h;
        Enqueue(() => p.WriteEnableContinuousUpdates(true, bx, by, bw, bh));
    }

    /// <summary>
    /// 后台写线程：从队列取出写动作并同步执行（受 socket SendTimeout 约束）。
    /// 写失败（超时/连接重置）即上报并关闭流，触发消息循环读出错 → 正常断开/自动重连。
    /// </summary>
    private async Task WriterLoopAsync(ChannelReader<Action> reader, CancellationToken ct)
    {
        try
        {
            await foreach (Action write in reader.ReadAllAsync(ct))
            {
                write();
            }
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
        catch (ChannelClosedException) { /* 正常关闭 */ }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            try { _stream?.Dispose(); } catch { } // 关闭流以唤醒消息循环的阻塞读 → 触发断开/重连
        }
    }

    /// <summary>断开与服务器的连接（线程安全，可多次调用）。</summary>
    public void Disconnect()
    {
        if (!IsConnected && _stream == null) return;

        // 先置 false：让消息循环的 finally 跳过 Disconnected 触发，
        // 避免后台线程在 Dispatcher.Invoke 与本方法的 Wait 之间互等造成卡顿。
        IsConnected = false;

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        _writeQueue?.Writer.TryComplete(); // 结束写线程的 ReadAllAsync

        try { _messageLoopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        try { _writerTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }

        try { _stream?.Dispose(); }
        catch (Exception) { }

        _stream = null;
        _protocol = null;

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

    /// <summary>Apple/ARD 认证（安全类型30，macOS 屏幕共享默认）。</summary>
    private async Task PerformAppleAuthenticationAsync(string username, string password, CancellationToken ct)
    {
        if (_protocol == null)
            throw new InvalidOperationException("协议处理器未初始化");

        // 明确提示：macOS 屏幕共享的 Apple 认证需要 Mac 账户用户名，空用户名必然失败
        if (string.IsNullOrEmpty(username))
            throw new InvalidOperationException(
                "此服务器要求 Apple 屏幕共享认证（类型30），需要填写 Mac 账户用户名。请在连接设置中填写用户名后重试。");

        StatusChanged?.Invoke(this, "正在进行 Apple 屏幕共享认证...");

        _protocol.WriteSecurityType(30);

        var (generator, prime, serverPublicKey) = await _protocol.ReadAppleDhParamsAsync(ct);
        var (cipher, clientPublicKey) = AppleAuthenticator.CreateResponse(
            generator, prime, serverPublicKey, username, password);
        _protocol.WriteAppleDhResponse(cipher, clientPublicKey);

        uint result = await _protocol.ReadSecurityResultAsync(ct);
        if (result != 0)
        {
            string? errorMsg = await _protocol.ReadSecurityResultErrorAsync(ct);
            throw new InvalidOperationException(
                $"Apple 认证失败: {errorMsg ?? "用户名或密码错误"} (错误码: {result})");
        }
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

                    case ServerMessageType.EndOfContinuousUpdates:
                        // 服务器支持连续更新：开启后由服务器主动推帧，客户端不再逐帧请求（省往返延迟）。
                        if (!_continuousUpdates)
                        {
                            _continuousUpdates = true;
                            EnableContinuousUpdates(0, 0, FramebufferWidth, FramebufferHeight);
                            StatusChanged?.Invoke(this, "已启用连续更新（更跟手）");
                        }
                        break;

                    default:
                        // 未知消息类型会导致流无法继续解析，断开以避免读到垃圾数据
                        throw new IOException($"收到未知的服务器消息类型: {(byte)messageType}");
                }

                // 连续更新开启后服务器会主动推送，无需逐帧再请求增量；否则保持请求-应答循环。
                if (needsUpdateRequest && !_continuousUpdates && IsConnected && _protocol != null)
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

            // Cursor 伪编码(-239)：x/y 为热点、w/h 为光标尺寸。不改帧缓冲，单独处理后跳过。
            if (encodingType == EncodingTypes.Cursor)
            {
                await HandleCursorPseudoEncodingAsync(x, y, w, h, ct);
                continue;
            }

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

    /// <summary>
    /// 处理 Cursor 伪编码(-239)：读取 w×h 像素 + 1bpp 透明掩码，转为 BGRA32 并通过
    /// <see cref="CursorChanged"/> 上抛由 UI 本地渲染。掩码位=0 的像素置为透明(A=0)。
    /// </summary>
    private async Task HandleCursorPseudoEncodingAsync(ushort hotspotX, ushort hotspotY, ushort w, ushort h, CancellationToken ct)
    {
        if (_stream == null) return;

        // 上限保护：真实光标极小（通常 ≤128px）。拒绝异常尺寸，防止 w*h 整数溢出与
        // 超大分配/读取（恶意服务器或流错位时）；这种值几乎必为协议错位，断开比继续读垃圾更安全。
        if (w > 1024 || h > 1024)
            throw new IOException($"光标尺寸异常: {w}x{h}");

        int bpp = PixelFormat.BytesPerPixel;
        int pixelLen = w * h * bpp;
        int maskRowBytes = (w + 7) / 8;          // 每行掩码字节数（向上取整到位）
        int maskLen = maskRowBytes * h;

        byte[] pixelData = pixelLen > 0 ? await _stream.ReadExactlyAsync(pixelLen, ct) : Array.Empty<byte>();
        byte[] mask = maskLen > 0 ? await _stream.ReadExactlyAsync(maskLen, ct) : Array.Empty<byte>();

        // 空光标：服务器要求隐藏光标 → 通知 UI 回退默认指针（与上次相同则跳过）
        if (w == 0 || h == 0)
        {
            if (_lastCursorW == 0 && _lastCursorH == 0) return;
            _lastCursorBgra = null;
            _lastCursorW = _lastCursorH = _lastCursorHotX = _lastCursorHotY = 0;
            CursorChanged?.Invoke(this, new CursorUpdateEventArgs(Array.Empty<byte>(), 0, 0, 0, 0));
            return;
        }

        byte[] bgra = new byte[w * h * 4];
        for (int yy = 0; yy < h; yy++)
        {
            int maskRow = yy * maskRowBytes;
            for (int xx = 0; xx < w; xx++)
            {
                int di = (yy * w + xx) * 4;
                uint px = PixelFormat.ReadPixel(pixelData, (yy * w + xx) * bpp);
                PixelFormat.WriteBgra32(px, bgra, di); // 写入 B,G,R,A=255

                // 掩码：MSB 在前，位=1 表示该像素可见(不透明)
                bool visible = ((mask[maskRow + (xx >> 3)] >> (7 - (xx & 7))) & 1) != 0;
                if (!visible) bgra[di + 3] = 0;        // 透明
            }
        }

        // 去重：与上次光标完全相同则跳过，避免服务器逐帧重发时频繁重建 HCURSOR。
        if (_lastCursorW == w && _lastCursorH == h && _lastCursorHotX == hotspotX && _lastCursorHotY == hotspotY
            && _lastCursorBgra != null && _lastCursorBgra.AsSpan().SequenceEqual(bgra))
            return;

        _lastCursorBgra = bgra;
        _lastCursorW = w; _lastCursorH = h; _lastCursorHotX = hotspotX; _lastCursorHotY = hotspotY;

        CursorChanged?.Invoke(this, new CursorUpdateEventArgs(bgra, w, h, hotspotX, hotspotY));
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
        // 自适应解码：先试 UTF-8（中文/Emoji），失败回退 Latin-1（西欧字符）
        string text = RfbProtocol.DecodeCutText(textBytes);

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
        // 异步派发：Console.Beep 在 Windows 会阻塞约 200ms，绝不能卡住消息循环
        _ = Task.Run(() =>
        {
            try { Console.Beep(); }
            catch (Exception) { /* 某些环境不可用，忽略 */ }
        });
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
