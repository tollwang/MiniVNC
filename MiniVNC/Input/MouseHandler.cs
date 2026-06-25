using System;
using System.Windows;
using System.Windows.Input;

namespace MiniVNC.Input;

/// <summary>
/// 鼠标输入处理辅助类 - 将本地鼠标事件转换为远程屏幕坐标
/// </summary>
public static class MouseHandler
{
    /// <summary>
    /// 将本地鼠标位置转换为远程屏幕坐标
    /// </summary>
    /// <param name="localPos">本地鼠标位置</param>
    /// <param name="renderRect">渲染区域矩形</param>
    /// <param name="remoteWidth">远程屏幕宽度</param>
    /// <param name="remoteHeight">远程屏幕高度</param>
    /// <returns>远程屏幕坐标</returns>
    public static Point LocalToRemote(Point localPos, Rect renderRect, int remoteWidth, int remoteHeight)
    {
        if (renderRect.Width <= 0 || renderRect.Height <= 0 || remoteWidth <= 0 || remoteHeight <= 0)
        {
            return new Point(0, 0);
        }

        int x = (int)((localPos.X - renderRect.X) / renderRect.Width * remoteWidth);
        int y = (int)((localPos.Y - renderRect.Y) / renderRect.Height * remoteHeight);

        x = Math.Clamp(x, 0, remoteWidth - 1);
        y = Math.Clamp(y, 0, remoteHeight - 1);

        return new Point(x, y);
    }

    /// <summary>
    /// 获取VNC按钮掩码
    /// </summary>
    /// <param name="button">WPF鼠标按钮</param>
    /// <returns>VNC按钮掩码值</returns>
    public static int GetButtonMask(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => 1,
            MouseButton.Middle => 2,
            MouseButton.Right => 4,
            _ => 0
        };
    }

    /// <summary>
    /// 获取鼠标按钮事件的按钮掩码
    /// </summary>
    /// <param name="e">鼠标按钮事件参数</param>
    /// <returns>VNC按钮掩码值</returns>
    public static int GetButtonMask(MouseButtonEventArgs e)
    {
        return GetButtonMask(e.ChangedButton);
    }

    /// <summary>
    /// 获取滚轮按钮掩码
    /// </summary>
    /// <param name="delta">滚轮滚动量</param>
    /// <returns>VNC滚轮掩码值（8=向上，16=向下）</returns>
    public static int GetWheelMask(int delta) => delta > 0 ? 8 : 16;

    /// <summary>
    /// 组合按钮掩码
    /// </summary>
    /// <param name="left">左键是否按下</param>
    /// <param name="middle">中键是否按下</param>
    /// <param name="right">右键是否按下</param>
    /// <returns>组合按钮掩码</returns>
    public static int GetCombinedMask(bool left, bool middle, bool right)
    {
        int mask = 0;
        if (left) mask |= 1;
        if (middle) mask |= 2;
        if (right) mask |= 4;
        return mask;
    }
}
