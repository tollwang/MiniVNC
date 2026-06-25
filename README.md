# MiniVNC — Windows免安装VNC远程控制Mac Mini工具

## 简介

MiniVNC 是一款专为 Windows 平台开发的轻量级 VNC 客户端，针对 Mac Mini（macOS 屏幕共享）进行了深度优化。编译后为**单个可执行文件**，无需安装，双击即可运行。

## 功能特性

| 功能 | 说明 |
|------|------|
| 免安装运行 | 单文件自包含发布，运行时随程序打包，目标机无需预装 .NET |
| Mac Mini 优化 | 针对 macOS 屏幕共享协议深度适配 |
| 多连接管理 | 保存多个 Mac Mini 连接配置，快速切换 |
| 完整远程控制 | 鼠标、键盘全转发，支持滚轮和组合键 |
| 全屏/窗口模式 | 支持无边框全屏和窗口模式切换 |
| 自适应缩放 | 原始尺寸、适应窗口、拉伸填充三种模式 |
| 剪贴板同步 | Windows 与 Mac 之间双向文本剪贴板同步 |
| Mac 快捷键 | 支持发送 Cmd+Space、Cmd+Tab 等 Mac 专用快捷键 |
| 深色主题 | 现代化深色 UI，与 macOS 风格协调 |
| 自动重连 | 连接异常断开后自动恢复 |

## 系统要求

- **操作系统**: Windows 10 64位 (1809+) 或 Windows 11
- **架构**: x64 (AMD64)
- **内存**: 最低 128MB，推荐 512MB
- **网络**: 与 Mac Mini 在同一局域网，或可通过 IP 访问

## Mac Mini 端配置

### 1. 启用屏幕共享

1. 在 Mac Mini 上打开 **系统设置** → **通用** → **共享**
2. 开启 **屏幕共享**
3. 点击旁边的 **(i)** 信息按钮：
   - 选择 **"仅这些用户"**，添加允许访问的用户账户
   - 记下 Mac Mini 的 IP 地址（如 `192.168.1.100`）

### 2. 设置VNC密码

如果需要通过密码连接（非Apple ID）：

1. 在屏幕共享设置中点击 **"电脑设置..."**
2. 勾选 **"VNC 查看器可以使用密码控制屏幕"**
3. 设置一个密码（最多8个字符）

### 3. 获取 Mac Mini IP 地址

- 打开 **终端**，输入 `ipconfig getifaddr en0`（WiFi）或 `ipconfig getifaddr en1`（有线）
- 或在 **系统设置** → **网络** 中查看

## Windows 端使用

### 方法一：快速连接

1. 双击 `MiniVNC.exe` 运行
2. 在主界面底部输入 Mac Mini 的 IP 地址和端口（默认5900）
3. 点击 **连接**，输入 VNC 密码即可

### 方法二：保存连接配置

1. 点击工具栏 **新增** 按钮
2. 填写连接信息：
   - **名称**: 给这个连接起个名字（如 "客厅Mac Mini"）
   - **主机**: Mac Mini 的 IP 地址
   - **端口**: 默认 5900（屏幕共享默认端口）
   - **密码**: VNC 密码
3. 点击 **保存并连接**

### 远程会话控制

连接成功后进入远程会话窗口：

| 操作 | 说明 |
|------|------|
| 鼠标左键 | 等同于 Mac 鼠标左键 |
| 鼠标右键 | 等同于 Mac 鼠标右键（Control+点击） |
| 鼠标滚轮 | 页面上下滚动 |
| **Win键** | 映射为 **Command键 (Cmd)** |
| **Alt键** | 映射为 **Option键** |
| **Ctrl+Alt+F** | 切换全屏/窗口模式 |
| **Ctrl+Alt+D** | 断开连接 |
| **ESC** | 退出全屏模式 |

### 工具栏功能

将鼠标移到屏幕顶部显示悬浮工具栏：

- **窗口/全屏**: 切换显示模式
- **缩放**: 100%原始尺寸 / 适应窗口 / 拉伸填充
- **发送快捷键**: 发送 Mac 常用组合键
  - Cmd+Space (Spotlight)
  - Cmd+Tab (切换应用)
  - Cmd+C / Cmd+V (复制/粘贴)
  - Cmd+Q (退出应用)
  - 等等...
- **剪贴板**: 开启/关闭双向剪贴板同步
- **断开**: 断开当前连接

## 编译说明

### 环境要求

- Windows 10/11 64位
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) 或更高版本
- Visual Studio 2022 17.8+（可选）

### 编译为单文件

在项目根目录执行以下命令：

```bash
cd MiniVNC
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true
```

> 说明：WPF 目前不支持 Native AOT，因此采用「单文件 + 自包含」发布方式。
> 运行时随程序一起打包，目标机无需预装 .NET 运行时（产物体积相应较大）。

编译完成后，单文件可执行程序位于：

```
bin/Release/net9.0-windows/win-x64/publish/MiniVNC.exe
```

将此文件复制到任意位置即可直接运行，无需安装任何依赖。

### 使用 Visual Studio 编译

1. 打开 `MiniVNC/MiniVNC.csproj`
2. 选择 **Release** 配置和 **x64** 平台
3. 右键项目 → **发布**
4. 选择目标位置，点击 **完成**

## 项目结构

```
MiniVNC/
├── App.xaml / App.xaml.cs              # WPF应用入口 + 深色主题资源
├── MainWindow.xaml / .xaml.cs          # 主窗口（连接管理器）
├── RemoteSessionWindow.xaml / .xaml.cs # 远程会话窗口
├── MiniVNC.csproj                      # 项目文件（Native AOT配置）
├── app.manifest                        # DPI感知配置
├── Core/
│   ├── VncClient.cs                    # VNC客户端主控制器
│   └── ConnectionSettings.cs           # 连接配置模型
├── Protocol/
│   ├── Messages.cs                     # 协议消息结构体
│   ├── SecurityTypes.cs                # 安全类型枚举
│   └── RfbProtocol.cs                  # RFB协议状态机
├── Encodings/
│   ├── IEncoding.cs                    # 编码接口
│   ├── RawEncoding.cs                  # Raw编码
│   ├── HextileEncoding.cs              # Hextile编码（Mac默认）
│   ├── ZrleEncoding.cs                 # ZRLE编码
│   ├── CopyRectEncoding.cs             # CopyRect编码
│   └── Framebuffer.cs                  # 帧缓冲管理
├── Network/
│   └── VncStream.cs                    # TCP连接与大端序流
├── Input/
│   ├── MouseHandler.cs                 # 鼠标事件处理
│   └── KeyboardHandler.cs              # 键盘映射处理
├── Controls/
│   └── VncViewport.cs                  # VNC渲染画布控件
├── Utils/
│   ├── DesEncryptor.cs                 # DES加密（VNC认证）
│   ├── ByteExtensions.cs               # 字节操作扩展
│   └── BitmapUtils.cs                  # 位图工具
└── Native/
    └── ClipboardHelper.cs              # Windows剪贴板操作
```

## 技术规格

| 项目 | 规格 |
|------|------|
| 开发语言 | C# 11 |
| UI框架 | WPF (.NET 9) |
| RFB协议 | 3.3 / 3.7 / 3.8 |
| 支持编码 | Raw, Hextile, CopyRect（ZRLE 暂未启用） |
| 认证方式 | VNC密码认证 (DES Challenge-Response) |
| 发布模式 | 单文件 / 自包含 |
| DPI支持 | Per-Monitor V2 |

## 常见问题

### 连接失败怎么办？

1. **检查网络连通性**: 在Windows命令行执行 `ping [Mac IP]` 确认网络可达
2. **检查Mac防火墙**: 确保Mac的**系统设置** → **网络** → **防火墙** 未阻止屏幕共享
3. **检查端口**: 确认Mac Mini的屏幕共享端口为5900（默认）
4. **检查密码**: VNC密码最多8个字符，超长会被截断

### 画面卡顿怎么办？

1. 切换缩放模式为 **"适应窗口"** 降低显示分辨率
2. 确保Mac Mini和Windows电脑在同一局域网
3. 检查网络带宽，WiFi建议5GHz频段

### 键盘按键不响应？

1. 点击远程画面确保焦点在 VNC 窗口内
2. 检查是否开启了 **仅查看模式**

## 安全提示

- VNC密码认证使用DES加密，安全性有限
- 建议仅在受信任的局域网内使用
- 不要在公共网络中传输敏感信息
- 建议为屏幕共享设置独立密码（与Apple ID密码不同）

## 许可证

MIT License — 可自由使用、修改和分发。

---

**MiniVNC** — 让 Windows 控制 Mac Mini 变得简单。
