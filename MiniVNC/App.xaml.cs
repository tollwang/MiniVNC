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

        // 处理未捕获异常
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show($"发生错误: {ex.Exception.Message}", "MiniVNC 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            MessageBox.Show($"发生致命错误: {ex.ExceptionObject}", "MiniVNC 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}
