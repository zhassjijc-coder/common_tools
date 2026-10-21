**简体中文** | [English](README.en.md) · [仓库首页](../README.md)

# 设备联调工具｜串口、Modbus、PLC、MQTT 调试工具箱

**设备联调工具**的 Python Qt 版，面向 Ubuntu，使用 **Python + PySide6 / Qt Widgets** 实现，也可在 Windows 上运行。当前版本 **1.0**。默认白色主题，可切换深色；系统配置固定在侧栏底部。串口、Modbus 和 PLC 协议使用独立实现，MQTT 使用 Eclipse Paho；不依赖 HslCommunication 或付费厂家 DLL。

![设备联调工具 · Python Qt 工作台](docs/images/home.png)

## 功能

| 模块 | 内容 |
| --- | --- |
| 串口 / RS485 | 端口检测，波特率/数据位/校验/停止位，HEX/UTF-8/ASCII，CRC16-Modbus/CRC32/SUM/XOR/LRC，定时发送、回显、固定回复 |
| TCP / UDP | TCP 客户端、多客户端服务器、UDP，本地绑定、原始报文收发 |
| Modbus 主站 / 设备模拟 | RTU/TCP，FC01/02/03/04/05/06/0F/10，读写、持续轮询、线圈/寄存器内存、数值及字节序转换 |
| PLC 调试 | S7 TCP、MC 3E 二进制、FINS/TCP，常用数值与位地址、读写、连续监视、服务器模拟、本机联调、内存快照导入导出 |
| MQTT | 3.1.1/5.0，TCP/TLS/WS/WSS，QoS0/1/2，订阅确认、发布确认、Retain、遗嘱、自动重连、模板和消息导出 |
| 网口配置 | ZLAN UDP1092 发现/定向查询，网络及串口参数、模板、身份核对、写入确认与同设备回读 |
| 报文 / 监视工具 | 按地址生成 RTU/TCP 报文、自定义 HEX、经现有连接发送、收发筛选、CSV/JSON、协议解析及数值转换 |
| 系统配置 | 后台自启动、系统托盘、F1 选区/缩放/移动、彩色文字和矩形/正方形、撤销、F3 缩放贴图、本机 OCR |

虚拟串口继续隐藏；使用系统已提供的 `/dev/ttyUSB*`、`/dev/ttyACM*` 或 COM 设备。模拟器只模拟协议和内存，不执行梯形图。

<details>
<summary>界面展示</summary>

![设备联调工具 · Python Qt PLC 客户端](docs/images/plc.png)

![设备联调工具 · Python Qt PLC 服务器 / 模拟内存](docs/images/plc-server.png)

![设备联调工具 · Python Qt MQTT 调试](docs/images/mqtt.png)

![设备联调工具 · Python Qt 网口配置演示数据，未写设备](docs/images/network-config.png)

![设备联调工具 · Python Qt 截图编辑](docs/images/capture-editor.png)

</details>

## 安装与运行

建议 Ubuntu **22.04 / 24.04 x64**、Python **3.10–3.13**。当前实际验证环境为 Windows / Python 3.12，Ubuntu 请按下面步骤自行实测。

从仓库根目录执行，首次需要网络安装 Python 依赖：

```bash
sudo apt update
sudo apt install python3-venv python3-pip libxcb-cursor0 libxkbcommon-x11-0 libegl1 libopengl0 fonts-noto-cjk tesseract-ocr tesseract-ocr-eng tesseract-ocr-chi-sim
bash linux_ubuntu/scripts/setup.sh
bash linux_ubuntu/scripts/run.sh
```

需要访问串口时，将当前用户加入 `dialout` 组，然后注销并重新登录：

```bash
sudo usermod -aG dialout "$USER"
```

不需要以 root 身份运行整个应用。监听 102/502 等低端口可能需要系统授权；本机联调默认使用高端口或随机空闲端口。

Windows 源码测试使用 Python 3.10–3.13：

```powershell
.\linux_ubuntu\scripts\setup.ps1 -Python C:\path\to\python.exe
.\linux_ubuntu\scripts\run.bat
```

依赖已配置后，可直接双击 `scripts/run.bat`；在 CMD 中也可以输入 `run.bat`。`.ps1` 需要 PowerShell 执行，不能在 CMD 中直接当作程序启动。若窗口未打开，运行 `run.bat --debug` 查看错误；`run.bat --background` 后台启动。

也可以直接运行 `.venv/Scripts/python.exe main.py`（先进入 `linux_ubuntu/`）。Ubuntu 对应 `.venv/bin/python main.py`。`--background` 在托盘可用时后台启动；托盘不可用时保留窗口。再次启动会唤回同一用户、同一桌面会话内的 Qt 实例。

## 使用 PyCharm 开发

1. 安装 Python **3.10–3.13**（建议 3.12 x64），先按上方步骤安装系统依赖并执行 `setup.sh` 或 `setup.ps1`，生成 `linux_ubuntu/.venv`。
2. 在 PyCharm 中选择 **File → Open（文件 → 打开）**，打开仓库中的 **`linux_ubuntu/` 文件夹**，保留完整源码目录。
3. 在 **Settings → Python → Interpreter** 中添加本地已有解释器，选择此项目的 `.venv`，不要重复创建环境。旧版 PyCharm 的入口可能是 **Project → Python Interpreter**。解释器文件为：

   | 系统 | 路径（相对于 `linux_ubuntu/`） |
   | --- | --- |
   | Windows | `.venv\Scripts\python.exe` |
   | Ubuntu | `.venv/bin/python` |

4. 打开 **Run → Edit Configurations（运行 → 编辑配置）**，新增 **Python** 配置：

   | 配置项 | 值 |
   | --- | --- |
   | Script path / 脚本路径 | `$PROJECT_DIR$/main.py` |
   | Working directory / 工作目录 | `$PROJECT_DIR$`（即 `linux_ubuntu/`） |
   | Python interpreter / 解释器 | 上一步选定的项目 `.venv` |
   | Parameters / 参数 | 留空，正常显示主窗口 |

5. 点击 **Run** 启动或 **Debug** 断点调试。也可右键 `main.py` 运行，再核对上述配置。`main.py` 已处理 `src/` 导入路径，无需额外设置 `PYTHONPATH`。

若在 PyCharm 中打开的是整个仓库，脚本路径改为 `$PROJECT_DIR$/linux_ubuntu/main.py`，工作目录改为 `$PROJECT_DIR$/linux_ubuntu`，解释器仍选 `linux_ubuntu/.venv` 中的文件。

注意事项：

- 提示 `ModuleNotFoundError`（如 `PySide6`）时，检查运行配置的解释器；进入 `linux_ubuntu/`，用该解释器执行 `-m pip install -r requirements.txt`。本项目使用 **PySide6**，不是 PyQt6。
- 调试前通过托盘菜单退出已运行的 Qt 实例，否则新启动只会唤回旧实例。直接运行 `main.py`，不要把 `src/linklab/__main__.py` 当作独立脚本。
- Windows 与 Ubuntu 需分别创建 `.venv`，不能跨系统复制。Ubuntu 仍需安装上方列出的 Qt 系统库、OCR 语言包并配置串口权限；PyCharm 不会代为安装。
- `.idea/`、`.venv/` 和运行产物由根目录 `.gitignore` 排除，无需提交。

PyCharm 菜单参考官方的 [解释器配置](https://www.jetbrains.com/help/pycharm/configuring-python-interpreter.html) 与 [Python 运行配置](https://www.jetbrains.com/help/pycharm/run-debug-configuration-python.html)。

## 使用

- **界面操作**：下拉菜单显示当前选项，长文本可悬停查看；未聚焦的下拉框不会因页面滚动误切参数。表格支持拖动列宽，窄窗口可在表内横向滚动，状态通过颜色标签区分。
- **PLC 工作角色**：顶部下拉选择“客户端 / 监视”或“服务器 / 模拟内存”。服务端模式中设置监听 IP 和端口，点击“启动服务器”，再读写模拟内存；切回客户端可连接同一模拟器。切换角色不会自动启动通信，转到服务端时会停止客户端连续监视。
- **连接 PLC**：客户端点击“连接 PLC”后才检查真实连接，S7/FINS 验证协议握手，MC 3E 显示 TCP 连通状态。连接失败会在连接区域提示目标地址及原因，可调整超时后重试；连接检查不读写设备内存。修改连接参数会释放旧连接，也可直接执行一次读写并按需连接。
- **无硬件联调**：PLC 页面选择协议后点“本机联调”。Modbus 页面先启动本机 TCP 模拟，再让主站连接对应端口。TCP 服务器与 UDP 默认向现有目标发送，避免两个开启回显的端点相互循环。
- **截图**：系统配置中开启当前截图热键，F1 支持多屏及不同缩放比例下的跨屏框选，拖动选区或控制点，滚轮缩放、空格/中键平移；T 文字、R 矩形、S 正方形，颜色可切换。Windows 按屏幕原生像素导出，修改显示器布局会取消当前截图。复制/保存/贴图包含标注，OCR 识别原始选区。F3 贴图后滚轮缩放、Ctrl+滚轮调透明度。
- **桌面差异**：F1/F3 支持 Windows 和 X11，并检测占用；Wayland 下不绕过系统限制。全局键或屏幕读取不可用时，页面会提示，可使用系统截图，再从剪贴板贴图或打开图片进行 OCR。Ubuntu 托盘图标是否显示取决于桌面扩展。
- **OCR**：Ubuntu 使用本机 Tesseract 可执行文件及语言包；Windows 可使用本机安装的 Tesseract，或同仓库 Windows 版中的原生 DLL 与模型。图像不上传。
- **MQTT**：需要外部 Broker；TLS 使用系统信任链或指定 CA，双向认证用 PEM 证书/私钥，不导入 PFX。模板不保存连接密码或私钥密码。
- **网口配置**：仅适用卓岚公开协议；167 字节参数基线可写，其余可识别版本只读。写前核对 DevID/通道及原 IP，确认后设备可能重启；回读失败不自动重发写命令。程序不更改电脑网卡配置。
- **偏好**：Windows 为 `%LOCALAPPDATA%/LinkLabQt/`，Linux 为 `$XDG_CONFIG_HOME/LinkLabQt/`（默认 `~/.config/LinkLabQt/`），与 WPF 配置分开。启动不自动连接设备或发送报文；自启动和默认截图首次均关闭。

## 测试与源码打包

```bash
bash linux_ubuntu/scripts/test.sh
python3 scripts/package-source.py
```

Windows 使用 `linux_ubuntu/scripts/test.ps1`。输出位于 `linux_ubuntu/artifacts/`，综合源码包位于 `windows/artifacts/LinkLab-1.0-all-platforms-source.zip`。Qt 截图见 `docs/images/`；平台验证边界见 [测试记录](docs/testing.md)。

Git 忽略文件仅在仓库根目录，排除两端的构建结果、`.venv`、Python 缓存、测试日志、发布包、私密配置和本地 AI 文档；保留源码、公开说明、截图及许可证。

Qt 安装参考 [官方指南](https://doc.qt.io/qtforpython-6.8/gettingstarted.html)。

## 许可证

项目自有代码采用 [MIT 许可证](LICENSE)。第三方依赖、模型和运行库仍遵守各自许可证，详见 [第三方说明](THIRD-PARTY-NOTICES.md)。
