using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniVNC.Core;

namespace MiniVNC;

/// <summary>
/// 主窗口 - 连接管理器界面
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 保存的连接列表
    /// </summary>
    private List<ConnectionSettings> _connections = new();

    /// <summary>
    /// 连接配置文件路径
    /// </summary>
    private readonly string _configPath;

    /// <summary>
    /// 构造函数
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiniVNC",
            "connections.json");
        LoadConnections();
    }

    /// <summary>
    /// 从配置文件加载连接列表
    /// </summary>
    private void LoadConnections()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                _connections = JsonSerializer.Deserialize<List<ConnectionSettings>>(json, options) ?? new List<ConnectionSettings>();
            }
        }
        catch (Exception ex)
        {
            _connections = new List<ConnectionSettings>();
            UpdateStatus($"加载配置失败: {ex.Message}");
        }

        RefreshConnectionList();
    }

    /// <summary>
    /// 保存连接列表到配置文件
    /// </summary>
    private void SaveConnections()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(_connections, options);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            UpdateStatus($"保存配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 刷新连接列表显示
    /// </summary>
    private void RefreshConnectionList()
    {
        LvConnections.ItemsSource = null;
        LvConnections.ItemsSource = _connections;
    }

    /// <summary>
    /// 更新状态栏文本
    /// </summary>
    /// <param name="message">状态消息</param>
    private void UpdateStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TbStatus.Text = message;
        });
    }

    /// <summary>
    /// 新增连接按钮点击事件
    /// </summary>
    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog();
        if (dialog.ShowDialog() == true)
        {
            _connections.Add(dialog.Settings);
            SaveConnections();
            RefreshConnectionList();
            UpdateStatus($"已添加连接: {dialog.Settings.Name}");
        }
    }

    /// <summary>
    /// 编辑连接按钮点击事件
    /// </summary>
    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (LvConnections.SelectedItem is not ConnectionSettings settings)
        {
            MessageBox.Show("请先选择一个连接", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ConnectionDialog(settings);
        if (dialog.ShowDialog() == true)
        {
            var index = _connections.FindIndex(c => c.Id == settings.Id);
            if (index >= 0)
            {
                _connections[index] = dialog.Settings;
                SaveConnections();
                RefreshConnectionList();
                UpdateStatus($"已更新连接: {dialog.Settings.Name}");
            }
        }
    }

    /// <summary>
    /// 删除连接按钮点击事件
    /// </summary>
    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LvConnections.SelectedItem is not ConnectionSettings settings)
        {
            MessageBox.Show("请先选择一个连接", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除连接 \"{settings.Name}\" 吗？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _connections.Remove(settings);
            SaveConnections();
            RefreshConnectionList();
            UpdateStatus($"已删除连接: {settings.Name}");
        }
    }

    /// <summary>
    /// 连接按钮点击事件
    /// </summary>
    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (LvConnections.SelectedItem is ConnectionSettings settings)
        {
            StartSession(settings);
        }
        else
        {
            MessageBox.Show("请先选择一个连接", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// 快速连接按钮点击事件
    /// </summary>
    private void BtnQuickConnect_Click(object sender, RoutedEventArgs e)
    {
        var host = TbQuickHost.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            MessageBox.Show("请输入主机地址", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 验证IP地址或主机名格式
        if (!IsValidHost(host))
        {
            MessageBox.Show("请输入有效的主机地址或IP", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TbQuickPort.Text, out int port) || port <= 0 || port > 65535)
        {
            port = 5900;
        }

        var settings = new ConnectionSettings
        {
            Name = $"{host}:{port}",
            Host = host,
            Port = port
        };

        StartSession(settings);
    }

    /// <summary>
    /// 验证主机地址或IP格式
    /// </summary>
    /// <param name="host">主机地址</param>
    /// <returns>是否有效</returns>
    private static bool IsValidHost(string host)
    {
        // 允许IP地址或主机名
        if (System.Net.IPAddress.TryParse(host, out _)) return true;
        // 简单的主机名验证
        return host.Length > 0 && host.Length < 256 && !host.Contains(' ');
    }

    /// <summary>
    /// 列表双击事件 - 启动远程会话
    /// </summary>
    private void ListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LvConnections.SelectedItem is ConnectionSettings settings)
        {
            StartSession(settings);
        }
    }

    /// <summary>
    /// 启动远程会话窗口
    /// </summary>
    /// <param name="settings">连接设置</param>
    private void StartSession(ConnectionSettings settings)
    {
        try
        {
            var sessionWindow = new RemoteSessionWindow(settings);
            sessionWindow.Show();
            this.Hide();
            sessionWindow.Closed += (s, e) =>
            {
                this.Show();
                this.Activate();
                UpdateStatus("会话已结束");
            };
            UpdateStatus($"正在连接 {settings.Host}:{settings.Port}...");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动会话失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>
/// 连接编辑对话框
/// </summary>
public class ConnectionDialog : Window
{
    private readonly ConnectionSettings _settings;
    private TextBox? _tbName;
    private TextBox? _tbHost;
    private TextBox? _tbPort;
    private PasswordBox? _pbPassword;

    /// <summary>
    /// 编辑后的连接设置
    /// </summary>
    public ConnectionSettings Settings => _settings;

    /// <summary>
    /// 构造函数 - 新建连接
    /// </summary>
    public ConnectionDialog()
    {
        _settings = new ConnectionSettings();
        InitializeDialog();
    }

    /// <summary>
    /// 构造函数 - 编辑现有连接
    /// </summary>
    /// <param name="settings">现有连接设置</param>
    public ConnectionDialog(ConnectionSettings settings)
    {
        _settings = new ConnectionSettings
        {
            Id = settings.Id,
            Name = settings.Name,
            Host = settings.Host,
            Port = settings.Port,
            Password = settings.Password
        };
        InitializeDialog();
    }

    /// <summary>
    /// 初始化对话框界面
    /// </summary>
    private void InitializeDialog()
    {
        Title = "连接设置";
        Width = 400;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current.MainWindow;

        var grid = new Grid { Margin = new Thickness(16) };
        for (int i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        AddTextField(grid, row++, "名称:", ref _tbName, _settings.Name);
        AddTextField(grid, row++, "主机:", ref _tbHost, _settings.Host);
        AddTextField(grid, row++, "端口:", ref _tbPort, _settings.Port.ToString());
        AddPasswordField(grid, row++, "密码:", ref _pbPassword, _settings.Password ?? "");

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(btnPanel, row++);

        var btnOk = new Button
        {
            Content = "确定",
            Width = 80,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0)
        };
        btnOk.Click += OnOkClick;

        var btnCancel = new Button
        {
            Content = "取消",
            Width = 80,
            Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderThickness = new Thickness(0)
        };
        btnCancel.Click += OnCancelClick;

        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        grid.Children.Add(btnPanel);

        Content = grid;
    }

    /// <summary>
    /// 添加文本表单字段
    /// </summary>
    private void AddTextField(Grid grid, int row, string label, ref TextBox? textBox, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(panel, row);

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        textBox = new TextBox
        {
            Text = value,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Padding = new Thickness(6, 4, 6, 4),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
        };

        panel.Children.Add(textBox);
        grid.Children.Add(panel);
    }

    /// <summary>
    /// 添加密码表单字段
    /// </summary>
    private void AddPasswordField(Grid grid, int row, string label, ref PasswordBox? passwordBox, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(panel, row);

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        passwordBox = new PasswordBox
        {
            Password = value,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Padding = new Thickness(6, 4, 6, 4),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
        };

        panel.Children.Add(passwordBox);
        grid.Children.Add(panel);
    }

    /// <summary>
    /// 确定按钮点击
    /// </summary>
    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tbName?.Text))
        {
            MessageBox.Show("请输入连接名称", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_tbHost?.Text))
        {
            MessageBox.Show("请输入主机地址", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.Name = _tbName?.Text?.Trim() ?? "";
        _settings.Host = _tbHost?.Text?.Trim() ?? "";
        _settings.Port = int.TryParse(_tbPort?.Text, out var p) ? p : 5900;
        _settings.Password = _pbPassword?.Password;

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 取消按钮点击
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
