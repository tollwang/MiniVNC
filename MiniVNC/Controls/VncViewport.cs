using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniVNC.Core;
using MiniVNC.Encodings;
using MiniVNC.Input;

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
    /// 更新帧缓冲区位图显示
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
                // 帧缓冲已是 BGRA32，直接按位图 stride 拷贝到后备缓冲
                framebuffer.CopyTo(_bitmap.BackBuffer, _bitmap.BackBufferStride);
                _bitmap.AddDirtyRect(new Int32Rect(
                    0, 0,
                    _bitmap.PixelWidth,
                    _bitmap.PixelHeight));
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
    }

    /// <summary>
    /// 鼠标滚轮事件
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_client == null) return;

        int mask = _buttonMask;
        mask |= MouseHandler.GetWheelMask(e.Delta);

        var pos = e.GetPosition(this);
        var rect = CalculateRenderRect();

        var remotePos = MouseHandler.LocalToRemote(
            pos, rect,
            _client.FramebufferWidth,
            _client.FramebufferHeight);

        _client.SendPointerEvent((int)remotePos.X, (int)remotePos.Y, mask);
        _client.SendPointerEvent((int)remotePos.X, (int)remotePos.Y, _buttonMask); // 释放滚轮
    }

    #endregion

    #region 键盘事件处理

    /// <summary>
    /// 键盘按下事件 - 转发给VNC服务器
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_client == null) return;

        uint keysym = KeyboardHandler.KeyToKeysym(e.Key);
        if (keysym != 0)
        {
            _client.SendKeyEvent(keysym, true);
        }

        e.Handled = true;
    }

    /// <summary>
    /// 键盘释放事件 - 转发给VNC服务器
    /// </summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (_client == null) return;

        uint keysym = KeyboardHandler.KeyToKeysym(e.Key);
        if (keysym != 0)
        {
            _client.SendKeyEvent(keysym, false);
        }

        e.Handled = true;
    }

    /// <summary>
    /// 释放所有按键（用于焦点丢失时清理）
    /// </summary>
    private void ReleaseAllKeys()
    {
        if (_client == null) return;

        // 释放所有修饰键
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
