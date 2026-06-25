using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniVNC.Core;
using MiniVNC.Encodings;
using MiniVNC.Input;
using MiniVNC.Protocol;

namespace MiniVNC.Controls;

/// <summary>
/// VNC渲染画布 - 自定义WPF控件，用于显示远程桌面画面并处理输入事件
/// </summary>
public class VncViewport : Control
{
    /// <summary>
    /// 用于显示的位图
    /// </summary>
    private WriteableBitmap? _bitmap;

    /// <summary>
    /// VNC客户端引用
    /// </summary>
    private VncClient? _client;

    /// <summary>
    /// 上次鼠标位置
    /// </summary>
    private Point _lastMousePos;

    /// <summary>
    /// 当前鼠标按钮掩码
    /// </summary>
    private int _buttonMask;

    /// <summary>
    /// 已按下的物理键 → 按下时实际发送的 keysym。用于松开时回放同一 keysym，
    /// 以及焦点丢失/断连时释放全部按下键。
    /// </summary>
    private readonly Dictionary<Key, uint> _pressedKeys = new();

    #region 依赖属性

    /// <summary>
    /// 缩放级别依赖属性
    /// </summary>
    public static readonly DependencyProperty ZoomLevelProperty = DependencyProperty.Register(
        nameof(ZoomLevel),
        typeof(double),
        typeof(VncViewport),
        new PropertyMetadata(1.0, OnZoomLevelChanged));

    /// <summary>
    /// 拉伸模式依赖属性
    /// </summary>
    public static readonly DependencyProperty StretchModeProperty = DependencyProperty.Register(
        nameof(StretchMode),
        typeof(StretchMode),
        typeof(VncViewport),
        new PropertyMetadata(StretchMode.Fit, OnStretchModeChanged));

    /// <summary>
    /// 缩放级别
    /// </summary>
    public double ZoomLevel
    {
        get => (double)GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    /// <summary>
    /// 拉伸模式
    /// </summary>
    public StretchMode StretchMode
    {
        get => (StretchMode)GetValue(StretchModeProperty);
        set => SetValue(StretchModeProperty, value);
    }

    #endregion

    /// <summary>
    /// 静态构造函数 - 注册默认样式
    /// </summary>
    static VncViewport()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(VncViewport),
            new FrameworkPropertyMetadata(typeof(VncViewport)));

        // 确保控件可以接收焦点
        FocusableProperty.OverrideMetadata(
            typeof(VncViewport),
            new FrameworkPropertyMetadata(true));
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    public VncViewport()
    {
        // 设置焦点视觉样式为空（无边框虚线）
        FocusVisualStyle = null;

        // 确保控件可以获得焦点
        Loaded += (s, e) => Focus();

        // 防止焦点丢失
        LostFocus += (s, e) =>
        {
            // 释放所有按下的按键
            ReleaseAllKeys();
        };
    }

    /// <summary>
    /// 设置VNC客户端
    /// </summary>
    /// <param name="client">VNC客户端实例</param>
    public void SetClient(VncClient client)
    {
        _client = client;

        // 重置输入状态，避免上次会话残留的按键/按钮掩码影响新会话
        _buttonMask = 0;
        _pressedKeys.Clear();

        if (_client?.Framebuffer != null)
        {
            _bitmap = new WriteableBitmap(
                _client.FramebufferWidth,
                _client.FramebufferHeight,
                96, 96,
                PixelFormats.Bgra32,
                null);

            InvalidateVisual();
            Focus();
        }
    }

    /// <summary>
    /// 更新整幅帧缓冲区位图显示（全屏刷新）。
    /// </summary>
    public void UpdateFramebuffer()
    {
        var framebuffer = _client?.Framebuffer;
        if (framebuffer == null || _bitmap == null) return;

        try
        {
            _bitmap.Lock();
            try
            {
                framebuffer.CopyTo(_bitmap.BackBuffer, _bitmap.BackBufferStride);
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
            }
            finally
            {
                _bitmap.Unlock();
            }
            InvalidateVisual();
        }
        catch (Exception)
        {
            // 帧缓冲区更新失败时忽略
        }
    }

    /// <summary>
    /// 仅刷新发生变化的矩形区域（增量刷新，避免整屏拷贝，显著提升流畅度）。
    /// </summary>
    /// <param name="rects">本次更新的矩形列表（帧缓冲坐标）。</param>
    public void UpdateFramebuffer(IReadOnlyList<FramebufferRect> rects)
    {
        var framebuffer = _client?.Framebuffer;
        if (framebuffer == null || _bitmap == null) return;
        if (rects == null || rects.Count == 0) return;

        try
        {
            _bitmap.Lock();
            try
            {
                foreach (var r in rects)
                {
                    int x = r.X, y = r.Y, w = r.Width, h = r.Height;
                    // 裁剪到位图范围
                    if (x < 0) { w += x; x = 0; }
                    if (y < 0) { h += y; y = 0; }
                    if (x + w > _bitmap.PixelWidth) w = _bitmap.PixelWidth - x;
                    if (y + h > _bitmap.PixelHeight) h = _bitmap.PixelHeight - y;
                    if (w <= 0 || h <= 0) continue;

                    framebuffer.CopyRegionTo(_bitmap.BackBuffer, _bitmap.BackBufferStride, x, y, w, h);
                    _bitmap.AddDirtyRect(new Int32Rect(x, y, w, h));
                }
            }
            finally
            {
                _bitmap.Unlock();
            }
            InvalidateVisual();
        }
        catch (Exception)
        {
            // 帧缓冲区更新失败时忽略
        }
    }

    /// <summary>
    /// 自定义渲染
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (_bitmap == null)
        {
            // 没有图像时显示黑色背景
            dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));

            // 显示提示文字
            var text = new FormattedText(
                "等待连接...",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                14,
                Brushes.Gray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(
                text,
                new Point(
                    (RenderSize.Width - text.Width) / 2,
                    (RenderSize.Height - text.Height) / 2));
            return;
        }

        var rect = CalculateRenderRect();
        dc.DrawImage(_bitmap, rect);
    }

    /// <summary>
    /// 计算图像在控件中的渲染区域
    /// </summary>
    private Rect CalculateRenderRect()
    {
        if (_bitmap == null) return new Rect(RenderSize);

        switch (StretchMode)
        {
            case StretchMode.Original:
                double w = _bitmap.PixelWidth * ZoomLevel;
                double h = _bitmap.PixelHeight * ZoomLevel;
                return new Rect(
                    (RenderSize.Width - w) / 2,
                    (RenderSize.Height - h) / 2,
                    w, h);

            case StretchMode.Fit:
                double scaleX = RenderSize.Width / _bitmap.PixelWidth;
                double scaleY = RenderSize.Height / _bitmap.PixelHeight;
                double scale = Math.Min(scaleX, scaleY);
                double fitW = _bitmap.PixelWidth * scale;
                double fitH = _bitmap.PixelHeight * scale;
                return new Rect(
                    (RenderSize.Width - fitW) / 2,
                    (RenderSize.Height - fitH) / 2,
                    fitW, fitH);

            case StretchMode.Stretch:
                return new Rect(RenderSize);

            default:
                return new Rect(RenderSize);
        }
    }

    #region 鼠标事件处理

    /// <summary>
    /// 鼠标移动事件 - 转发给VNC服务器
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_client == null) return;

        var pos = e.GetPosition(this);
        var rect = CalculateRenderRect();

        var remotePos = MouseHandler.LocalToRemote(
            pos, rect,
            _client.FramebufferWidth,
            _client.FramebufferHeight);

        _client.SendPointerEvent(
            (int)remotePos.X,
            (int)remotePos.Y,
            _buttonMask);

        _lastMousePos = pos;
    }

    /// <summary>
    /// 鼠标按下事件
    /// </summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        _buttonMask |= MouseHandler.GetButtonMask(e.ChangedButton);
        CaptureMouse(); // 捕获鼠标：即使指针移出控件也能收到 MouseUp，避免按住状态残留
        OnMouseMove(e);
        Focus();
    }

    /// <summary>
    /// 鼠标释放事件
    /// </summary>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _buttonMask &= ~MouseHandler.GetButtonMask(e.ChangedButton);
        OnMouseMove(e);
        if (_buttonMask == 0) ReleaseMouseCapture();
    }

    /// <summary>
    /// 鼠标捕获丢失（如断连/窗口失活）时清零按钮掩码并通知服务器，避免远端"幽灵按住/拖拽"。
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_buttonMask != 0)
        {
            _buttonMask = 0;
            var rect = CalculateRenderRect();
            var remotePos = MouseHandler.LocalToRemote(
                _lastMousePos, rect,
                _client?.FramebufferWidth ?? 1,
                _client?.FramebufferHeight ?? 1);
            _client?.SendPointerEvent((int)remotePos.X, (int)remotePos.Y, 0);
        }
    }

    /// <summary>
    /// 鼠标滚轮事件。按档数（每 120 一档）发送对应次数的滚轮点击。
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_client == null || e.Delta == 0) return;

        int wheelBit = e.Delta > 0 ? 8 : 16; // 8=上滚, 16=下滚
        int notches = Math.Max(1, Math.Abs(e.Delta) / 120);

        var pos = e.GetPosition(this);
        var rect = CalculateRenderRect();
        var remotePos = MouseHandler.LocalToRemote(
            pos, rect,
            _client.FramebufferWidth,
            _client.FramebufferHeight);
        int x = (int)remotePos.X, y = (int)remotePos.Y;

        for (int i = 0; i < notches; i++)
        {
            _client.SendPointerEvent(x, y, _buttonMask | wheelBit);
            _client.SendPointerEvent(x, y, _buttonMask); // 释放滚轮
        }
    }

    #endregion

    #region 键盘事件处理

    /// <summary>
    /// 解析 WPF 报告的有效按键：Alt 组合时 e.Key 恒为 Key.System（真实键在 e.SystemKey）；
    /// IME 处理时真实键在 e.ImeProcessedKey。
    /// </summary>
    private static Key EffectiveKey(KeyEventArgs e)
    {
        if (e.Key == Key.System) return e.SystemKey;
        if (e.Key == Key.ImeProcessed) return e.ImeProcessedKey;
        return e.Key;
    }

    /// <summary>
    /// 键盘按下事件 - 转发给VNC服务器。
    /// 记录按下时实际发送的 keysym，松开时回放同一个，避免修饰键先松导致远端按键卡死。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = true;
        if (_client == null) return;

        Key key = EffectiveKey(e);
        uint keysym = KeyboardHandler.KeyToKeysym(key);
        if (keysym != 0)
        {
            _pressedKeys[key] = keysym; // 记录该物理键实际发送的 keysym
            _client.SendKeyEvent(keysym, true);
        }
    }

    /// <summary>
    /// 键盘释放事件 - 转发给VNC服务器。回放按下时记录的 keysym。
    /// </summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        e.Handled = true;
        if (_client == null) return;

        Key key = EffectiveKey(e);
        if (_pressedKeys.TryGetValue(key, out uint keysym))
        {
            _pressedKeys.Remove(key);
        }
        else
        {
            keysym = KeyboardHandler.KeyToKeysym(key); // 回退：未记录则按当前键重算
        }

        if (keysym != 0)
        {
            _client.SendKeyEvent(keysym, false);
        }
    }

    /// <summary>
    /// 释放所有按下的按键（焦点丢失/断连时清理），避免远端按键卡死、失控重复。
    /// </summary>
    private void ReleaseAllKeys()
    {
        if (_client == null) { _pressedKeys.Clear(); return; }

        // 释放所有跟踪到的按下键（含字母/方向/回车等，不只修饰键）
        foreach (uint keysym in _pressedKeys.Values)
        {
            _client.SendKeyEvent(keysym, false);
        }
        _pressedKeys.Clear();

        // 防御性再补发常见修饰键的释放
        _client.SendKeyEvent(0xFFE1, false); // Shift
        _client.SendKeyEvent(0xFFE2, false); // Shift_R
        _client.SendKeyEvent(0xFFE3, false); // Ctrl
        _client.SendKeyEvent(0xFFE4, false); // Ctrl_R
        _client.SendKeyEvent(0xFFE9, false); // Alt
        _client.SendKeyEvent(0xFFEA, false); // Alt_R
        _client.SendKeyEvent(0xFFEB, false); // Win/Command
        _client.SendKeyEvent(0xFFEC, false); // Win_R/Command_R
    }

    #endregion

    #region 依赖属性变更回调

    /// <summary>
    /// 缩放级别变更回调
    /// </summary>
    private static void OnZoomLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VncViewport viewport)
        {
            viewport.InvalidateVisual();
        }
    }

    /// <summary>
    /// 拉伸模式变更回调
    /// </summary>
    private static void OnStretchModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VncViewport viewport)
        {
            viewport.InvalidateVisual();
        }
    }

    #endregion
}

/// <summary>
/// 图像拉伸模式
/// </summary>
public enum StretchMode
{
    /// <summary>
    /// 原始尺寸
    /// </summary>
    Original,

    /// <summary>
    /// 适应窗口（保持比例）
    /// </summary>
    Fit,

    /// <summary>
    /// 拉伸填充
    /// </summary>
    Stretch
}
