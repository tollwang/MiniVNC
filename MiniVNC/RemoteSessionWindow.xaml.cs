using System;
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
    /// 剪贴板同步中标志（防止双向同步死循环）
    /// </summary>
    private bool _isSyncingClipboard = false;

    /// <summary>
    /// 工具栏隐藏延迟计时器
    /// </summary>
    private DispatcherTimer? _toolbarHideTimer;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="settings">连接设置</param>
    public RemoteSessionWindow(ConnectionSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _client = new VncClient();

        // 绑定事件
        _client.FramebufferUpdated += OnFramebufferUpdated;
        _client.StatusChanged += OnStatusChanged;
        _client.Connected += OnConnected;
        _client.Disconnected += OnDisconnected;
        _client.ServerClipboardChanged += OnServerClipboardChanged;
        _client.ErrorOccurred += OnError;

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

    /// <summary>
    /// 窗口加载事件 - 建立VNC连接
    /// </summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateStatus($"正在连接 {_settings.Host}:{_settings.Port}...");

            await _client.ConnectAsync(_settings.Host, _settings.Port);
            await _client.AuthenticateAsync(_settings.Password ?? "");
            await _client.InitializeAsync();

            // 设置VncViewport
            VncViewport.SetClient(_client);

            // 启动更新循环
            _client.StartUpdateLoop();

            // 启动状态定时器
            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _statusTimer.Tick += OnStatusTimerTick;
            _statusTimer.Start();

            // 启动剪贴板同步定时器
            StartClipboardSync();

            // 请求初始帧缓冲区更新（第一次请求完整更新）
            _client.RequestFramebufferUpdate(
                false,
                0,
                0,
                _client.FramebufferWidth,
                _client.FramebufferHeight);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    /// <summary>
    /// 帧缓冲区更新事件
    /// </summary>
    private void OnFramebufferUpdated(object? sender, FramebufferUpdateEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            VncViewport.UpdateFramebuffer();

            // 请求下一帧增量更新
            _client.RequestFramebufferUpdate(
                true,
                0,
                0,
                _client.FramebufferWidth,
                _client.FramebufferHeight);
        });
    }

    /// <summary>
    /// 状态变化事件
    /// </summary>
    private void OnStatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateStatus(status);
        });
    }

    /// <summary>
    /// 连接成功事件
    /// </summary>
    private void OnConnected(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateStatus($"已连接到 {_settings.Host}:{_settings.Port}");
            TbConnectionState.Text = "已连接";
            TbConnectionState.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x10));
            TbResolution.Text = $"{_client.FramebufferWidth}x{_client.FramebufferHeight}";
        });
    }

    /// <summary>
    /// 断开连接事件
    /// </summary>
    private void OnDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateStatus("连接已断开");
            TbConnectionState.Text = "已断开";
            TbConnectionState.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC7, 0x54, 0x50));
        });
    }

    /// <summary>
    /// 服务器剪贴板变化事件
    /// </summary>
    private void OnServerClipboardChanged(object? sender, string text)
    {
        if (!_clipboardSyncEnabled || _isSyncingClipboard) return;

        Dispatcher.Invoke(() =>
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
        Dispatcher.Invoke(() =>
        {
            UpdateStatus($"错误: {ex.Message}");
        });
    }

    /// <summary>
    /// 状态定时器 tick - 更新状态信息
    /// </summary>
    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_client?.IsConnected == true)
        {
            TbResolution.Text = $"{_client.FramebufferWidth}x{_client.FramebufferHeight}";
            TbEncoding.Text = _client.CurrentEncoding ?? "Raw";
        }
    }

    /// <summary>
    /// 启动剪贴板同步
    /// </summary>
    private void StartClipboardSync()
    {
        var clipboardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        clipboardTimer.Tick += async (s, e) =>
        {
            if (!_clipboardSyncEnabled || !_client.IsConnected || _isSyncingClipboard) return;

            try
            {
                var text = ClipboardHelper.GetText();
                if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                {
                    _isSyncingClipboard = true;
                    _lastClipboardText = text;
                    _client.SetClientClipboard(text);
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
        clipboardTimer.Start();
    }

    /// <summary>
    /// 更新状态栏文本
    /// </summary>
    private void UpdateStatus(string message)
    {
        Dispatcher.Invoke(() =>
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
        try
        {
            _statusTimer?.Stop();
            _toolbarHideTimer?.Stop();
            _client.Disconnect();
        }
        catch { /* 忽略断开时的错误 */ }
        finally
        {
            Close();
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

        // ESC - 如果全屏则退出全屏
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// 窗口关闭事件
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _statusTimer?.Stop();
        _toolbarHideTimer?.Stop();
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.StatusChanged -= OnStatusChanged;
        _client.Connected -= OnConnected;
        _client.Disconnected -= OnDisconnected;
        _client.ServerClipboardChanged -= OnServerClipboardChanged;
        _client.ErrorOccurred -= OnError;
        _client.Dispose();

        base.OnClosing(e);
    }
}
