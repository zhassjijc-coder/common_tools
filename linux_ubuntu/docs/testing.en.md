[简体中文](testing.md) | **English**

# Compatibility and verification scope

The Python Qt edition has been verified on Windows x64 with Python 3.12 and PySide6 6.8.3. Ubuntu 22.04 / 24.04 are target platforms; native desktop and hardware verification remains pending.

| Feature | Verified | Environment-specific checks still required |
| --- | --- | --- |
| Serial / network | Multi-client TCP, UDP, serial loopback and a local RTU serial bridge | USB/RS485 drivers, electrical timing and physical serial devices |
| Modbus / PLC | Eight Modbus functions; S7, MC3E and FINS/TCP handshakes, reads/writes and memory simulation | Specific PLC models, firmware and device settings |
| MQTT | TCP 3.1.1/5.0, QoS0/1/2, publishing/subscriptions, retained messages and reconnect | Real TLS/WS/WSS endpoints; configuration and certificate/hostname checks are covered |
| Device configuration | Local ZLAN UDP mock discovery/read/write/readback | Hardware broadcast/reboot and Linux interface-directed broadcast |
| Capture / OCR | Windows editing, DPI mapping, pins, original-source OCR, shortcut conflicts and cleanup | Ubuntu X11 capture/shortcuts, Tesseract languages and desktop tray |

Global F1/F3 is unavailable under Wayland; desktop capture depends on system permissions. Use system capture and then pin its clipboard image or open it for OCR when necessary. Autostart and capture defaults initially remain off.

Device-configuration screenshots use demo data rather than hardware. Simulators provide protocol/memory access and do not execute PLC programs. The Windows WPF edition's results do not establish equivalent Python hardware validation. Configuration and snapshot formats are edition-specific.

See the [usage guide](../README.en.md#test-and-package) for test commands. Generated results remain in the local ignored `artifacts/` directory.
