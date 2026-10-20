[简体中文](README.md) | **English**

# Device Debug Tools | Serial, Modbus, PLC & MQTT Debugging Toolbox

**Device Debug Tools** (设备联调工具) is a desktop communication workbench for embedded, industrial and IoT development. Serial, TCP/UDP, Modbus, PLC, MQTT, device configuration and local screenshot/OCR tools share a light-themed workspace. Both editions remain at **1.0**.

| Edition | Stack | Guide |
| --- | --- | --- |
| Windows | .NET 10 / WPF | [Windows guide](windows/README.en.md) |
| Ubuntu / cross-platform | Python 3.10–3.13 / PySide6 Qt 6 | [Python Qt guide](linux_ubuntu/README.en.md); also runnable and tested on Windows |

![Device Debug Tools · Python Qt workspace](linux_ubuntu/docs/images/home.png)

## Features

- Serial / RS485 with HEX, UTF-8 and ASCII, periodic sending and checksums.
- TCP clients, multi-client servers, UDP, echo and fixed replies.
- Modbus RTU/TCP master reads, writes, polling and shared-memory slave simulation.
- Siemens S7, Mitsubishi MC 3E and Omron FINS/TCP clients, monitoring and simulated servers.
- MQTT 3.1.1/5, QoS, subscriptions, publishing, retained messages, wills, reconnection and templates.
- ZLAN device discovery, network/serial parameters, templates and verified write/readback.
- Frame generation, sending through existing connections, traffic inspection, conversion and CSV/JSON export.
- System settings, background startup, tray, F1 capture, colored annotations, F3 pins and local OCR.

## Repository layout

```text
.gitignore                # shared repository exclusions
LICENSE                   # MIT license for project-owned code
README.md / README.en.md
windows/
  src/ tests/ scripts/
  docs/                   # Windows guides and screenshots
  artifacts/              # generated releases and verification, ignored
linux_ubuntu/
  main.py
  src/linklab/ tests/ scripts/
  docs/                   # Python Qt guides and screenshots
  .venv/ artifacts/       # local dependencies and output, ignored
scripts/package-source.py # source archive for both editions
```

## Getting started

Windows portable releases live in `windows/artifacts/`. Extract the complete directory and launch `LinkLab.exe`. See the [Windows guide](windows/README.en.md) for source builds. After moving the application, update an existing startup entry from System settings.

From the repository root on Ubuntu:

```bash
bash linux_ubuntu/scripts/setup.sh
bash linux_ubuntu/scripts/run.sh
```

See [Ubuntu setup](linux_ubuntu/README.en.md#setup-and-run) for system libraries, serial permissions and OCR languages. On Windows, set up dependencies using `linux_ubuntu/scripts/setup.ps1` in PowerShell, then double-click `linux_ubuntu/scripts/run.bat`.

The Python edition passed Windows loopback, Qt UI and cleanup checks; **Ubuntu hardware/desktop validation is pending**. X11 is preferred for capture/global shortcuts; Wayland limitations are shown explicitly. Real PLC/serial timing, ZLAN hardware and MQTT TLS/WS/WSS endpoints still require device/environment validation. Virtual serial remains hidden.

The two editions provide corresponding modules and workflows; configuration, templates and PLC snapshots are not guaranteed to be interchangeable.

## License

Project-owned code is licensed under the [MIT License](LICENSE). Third-party dependencies, models and runtimes remain subject to their respective licenses; see the [Windows notices](windows/THIRD-PARTY-NOTICES.md) / [Python Qt notices](linux_ubuntu/THIRD-PARTY-NOTICES.md).
