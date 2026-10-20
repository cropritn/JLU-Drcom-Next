# JLU-Drcom-Next

Unofficial Windows client for the JLU Dr.COM campus network.  
厌倦了每次连校园网都要等某个软件启动吗？这里是JLU校园网的服务实现，无感使用校园网。

> **免责声明**：本项目非官方实现，与吉林大学及 Dr.COM 官方无关，仅供个人测试使用

## Tech Stack

项目使用 **C# / .NET** 编写，采用 服务 + GUI 分离的架构：

- `JluDrcom.Core`：核心类库。负责 Dr.COM 协议封包/解包、加解密、UDP 通信以及网卡路由选择。
- `JluDrcom.Service`：Windows 后台服务。静默运行（`LocalService` 权限），负责常驻心跳与断线重连。
- `JluDrcom.Setup-Gui`：WPF 界面。提供托盘图标状态、手动连接/断开以及网卡设置窗口（需管理员权限）。

## Key Features

- **网卡**：根据到认证服务器的路由表自动选择物理网卡，支持手动指定网卡。
- **安全**：密码使用 Windows **DPAPI (LocalMachine)** 加密后存储；日志严格脱敏，避免暴露明文密码、MD5A 或完整抓包。
- **保活**：登录失败采用指数退避重试；心跳失败自动重新登录；监听网络地址变化和睡眠唤醒事件，自动恢复连接。
- **配置**：修改配置后通过 `FileSystemWatcher` 热重载，无需重启服务。

## How to use

1. 去 [Releases](https://github.com/croptrin/JLU-Drcom-Next/releases) 页面下载最新的发布包。
2. 安装JluDrcomSetup.exe后打开`JluDrcom.Setup-Gui.exe`。（无须后台常驻，不会自启，修改配置后完全可关闭进程）
3. 在设置窗口中输入校园网账号和密码，点击保存并连接。
4. 托盘图标变绿代表连接成功。服务会自动安装并在后台保持网络畅通。（托盘仅作为GUI配置使用，可退出，服务会自动启动）


## Config & logs

- **配置文件**：`%ProgramData%\JluDrcom\config.json`
- **日志目录**：`%ProgramData%\JluDrcom\logs`（可在托盘右键“打开日志目录”快速访问）

JluDrcom.Setup-Gui.exe设置界面中的「扩展选项」可直接修改有线网卡的静态 IP、网关和 DNS 配置。

## License

[MIT License](LICENSE)。协议行为参考了 开源项目[Yesterday17/JLUDrcomService](https://github.com/Yesterday17/JLUDrcomService)，在此致谢。