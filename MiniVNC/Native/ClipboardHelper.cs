using System.Threading;
using System.Windows;

namespace MiniVNC.Native;

/// <summary>
/// Windows剪贴板操作辅助类 - 提供线程安全的剪贴板读写操作
/// </summary>
/// <remarks>
/// 剪贴板操作必须在STA线程上执行，此类封装了线程切换逻辑。
/// </remarks>
public static class ClipboardHelper
{
    /// <summary>
    /// 获取剪贴板文本（线程安全）
    /// </summary>
    /// <returns>剪贴板中的文本内容，获取失败返回null</returns>
    public static string? GetText()
    {
        string? text = null;

        // 确保剪贴板内容不是本应用设置的（避免循环同步）
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch
            {
                // 剪贴板访问可能失败，忽略错误
            }
        }
        else
        {
            // 在非STA线程上创建STA线程执行剪贴板操作
            Thread staThread = new(() =>
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText();
                    }
                }
                catch
                {
                    // 剪贴板访问可能失败，忽略错误
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(1000); // 最多等待1秒
        }

        return text;
    }

    /// <summary>
    /// 设置剪贴板文本（线程安全）
    /// </summary>
    /// <param name="text">要写入的文本内容</param>
    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // 剪贴板访问可能失败，忽略错误
            }
        }
        else
        {
            Thread staThread = new(() =>
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // 剪贴板访问可能失败，忽略错误
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(1000); // 最多等待1秒
        }
    }

    /// <summary>
    /// 尝试获取剪贴板文本
    /// </summary>
    /// <param name="text">输出文本内容</param>
    /// <returns>是否成功获取</returns>
    public static bool TryGetText(out string? text)
    {
        text = GetText();
        return text != null;
    }

    /// <summary>
    /// 检查剪贴板是否包含文本
    /// </summary>
    /// <returns>是否包含文本</returns>
    public static bool ContainsText()
    {
        bool result = false;

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try
            {
                result = Clipboard.ContainsText();
            }
            catch
            {
                // 忽略错误
            }
        }
        else
        {
            Thread staThread = new(() =>
            {
                try
                {
                    result = Clipboard.ContainsText();
                }
                catch
                {
                    // 忽略错误
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(1000);
        }

        return result;
    }
}
