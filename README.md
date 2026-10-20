**简体中文** | [English](README.en.md)

# LinkLab · 连接实验室

面向上位机、嵌入式与物联网开发的桌面调试工具。串口、TCP/UDP、Modbus、PLC、MQTT、网口配置与截图 OCR 集中在一个白色主题工作台。当前版本统一为 **1.0**。

| 版本 | 技术 | 入口与说明 |
| --- | --- | --- |
| Windows | .NET 10 / WPF | [Windows 使用说明](windows/README.md) |
| Ubuntu / 跨平台 | Python 3.10–3.13 / PySide6 Qt 6 | [Python Qt 使用说明](linux_ubuntu/README.md)；可在 Windows 运行与测试 |

![Python Qt 白色主题工作台](linux_ubuntu/docs/images/home.png)

## 功能

- 串口 / RS485：参数配置、HEX / UTF-8 / ASCII 收发、定时发送、常用校验码。
- TCP / UDP：客户端、多客户端服务器、回显与固定回复。
- Modbus RTU / TCP：主站读写与轮询、从站模拟、线圈和寄存器内存编辑。
- PLC：西门子 S7、三菱 MC 3E、欧姆龙 FINS/TCP，客户端读写、连续监视与服务器模拟。
- MQTT：3.1.1 / 5.0、QoS、发布订阅、Retain、遗嘱、重连与模板。
- 网口配置：卓岚 ZLAN 公开协议设备发现、参数编辑、模板与写入后回读确认。
- 报文工作台与监视：按地址生成报文、通过现有连接发送、解析、校验、转换、CSV / JSON 导出。
- 系统配置：后台自启动、托盘、F1 截图、彩色标注、F3 贴图和本机 OCR。

## 目录

```text
.gitignore                # 全仓库共享忽略规则
LICENSE                   # 项目自有代码的 MIT 许可证
README.md / README.en.md  # 仓库入口
windows/                  # 原 Windows WPF 程序
  src/ tests/ scripts/
  docs/                   # Windows 文档与截图
  artifacts/              # 发布包、验证结果（忽略）
linux_ubuntu/             # Python + Qt 程序
  main.py
  src/linklab/ tests/ scripts/
  docs/                   # Qt 使用说明与截图
  .venv/ artifacts/       # 本机依赖和验证输出（忽略）
scripts/package-source.py # 打包两个平台源码
```

## 开始使用

Windows 便携包位于 `windows/artifacts/`，保留完整解压目录运行 `LinkLab.exe`。源码构建命令在 [Windows README](windows/README.md)。移动程序目录后，可在系统配置中更新已有启动项。

Ubuntu 在仓库根目录执行：

```bash
bash linux_ubuntu/scripts/setup.sh
bash linux_ubuntu/scripts/run.sh
```

首次准备的系统依赖、串口权限与 OCR 语言包见 [Ubuntu 安装步骤](linux_ubuntu/README.md#安装与运行)。Python 版可在 Windows 下使用 PowerShell 执行 `linux_ubuntu/scripts/setup.ps1` 配置依赖，然后双击 `linux_ubuntu/scripts/run.bat` 启动。

Python 版已在 Windows 完成协议回环、Qt 界面和关闭清理测试；**Ubuntu 实机验证待完成**。截图与全局热键优先使用 X11；Wayland 下会明确提示限制。真实 PLC、串口时序、ZLAN 硬件及 MQTT TLS/WS/WSS 端点仍需对应环境验证。虚拟串口模块继续隐藏。

两个版本的模块与主要操作对应，配置、模板和 PLC 内存快照不保证跨版本互换。

## 许可证

项目自有代码采用 [MIT 许可证](LICENSE)。第三方依赖、模型和运行库仍遵守各自许可证，详见 [Windows 第三方说明](windows/THIRD-PARTY-NOTICES.md) / [Python Qt 第三方说明](linux_ubuntu/THIRD-PARTY-NOTICES.md)。
