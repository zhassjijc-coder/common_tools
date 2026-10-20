# Device Debug Tools · Third-party components (Python Qt)

The Python Qt edition of Device Debug Tools (设备联调工具) installs dependencies through pip; it does not bundle or modify their source. No HslCommunication, commercial PLC SDK or virtual serial driver is included.

| Component | Version / purpose | License and upstream |
| --- | --- | --- |
| PySide6 / Shiboken6 / Qt | 6.8.3, Qt Widgets desktop UI | LGPLv3/GPLv3/commercial alternatives; this application uses Qt Core/Gui/Widgets/Network through the official dynamic Python packages. [Qt for Python](https://doc.qt.io/qtforpython-6/), [licensing](https://doc.qt.io/qtforpython-6/licenses.html), [source](https://code.qt.io/cgit/pyside/pyside-setup.git/) |
| pySerial | 3.5, serial communication | BSD-3-Clause; [license/source](https://github.com/pyserial/pyserial/blob/v3.5/LICENSE.txt) |
| Eclipse Paho MQTT Python | 2.1.0, MQTT | EPL-2.0 / EDL-1.0 alternatives; [license/source](https://github.com/eclipse-paho/paho.mqtt.python/tree/v2.1.0) |
| python-xlib | 0.33, Linux X11 shortcuts | LGPL-2.1-or-later; [license/source](https://github.com/python-xlib/python-xlib) |
| six | transitive dependency of python-xlib | MIT; [source](https://github.com/benjaminp/six) |
| Tesseract | optional local executable; Windows DLL fallback | Apache-2.0; [engine source](https://github.com/tesseract-ocr/tesseract), [language models](https://github.com/tesseract-ocr/tessdata_fast) |

Ubuntu OCR libraries/language packages are installed by the user using the distribution package manager. When using the existing Windows engine/model fallback, retain that edition's [notices and licenses](../windows/THIRD-PARTY-NOTICES.md). Redistributing a runtime or a packaged binary requires retaining the applicable upstream notices and license terms; the combined source archive excludes `.venv` and other installed libraries.

PLC and Modbus framing is implemented independently in Python using the corresponding Windows source as the behavior reference. ZLAN configuration implements the [published UDP management protocol](https://www.zlmcu.com/download/UDP_port_protocol.pdf); the vendor's utility, DLLs, protocol PDF and embedded SDK credentials are not redistributed.
