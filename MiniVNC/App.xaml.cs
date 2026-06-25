using System;
using System.IO;
using System.Windows;

namespace MiniVNC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 创建AppData目录
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiniVNC");
        Directory.CreateDirectory(appDataPath);

        string logPath = Path.Combine(appDataPath, "error.log");

        // 处理未捕获异常：先落盘日志（含堆栈）再提示，便于排查
        DispatcherUnhandledException += (s, ex) =>
        {
            LogException(logPath, "UI线程未处理异常", ex.Exception);
            MessageBox.Show($"发生错误: {ex.Exception.Message}\n\n详情已写入:\n{logPath}", "MiniVNC 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true; // UI 线程异常已记录并提示，保持应用存活
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            LogException(logPath, "致命未处理异常", ex.ExceptionObject as Exception);
            MessageBox.Show($"发生致命错误，详情已写入:\n{logPath}", "MiniVNC 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    /// <summary>将异常（含堆栈）追加写入日志文件，写入失败则静默忽略。</summary>
    private static void LogException(string logPath, string category, Exception? ex)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {category}: {ex}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch { /* 日志写入失败不应再抛出 */ }
    }
}
