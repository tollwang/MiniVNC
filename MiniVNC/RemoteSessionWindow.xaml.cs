using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiniVNC.Controls;
using MiniVNC.Core;
using MiniVNC.Input;
using MiniVNC.Native;
using MiniVNC.Protocol;

namespace MiniVNC;

/// <summary>
/// 远程会话窗口 - 全屏VNC查看
/// </summary>
public partial class RemoteSessionWindow : Window
{
    /// <summary>
    /// VNC客户端实例
    /// </summary>
    private VncClient _client;

    /// <summary>
    /// 连接设置
    /// </summary>
    private ConnectionSettings _settings;

    /// <summary>
    /// 状态刷新定时器
    /// </summary>
    private DispatcherTimer? _statusTimer;

    /// <summary>
    /// 是否全屏模式
    /// </summary>
    private bool _isFullscreen = true;

    /// <summary>
    /// 剪贴板同步是否启用
    /// </summary>
    private bool _clipboardSyncEnabled = true;

    /// <summary>
    /// 上次剪贴板内容（防止重复同步）
    /// </summary>
    private string _lastClipboardText = string.Empty;

    /// <summary>
    /// 剪贴板同步中标志（防止双向同步死循环）。跨线程读写，标记为 volatile。
    /// </summary>
    private volatile bool _isSyncingClipboard = false;

    /// <summary>渲染合并锁。</summary>
    private readonly object _renderLock = new();

    /// <summary>累积的待渲染矩形（合并多次更新为一次渲染）。</summary>
    private List<FramebufferRect>? _pendingRects;

    /// <summary>是否已排队一次渲染（确保渲染队列深度恒为1）。</summary>
    private bool _renderQueued;

    /// <summary>最近一次成功建立连接的时刻（UTC），用于判断"快速掉线"。</summary>
    private DateTime _lastConnectTime;

    /// <summary>连续"快速掉线"次数，用于阻止自动重连陷入死循环。</summary>
    private int _quickDropCount = 0;

    /// <summary>
    /// 工具栏隐藏延迟计时器
    /// </summary>
    private DispatcherTimer? _toolbarHideTimer;

    /// <summary>剪贴板轮询定时器（需在关闭时停止，避免泄漏）。</summary>
    private DispatcherTimer? _clipboardTimer;

    /// <summary>用户主动关闭/断开标志。用于区分"用户关闭"与"意外断线"，避免误触发自动重连或关闭后竞态。</summary>
    private bool _userClosing = false;

    /// <summary>是否正在自动重连。</summary>
    private bool _reconnecting = false;

    /// <summary>状态/剪贴板定时器是否已启动（确保只启动一次，重连时不重复）。</summary>
    private bool _timersStarted = false;

    /// <summary>自动重连最大尝试次数。</summary>
    private const int MaxReconnectAttempts = 5;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="settings">连接设置</param>
    public RemoteSessionWindow(ConnectionSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _client = new VncClient { ViewOnly = settings.ViewOnly };
        BindClientEvents();

        // 初始化工具栏隐藏计时器
        _toolbarHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _toolbarHideTimer.Tick += (s, e) =>
        {
            HideToolbar();
            _toolbarHideTimer?.Stop();
        };

        // 键盘焦点始终在VncViewport上
        Loaded += (s, e) => VncViewport.Focus();
        Activated += (s, e) => VncViewport.Focus();
    }

    /// <summary>绑定 VNC 客户端事件。</summary>
    private void BindClientEvents()
    {
        _client.FramebufferUpdated += OnFramebufferUpdated;
        _client.StatusChanged += OnStatusChanged;
        _client.Connected += OnConnected;
        _client.Disconnected += OnDisconnected;
        _client.ServerClipboardChanged += OnServerClipboardChanged;
        _client.ErrorOccurred += OnError;
        _client.CursorChanged += OnCursorChanged;
    }

    /// <summary>解绑 VNC 客户端事件。</summary>
    private void UnbindClientEvents()
    {
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.StatusChanged -= OnStatusChanged;
        _client.Connected -= OnConnected;
        _client.Disconnected -= OnDisconnected;
        _client.ServerClipboardChanged -= OnServerClipboardChanged;
        _client.ErrorOccurred -= OnError;
        _client.CursorChanged -= OnCursorChanged;
    }

    /// <summary>
    /// 窗口加载事件 - 建立VNC连接
    /// </summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ConnectSequenceAsync();
        }
        catch (OperationCanceledException)
        {
            if (_userClosing) return;
            MessageBox.Show(
                "连接超时：服务器未在 25 秒内完成握手/初始化。\n\n" +
                "可重试连接（首次连接偶发卡顿时再连一次通常即可）。\n" +
                "若 Mac 端设置为“任何人都可请求控制”，请先在 Mac 上点击允许后重试。",
                "连接超时", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        catch (Exception ex)
        {
            if (_userClosing) return;
            MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    /// <summary>
    /// 执行一次完整的连接序列（连接→认证→初始化→启动循环）。
    /// 用于首次连接与自动/手动重连复用。带 25 秒超时；关窗时安全退出。
    /// </summary>
    private async Task ConnectSequenceAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        UpdateStatus($"正在连接 {_settings.Host}:{_settings.Port}...");

        await _client.ConnectAsync(_settings.Host, _settings.Port, cts.Token);
        await _client.AuthenticateAsync(_settings.Username ?? "", _settings.Password ?? "", cts.Token);
        _client.PreferredColorDepth = _settings.ColorDepth == 16 ? 16 : 32;
        _client.ViewOnly = _settings.ViewOnly;
        await _client.InitializeAsync(cts.Token);

        // 连接过程中窗口已被关闭：安全退出，不再操作已释放的对象
        if (_userClosing) return;

        VncViewport.SetClient(_client);
        _client.StartUpdateLoop();
        _lastConnectTime = DateTime.UtcNow;

        // 初始化完成后尺寸已知，立即更新分辨率显示（避免显示 0x0）
        TbResolution.Text = $"{_client.FramebufferWidth}x{_client.FramebufferHeight}";

        StartTimersOnce();
        // 初始完整更新请求已由 StartUpdateLoop 发起，增量更新由消息循环驱动。
    }

    /// <summary>启动状态/剪贴板定时器（只启动一次，重连时不重复创建）。</summary>
    private void StartTimersOnce()
    {
        if (_timersStarted) return;
        _timersStarted = true;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();

        StartClipboardSync();
    }

    /// <summary>
    /// 帧缓冲区更新事件
    /// </summary>
    private void OnFramebufferUpdated(object? sender, FramebufferUpdateEventArgs e)
    {
        // 合并待渲染矩形并以 BeginInvoke 异步派发（非阻塞后台线程）：
        // 1) 避免断开时 UI 的 Wait 与后台 Invoke 互等造成卡顿；
        // 2) 多次更新合并为一次渲染，队列深度恒为1，防止高负载下堆积。
        lock (_renderLock)
        {
            (_pendingRects ??= new List<FramebufferRect>(e.UpdatedRects.Count)).AddRange(e.UpdatedRects);
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderPending));
    }

    /// <summary>在 UI 线程上排空并渲染累积的待刷新矩形。</summary>
    private void RenderPending()
    {
        List<FramebufferRect>? rects;
        lock (_renderLock)
        {
            rects = _pendingRects;
            _pendingRects = null;
            _renderQueued = false;
        }
        if (rects != null && rects.Count > 0)
            VncViewport.UpdateFramebuffer(rects);
    }

    /// <summary>
    /// 状态变化事件
    /// </summary>
    private void OnStatusChanged(object? sender, string status)
    {
        UpdateStatus(status);
    }

    /// <summary>
    /// 连接成功事件
    /// </summary>
    private void OnConnected(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus($"已连接到 {_settings.Host}:{_settings.Port}");
            TbConnectionState.Text = "已连接";
            TbConnectionState.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x10));
            // 分辨率在初始化完成后由 ConnectSequenceAsync 设置（此刻尚未取得，避免显示 0x0）
        });
    }

    /// <summary>
    /// 断开连接事件
    /// </summary>
    private void OnDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TbConnectionState.Text = "已断开";
            TbConnectionState.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC7, 0x54, 0x50));

            // 意外断线（非用户主动）且开启了自动重连 → 触发重连
            if (!_userClosing && !_reconnecting && _settings.AutoReconnect)
            {
                // 快速掉线保护：若连上后很快又断开，累计计数；连续多次则停止自动重连，
                // 避免"连上→秒断→重连"无限循环（如某种持续性解码/协议问题）。
                bool stable = (DateTime.UtcNow - _lastConnectTime).TotalSeconds > 8;
                if (stable) _quickDropCount = 0; else _quickDropCount++;

                if (_quickDropCount >= 3)
                {
                    UpdateStatus("连接反复快速断开，已停止自动重连。请点击工具栏“重连”手动重试，或检查网络/画质设置。");
                }
                else
                {
                    _ = TryReconnectAsync();
                }
            }
            else
            {
                UpdateStatus("连接已断开");
            }
        });
    }

    /// <summary>
    /// 自动重连：重建客户端并按退避间隔重试若干次。期间用户可随时关闭。
    /// </summary>
    private async Task TryReconnectAsync()
    {
        _reconnecting = true;
        try
        {
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                if (_userClosing) return;

                UpdateStatus($"连接已断开，正在自动重连... (第 {attempt}/{MaxReconnectAttempts} 次)");
                await Task.Delay(2000);
                if (_userClosing) return;

                RebuildClient();

                try
                {
                    await ConnectSequenceAsync();
                    UpdateStatus("重连成功");
                    return;
                }
                catch (Exception)
                {
                    // 本次重连失败，继续下一次尝试
                }
            }

            if (!_userClosing)
                UpdateStatus("自动重连失败，请点击工具栏“重连”手动重试，或检查网络/Mac 端。");
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>重建 VNC 客户端（重连前调用）：解绑旧实例、释放、新建并重新绑定。</summary>
    private void RebuildClient()
    {
        UnbindClientEvents();
        try { _client.Dispose(); } catch { /* 忽略 */ }

        _client = new VncClient
        {
            ViewOnly = _settings.ViewOnly,
            PreferredColorDepth = _settings.ColorDepth == 16 ? 16 : 32
        };
        BindClientEvents();
    }

    /// <summary>
    /// 服务器剪贴板变化事件
    /// </summary>
    private void OnServerClipboardChanged(object? sender, string text)
    {
        if (!_clipboardSyncEnabled || _isSyncingClipboard) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
            {
                _isSyncingClipboard = true;
                _lastClipboardText = text;
                ClipboardHelper.SetText(text);
                // 延迟重置标志，给服务器响应时间
                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    _isSyncingClipboard = false;
                });
            }
        });
    }

    /// <summary>
    /// 错误事件
    /// </summary>
    private void OnError(object? sender, Exception ex)
    {
        UpdateStatus($"错误: {ex.Message}");
    }

    /// <summary>
    /// 远程光标形状变化（Cursor 伪编码 -239）→ 在 UI 线程更新本地渲染的光标。
    /// </summary>
    private void OnCursorChanged(object? sender, CursorUpdateEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
            VncViewport.SetRemoteCursor(e.Bgra, e.Width, e.Height, e.HotspotX, e.HotspotY));
    }

    /// <summary>
    /// 状态定时器 tick - 更新状态信息
    /// </summary>
    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_client?.IsConnected == true)
        {
            TbResolution.Text = $"{_client.FramebufferWidth}x{_client.FramebufferHeight}";
            TbEncoding.Text = _client.CurrentEncoding;
        }
    }

    /// <summary>
    /// 启动剪贴板同步
    /// </summary>
    private void StartClipboardSync()
    {
        _clipboardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _clipboardTimer.Tick += async (s, e) =>
        {
            if (_userClosing || !_clipboardSyncEnabled || !_client.IsConnected || _isSyncingClipboard) return;

            try
            {
                var text = ClipboardHelper.GetText();
                if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                {
                    _isSyncingClipboard = true;
                    _lastClipboardText = text;
                    _client.SendCutText(text);
                }
            }
            catch
            {
                // 剪贴板访问可能失败，忽略错误
            }
            finally
            {
                // 延迟重置标志，给服务器响应时间
                await Task.Delay(100);
                _isSyncingClipboard = false;
            }
        };
        _clipboardTimer.Start();
    }

    /// <summary>
    /// 更新状态栏文本
    /// </summary>
    private void UpdateStatus(string message)
    {
        // BeginInvoke：可安全从 UI 线程或后台线程调用，且不阻塞后台线程。
        Dispatcher.BeginInvoke(() =>
        {
            TbStatusInfo.Text = message;
        });
    }

    #region 工具栏按钮处理

    /// <summary>
    /// 返回窗口模式按钮
    /// </summary>
    private void BtnWindowed_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ToggleFullscreen();
        }
    }

    /// <summary>
    /// 全屏切换按钮
    /// </summary>
    private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    /// <summary>
    /// 缩放模式选择变化
    /// </summary>
    private void CmbZoomMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbZoomMode.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            VncViewport.StretchMode = tag switch
            {
                "Original" => StretchMode.Original,
                "Fit" => StretchMode.Fit,
                "Stretch" => StretchMode.Stretch,
                _ => StretchMode.Fit
            };
        }
    }

    /// <summary>
    /// 发送Mac快捷键菜单
    /// </summary>
    private void BtnSendKey_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x42)),
            BorderThickness = new Thickness(1)
        };

        // Mac快捷键定义
        var shortcuts = new (string Header, uint[] Keysyms)[]
        {
            ("Cmd+Space (Spotlight)", new[] { 0xFFEBu, 0x0020u }),
            ("Cmd+Tab (切换应用)", new[] { 0xFFEBu, 0xFF09u }),
            ("Cmd+C (复制)", new[] { 0xFFEBu, 0x0063u }),
            ("Cmd+V (粘贴)", new[] { 0xFFEBu, 0x0076u }),
            ("Cmd+X (剪切)", new[] { 0xFFEBu, 0x0078u }),
            ("Cmd+A (全选)", new[] { 0xFFEBu, 0x0061u }),
            ("Cmd+Z (撤销)", new[] { 0xFFEBu, 0x007Au }),
            ("Cmd+Shift+Z (重做)", new[] { 0xFFEBu, 0xFFE2u, 0x007Au }),
            ("Cmd+Q (退出)", new[] { 0xFFEBu, 0x0071u }),
            ("Cmd+W (关闭窗口)", new[] { 0xFFEBu, 0x0077u }),
            ("Cmd+N (新建)", new[] { 0xFFEBu, 0x006Eu }),
            ("Cmd+S (保存)", new[] { 0xFFEBu, 0x0073u }),
            ("Cmd+T (新建标签)", new[] { 0xFFEBu, 0x0074u }),
            ("Cmd+Option+Esc (强制退出)", new[] { 0xFFEBu, 0xFFEAu, 0xFF1Bu }),
        };

        foreach (var (header, keysyms) in shortcuts)
        {
            var menuItem = new MenuItem
            {
                Header = header,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC))
            };
            menuItem.Click += (s, ev) =>
            {
                KeyboardHandler.SendKeyCombo(_client, keysyms);
                VncViewport.Focus();
            };
            menu.Items.Add(menuItem);
        }

        // 分隔线
        menu.Items.Add(new Separator
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x42))
        });

        // 功能键
        var fkeys = new (string Header, uint Keysym)[]
        {
            ("F1", 0xFFBEu), ("F2", 0xFFBFu), ("F3", 0xFFC0u), ("F4", 0xFFC1u),
            ("F5", 0xFFC2u), ("F6", 0xFFC3u), ("F7", 0xFFC4u), ("F8", 0xFFC5u),
            ("F9", 0xFFC6u), ("F10", 0xFFC7u), ("F11", 0xFFC8u), ("F12", 0xFFC9u),
        };

        var fkeyMenu = new MenuItem
        {
            Header = "功能键",
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC))
        };

        foreach (var (header, keysym) in fkeys)
        {
            var fItem = new MenuItem
            {
                Header = header,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC))
            };
            fItem.Click += (s, ev) =>
            {
                _client.SendKeyEvent(keysym, true);
                _client.SendKeyEvent(keysym, false);
                VncViewport.Focus();
            };
            fkeyMenu.Items.Add(fItem);
        }

        menu.Items.Add(fkeyMenu);

        menu.PlacementTarget = BtnSendKey;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 剪贴板同步开关
    /// </summary>
    private void BtnClipboard_Click(object sender, RoutedEventArgs e)
    {
        _clipboardSyncEnabled = !_clipboardSyncEnabled;
        UpdateStatus(_clipboardSyncEnabled ? "剪贴板同步已启用" : "剪贴板同步已禁用");
    }

    /// <summary>
    /// 断开连接按钮
    /// </summary>
    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        DisconnectAndClose();
    }

    #endregion

    #region 工具栏显示/隐藏

    /// <summary>
    /// 鼠标进入工具栏触发区域
    /// </summary>
    private void ToolbarTrigger_MouseEnter(object sender, MouseEventArgs e)
    {
        ShowToolbar();
        _toolbarHideTimer?.Stop();
        _toolbarHideTimer?.Start();
    }

    /// <summary>
    /// 鼠标离开工具栏触发区域
    /// </summary>
    private void ToolbarTrigger_MouseLeave(object sender, MouseEventArgs e)
    {
        // 延迟隐藏
        _toolbarHideTimer?.Stop();
        _toolbarHideTimer?.Start();
    }

    /// <summary>
    /// 显示工具栏
    /// </summary>
    private void ShowToolbar()
    {
        ToolbarPanel.Visibility = Visibility.Visible;
        ToolbarPanel.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>
    /// 隐藏工具栏
    /// </summary>
    private void HideToolbar()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        anim.Completed += (s, e) =>
        {
            if (ToolbarPanel.Opacity == 0)
            {
                ToolbarPanel.Visibility = Visibility.Collapsed;
            }
        };
        ToolbarPanel.BeginAnimation(OpacityProperty, anim);
    }

    #endregion

    /// <summary>
    /// 切换全屏/窗口模式
    /// </summary>
    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;

        if (_isFullscreen)
        {
            WindowState = WindowState.Maximized;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Width = 1024;
            Height = 768;
        }

        // 确保VncViewport重新计算渲染区域
        VncViewport.InvalidateVisual();
        VncViewport.Focus();
    }

    /// <summary>
    /// 断开连接并关闭窗口
    /// </summary>
    private void DisconnectAndClose()
    {
        _userClosing = true; // 标记为用户主动关闭，阻止自动重连
        try
        {
            _statusTimer?.Stop();
            _toolbarHideTimer?.Stop();
            _clipboardTimer?.Stop();
            _client.Disconnect();
        }
        catch { /* 忽略断开时的错误 */ }
        finally
        {
            Close();
        }
    }

    /// <summary>
    /// 手动重连按钮：在意外断线/自动重连失败后由用户触发。
    /// </summary>
    private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_reconnecting || _userClosing) return;
        if (_client.IsConnected) return;

        _quickDropCount = 0; // 用户手动重连，重置快速掉线保护
        _reconnecting = true;
        try
        {
            RebuildClient();
            await ConnectSequenceAsync();
            UpdateStatus("重连成功");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("重连超时，请重试。");
        }
        catch (Exception ex)
        {
            UpdateStatus($"重连失败: {ex.Message}");
        }
        finally
        {
            _reconnecting = false;
            VncViewport.Focus();
        }
    }

    /// <summary>
    /// 窗口预览键盘事件 - 处理快捷键
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Alt+W - 窗口模式
        if (e.Key == Key.W && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (_isFullscreen)
            {
                ToggleFullscreen();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Alt+F - 全屏切换
        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        // Ctrl+Alt+D - 断开连接
        if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            DisconnectAndClose();
            e.Handled = true;
            return;
        }

        // 注意：不再用 ESC 退出全屏。ESC 在 Mac 上是高频按键（取消、退出、vim 等），
        // 必须放行给远端，否则全屏时按 ESC 只会退出全屏、永远传不到 Mac。
        // 退出全屏请用 Ctrl+Alt+F / Ctrl+Alt+W，或把鼠标移到顶部用悬浮工具栏。
    }

    /// <summary>
    /// 窗口关闭事件
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _userClosing = true; // 阻止关闭后的自动重连与连接竞态
        _statusTimer?.Stop();
        _toolbarHideTimer?.Stop();
        _clipboardTimer?.Stop();

        UnbindClientEvents();
        try { _client.Dispose(); } catch { /* 忽略释放时的错误 */ }

        base.OnClosing(e);
    }
}
