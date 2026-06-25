using System.Linq;
using System.Windows.Input;
using MiniVNC.Core;

namespace MiniVNC.Input;

/// <summary>
/// 键盘输入处理辅助类 - 将WPF Key转换为X11 Keysym
/// </summary>
public static class KeyboardHandler
{
    /// <summary>
    /// 将WPF Key转换为X11 Keysym（用于VNC协议）
    /// </summary>
    /// <param name="key">WPF按键</param>
    /// <returns>X11 Keysym值</returns>
    public static uint KeyToKeysym(Key key)
    {
        // 字母键（A-Z），根据Shift状态返回大小写
        if (key >= Key.A && key <= Key.Z)
        {
            bool isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            return isShiftPressed
                ? (uint)(key - Key.A + 'A')   // Shift按下返回大写
                : (uint)(key - Key.A + 'a');  // 否则返回小写
        }

        // 数字键（0-9），根据Shift状态返回对应符号
        if (key >= Key.D0 && key <= Key.D9)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return key switch
                {
                    Key.D0 => 0x0029, // )
                    Key.D1 => 0x0021, // !
                    Key.D2 => 0x0040, // @
                    Key.D3 => 0x0023, // #
                    Key.D4 => 0x0024, // $
                    Key.D5 => 0x0025, // %
                    Key.D6 => 0x005E, // ^
                    Key.D7 => 0x0026, // &
                    Key.D8 => 0x002A, // *
                    Key.D9 => 0x0028, // (
                    _ => (uint)(key - Key.D0 + '0')
                };
            }
            return (uint)(key - Key.D0 + '0');
        }

        // 数字键盘
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return (uint)(key - Key.NumPad0 + 0xFFB0);

        return key switch
        {
            // 空格和基本标点
            Key.Space => 0x0020,
            Key.Enter => 0xFF0D,   // 注: Key.Enter 与 Key.Return 在 WPF 中为同一枚举值
            Key.Escape => 0xFF1B,
            Key.Back => 0xFF08,
            Key.Tab => 0xFF09,

            // 修饰键
            Key.LeftShift => 0xFFE1,
            Key.RightShift => 0xFFE2,
            Key.LeftCtrl => 0xFFE3,
            Key.RightCtrl => 0xFFE4,
            Key.LeftAlt => 0xFFE9,
            Key.RightAlt => 0xFFEA,
            Key.LWin => 0xFFEB,  // Windows/Command键映射
            Key.RWin => 0xFFEC,

            // 方向键
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,

            // 编辑键
            Key.Delete => 0xFFFF,
            Key.Insert => 0xFF63,
            Key.Home => 0xFF50,
            Key.End => 0xFF57,
            Key.PageUp => 0xFF55,
            Key.PageDown => 0xFF56,

            // 锁定键
            Key.CapsLock => 0xFFE5,
            Key.NumLock => 0xFF7F,
            Key.Scroll => 0xFF14,

            // 功能键
            Key.F1 => 0xFFBE,
            Key.F2 => 0xFFBF,
            Key.F3 => 0xFFC0,
            Key.F4 => 0xFFC1,
            Key.F5 => 0xFFC2,
            Key.F6 => 0xFFC3,
            Key.F7 => 0xFFC4,
            Key.F8 => 0xFFC5,
            Key.F9 => 0xFFC6,
            Key.F10 => 0xFFC7,
            Key.F11 => 0xFFC8,
            Key.F12 => 0xFFC9,

            // 标点符号（根据Shift状态返回对应符号）
            Key.OemMinus => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x005Fu : 0x002Du,          // _ / -
            Key.OemPlus => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x002Bu : 0x003Du,            // + / =
            Key.OemOpenBrackets => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x007Bu : 0x005Bu,    // { / [
            Key.OemCloseBrackets => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x007Du : 0x005Du,   // } / ]
            Key.OemPipe => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x007Cu : 0x005Cu,            // | / \
            Key.OemSemicolon => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x003Au : 0x003Bu,       // : / ;
            Key.OemQuotes => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x0022u : 0x0027u,          // " / '
            Key.OemComma => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x003Cu : 0x002Cu,           // < / ,
            Key.OemPeriod => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x003Eu : 0x002Eu,          // > / .
            Key.OemQuestion => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x003Fu : 0x002Fu,        // ? / /
            Key.OemTilde => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x007Eu : 0x0060u,           // ~ / `

            // 数字键盘运算键
            Key.Add => 0xFFAB,
            Key.Subtract => 0xFFAD,
            Key.Multiply => 0xFFAA,
            Key.Divide => 0xFFAF,
            Key.Decimal => 0xFFAE,

            _ => 0
        };
    }

    /// <summary>
    /// 发送组合键（如Ctrl+C）
    /// </summary>
    /// <param name="client">VNC客户端</param>
    /// <param name="keysyms">按键序列</param>
    public static void SendKeyCombo(VncClient client, params uint[] keysyms)
    {
        if (keysyms == null || keysyms.Length == 0) return;

        // 依次按下所有键
        foreach (var key in keysyms)
        {
            client.SendKeyEvent(key, true);
        }

        // 逆序释放所有键
        foreach (var key in keysyms.Reverse())
        {
            client.SendKeyEvent(key, false);
        }
    }

    /// <summary>
    /// 发送带修饰键的组合键
    /// </summary>
    /// <param name="client">VNC客户端</param>
    /// <param name="modifierKeysyms">修饰键Keysyms</param>
    /// <param name="mainKeysym">主键Keysym</param>
    public static void SendModifiedKey(VncClient client, uint[] modifierKeysyms, uint mainKeysym)
    {
        // 按下修饰键
        foreach (var mod in modifierKeysyms)
        {
            client.SendKeyEvent(mod, true);
        }

        // 按下并释放主键
        client.SendKeyEvent(mainKeysym, true);
        client.SendKeyEvent(mainKeysym, false);

        // 释放修饰键
        foreach (var mod in modifierKeysyms.Reverse())
        {
            client.SendKeyEvent(mod, false);
        }
    }

    /// <summary>
    /// 发送文本字符串（逐字符发送）
    /// </summary>
    /// <param name="client">VNC客户端</param>
    /// <param name="text">要发送的文本</param>
    public static void SendText(VncClient client, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (char c in text)
        {
            uint keysym = CharToKeysym(c);
            if (keysym != 0)
            {
                client.SendKeyEvent(keysym, true);
                client.SendKeyEvent(keysym, false);
            }
        }
    }

    /// <summary>
    /// 将字符转换为X11 Keysym
    /// </summary>
    /// <param name="c">字符</param>
    /// <returns>X11 Keysym值</returns>
    private static uint CharToKeysym(char c)
    {
        // ASCII字符直接返回
        if (c < 128)
        {
            return c;
        }

        // 扩展ASCII和Unicode映射
        return c switch
        {
            '\n' => 0xFF0D,  // Enter
            '\t' => 0xFF09,  // Tab
            '\r' => 0xFF0D,  // Enter
            _ => (uint)c
        };
    }
}
