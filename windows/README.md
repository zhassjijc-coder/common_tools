**简体中文** | [English](README.en.md)

# 设备联调工具｜串口、Modbus、PLC、MQTT 调试工具箱

**设备联调工具**的 Windows 版，面向上位机、嵌入式和物联网开发。将串口、网络、Modbus、PLC、MQTT 和截图 OCR 集中在一个桌面工作台，减少调试时来回切换工具。

基于 **.NET 10 + WPF + MVVM**，默认白色主题，支持深色切换。当前版本 **1.0**，应用界面为中文。

这是仓库的 `windows/` 子项目。[返回仓库首页](../README.md) · [Ubuntu / Python Qt 版](../linux_ubuntu/README.md)。文档位于 `windows/docs/`，发布和验证输出位于 `windows/artifacts/`；Git 忽略规则统一放在仓库根目录 `.gitignore`。

![设备联调工具 · Windows 白色主题工作台](docs/images/home.png)

## 功能

| 模块 | 主要功能 |
| --- | --- |
| 串口 / RS485 | COM 参数配置，HEX / UTF-8 / ASCII 收发，定时发送，CRC16-Modbus、CRC32、SUM、XOR、LRC 校验 |
| TCP / UDP | TCP 客户端与多客户端服务端，UDP 收发，回显与固定回复 |
| 网口配置软件 | 卓岚 ZLAN 设备广播发现、按 IP 查询，网络与串口参数编辑、模板导入导出，写入后回读确认 |
| Modbus 主站 | RTU / TCP，功能码 01/02/03/04/05/06/0F/10，寄存器读写与轮询，字节序转换 |
| 设备模拟 | Modbus TCP 服务端、RTU 从站，线圈与寄存器内存编辑 |
| 报文工作台 | 按地址、功能码和值生成报文，自定义 HEX，通过已建立的串口或网络连接发送，保存模板 |
| 监视与工具 | 收发时间线、筛选、报文解析、校验计算、进制转换，CSV / JSON 导出 |
| PLC 调试 | 西门子 S7 TCP、三菱 MC 3E 二进制、欧姆龙 FINS/TCP；客户端读写、连续监视、服务器模拟及内存快照 |
| MQTT 调试 | MQTT 3.1.1 / 5.0，TCP / TLS / WS / WSS，QoS 0/1/2，发布订阅、保留消息、遗嘱、重连、配置模板 |
| 系统配置 | 开机后台自启动、截图偏好、系统托盘；F1 移动/调整选区、缩放、彩色文字与矩形/正方形标注，F3 缩放贴图，中英文离线 OCR |

<details>
<summary>更多截图：PLC / MQTT / 网口配置 / 系统配置</summary>

**PLC 调试**

![设备联调工具 · PLC 客户端配置与读写](docs/images/plc.png)

**MQTT 调试**

![设备联调工具 · MQTT 连接、订阅与发布](docs/images/mqtt.png)

**系统配置、截图与离线 OCR**

![设备联调工具 · 系统配置与截图设置](docs/images/quick-tools.png)

![设备联调工具 · 截图选区移动、缩放与彩色标注](docs/images/capture-editor.png)

**网口配置软件**

![设备联调工具 · 网口设备发现与参数配置](docs/images/network-config.png)

</details>

## 前置环境

| 场景 | 要求 |
| --- | --- |
| 运行便携版 | Windows 10 / 11 x64；自包含发布包已带 .NET 运行时，无需另装 .NET |
| 使用 OCR | [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)；中英文识别模型已随项目提供，无需在线下载 |
| 使用串口 / RS485 | 可用的 COM 设备及对应 USB 转串口 / 485 驱动 |
| 从源码开发 | Windows、[.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)；项目脚本使用 PowerShell 7+，首次还原需要访问 NuGet |

## 快速开始

**便携版：** 解压 `LinkLab-<版本>-win-x64.zip`，进入文件夹运行 `LinkLab.exe`。请保留完整目录，不要只复制 EXE。

**Visual Studio 开发：** 推荐使用最新稳定版 **Visual Studio 2026**。

1. 在 Visual Studio Installer 中安装 **“.NET 桌面开发”** 工作负载，并确认已安装 **.NET 10 SDK**；仅安装运行时不能编译项目。
2. 打开仓库中的 `windows/LinkLab.sln`，在解决方案资源管理器中将 **LinkLab.App** 设为启动项目。
3. 等待 NuGet 包恢复完成，或右键解决方案选择“还原 NuGet 程序包”，然后按 **F5** 调试，或 **Ctrl+F5** 直接运行。

当前 WPF 项目目标为 `net10.0-windows`，**Visual Studio 2022 不支持当前目标**，本项目不提供 VS2022 兼容构建。版本对应关系见 [Microsoft 官方说明](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs)。

项目加载或恢复失败时，先查看“视图 → 输出”中的生成 / NuGet 错误，核对 VS 版本、SDK 和桌面开发工作负载。在 `windows/` 目录执行 `dotnet --list-sdks` 和 `dotnet --version` 可检查 SDK。项目的 `global.json` 指定 `10.0.100`、`rollForward: latestFeature`，允许使用已安装的更新 .NET 10 SDK 功能带；请保留该文件和当前目标框架，不通过删除 SDK 配置或降级目标绕过错误。SDK 选择规则见 [global.json 文档](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)。

**源码运行：** 先进入 `windows/` 项目目录，再执行：

```powershell
dotnet restore LinkLab.sln
dotnet build LinkLab.sln -c Release -m:1
dotnet run --project src/LinkLab.App -c Release --no-build
```

测试和生成 Windows x64 便携包：

```powershell
.\scripts\build.ps1 -RunTests
.\scripts\publish.ps1
```

发布结果在 `artifacts/`。无硬件时，可进入 **PLC 调试 → 本机联调**，体验客户端与模拟服务器读写。

## 使用说明

- PLC 客户端设置地址与端口后，点击“连接 PLC”检查通信；S7/FINS 验证协议握手，MC 3E 显示 TCP 连通状态。失败会在连接区域显示原因，可调整超时后重试。连接检查不读写 PLC 内存，首次直接读写仍可按需连接。
- PLC 模拟器提供通信与内存读写，不执行梯形图。S7-1200/1500 需配置 PUT/GET 和非优化 DB；具体型号、固件及串口时序仍需实机验证。
- MQTT 调试需要外部 Broker。配置模板不保存连接密码或客户端证书密码。
- 网口配置支持卓岚公开 UDP 管理协议，不是通用的所有厂商设备配置工具。点击搜索才发送查询，写入需要核对目标并确认；协议和使用范围见 [网口配置说明](docs/network-config.md)。
- 虚拟串口模块暂未开放；当前版本无需安装 com0com。普通串口 / RS485 与 Modbus RTU 可使用已有的 COM 设备。
- “系统配置”固定在侧栏底部，可独立设置“开机自启动”和“启动时默认启用截图”，首次均关闭，修改立即保存。自启动在登录 Windows 后静默驻留托盘；双击托盘图标恢复界面，右键可退出。升级或移动程序后，可将启动项更新为当前版本。
- 截图当前开关与下次启动偏好独立；F1/F3 被占用时会提示。OCR 在本机执行，不上传图片。后台运行、升级和启动项说明见 [系统配置](docs/system-settings.md)。
- 参数保存在 `%LOCALAPPDATA%\LinkLab\settings.json`，启动时不自动连接设备或发送数据。

## 主要依赖

PLC 使用 [IoTClient](https://github.com/zhaopeiym/IoTClient)，MQTT 使用 [MQTTnet](https://github.com/dotnet/MQTTnet)，OCR 使用 [Tesseract .NET](https://github.com/charlesw/tesseract) 与离线模型。项目不引用 HslCommunication DLL。

## 许可证

项目自有代码采用 [MIT 许可证](LICENSE)。第三方依赖、模型和运行库仍遵守各自许可证，详见 [第三方说明](THIRD-PARTY-NOTICES.md) 和 [licenses/](licenses/)。
