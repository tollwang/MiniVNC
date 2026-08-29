using System.Threading;
using System.Windows;

namespace MiniVNC.Native;

/// <summary>
/// Windows剪贴板操作辅助类 - 提供线程安全的剪贴板读写操作
/// </summary>
/// <remarks>
/// 剪贴板 API 必须在 STA 线程上调用。本类的调用方都在 WPF UI 线程（本身即 STA），
/// 少数非 STA 的情况统一转交应用的 Dispatcher 执行——比每次现开一个 STA 线程更省、也更可靠
/// （新线程与本进程主 STA 不共享剪贴板所有权，取回的结果未必是调用方想要的）。
/// </remarks>
public static class ClipboardHelper
{
    /// <summary>
    /// 获取剪贴板文本（线程安全）
    /// </summary>
    /// <returns>剪贴板中的文本内容，获取失败返回null</returns>
    public static string? GetText()
        => Run(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <summary>
    /// 设置剪贴板文本（线程安全）
    /// </summary>
    /// <param name="text">要写入的文本内容</param>
    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Run<object?>(() => { Clipboard.SetText(text); return null; });
    }

    /// <summary>
    /// 检查剪贴板是否包含文本
    /// </summary>
    /// <returns>是否包含文本</returns>
    public static bool ContainsText() => Run(() => (bool?)Clipboard.ContainsText()) ?? false;

    /// <summary>
    /// 在 STA 线程上执行剪贴板操作。已在 STA 上则直接执行，否则转交 UI 线程。
    /// 剪贴板可能被别的进程占用而失败，一律吞掉异常返回默认值——同步失败不应影响远程会话。
    /// </summary>
    private static T? Run<T>(Func<T?> action)
    {
        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return action();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return default;
            return dispatcher.Invoke(() =>
            {
                try { return action(); }
                catch { return default; }
            });
        }
        catch
        {
            return default;
        }
    }
}
