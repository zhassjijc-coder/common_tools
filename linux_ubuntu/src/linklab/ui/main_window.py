from __future__ import annotations

import json
import os
from pathlib import Path
import sys
from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QAction, QColor, QIcon, QPainter, QPen, QPixmap, QPainterPath
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QGridLayout,
    QLabel, QFrame, QScrollArea, QStackedWidget, QSystemTrayIcon, QMenu, QComboBox, QSpinBox, QLineEdit, QPlainTextEdit, QCheckBox)
from .common import Page, button, row
from .theme import stylesheet
from .communication_pages import SerialPage, NetworkPage
from .industrial_pages import MasterPage, SimulatorPage, PlcPage
from .tools_pages import WorkbenchPage, MonitorPage
from .mqtt_page import MqttPage
from .netconfig_page import NetConfigPage
from .settings_page import SettingsPage
from ..services.hub import Hub
from .. import APP_NAME, APP_SUBTITLE, APP_TITLE, __version__


MODULES = [
    ("home", "工作台", "连接设备，从这里开始", "#405cf5", "diamond"),
    ("serial", "串口 / RS485", "参数配置、收发与校验", "#13969e", "hexagon"),
    ("network", "TCP / UDP", "客户端、服务端与网络收发", "#405cf5", "circle"),
    ("netconfig", "网口配置软件", "发现设备，配置网络与串口参数", "#167b9d", "network"),
    ("master", "Modbus 主站", "地址读写、寄存器与持续轮询", "#8856d8", "grid"),
    ("simulator", "设备模拟", "Modbus TCP / RTU 内存设备", "#c67c27", "diamond"),
    ("workbench", "报文工作台", "按地址生成、编辑并发送报文", "#c75695", "grid"),
    ("monitor", "监视与工具", "时间线、协议解析与数值转换", "#1583a2", "wave"),
    ("plc", "PLC 调试", "西门子、三菱、欧姆龙读写与模拟", "#648d36", "chip"),
    ("mqtt", "MQTT 调试", "连接 Broker，发布与订阅消息", "#9556d5", "bow"),
    ("settings", "系统配置", "后台启动、截图、贴图与离线 OCR", "#b96920", "sliders"),
]


def graphic(shape, color, size=80):
    image = QPixmap(size, size)
    image.fill(Qt.GlobalColor.transparent)
    painter = QPainter(image)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.scale(size / 100, size / 100)
    painter.setPen(QPen(QColor(color), 2.5))
    painter.setBrush(Qt.BrushStyle.NoBrush)
    if shape in ("grid", "network", "chip"):
        painter.drawRoundedRect(18, 18, 64, 64, 2, 2)
        if shape == "chip":
            painter.drawRect(32, 32, 36, 36)
            for position in (30, 50, 70):
                painter.drawLine(position, 5, position, 18)
                painter.drawLine(position, 82, position, 95)
                painter.drawLine(5, position, 18, position)
                painter.drawLine(82, position, 95, position)
        else:
            for position in (39, 61):
                painter.drawLine(position, 18, position, 82)
                painter.drawLine(18, position, 82, position)
    elif shape == "circle":
        for diameter in (80, 48, 16):
            painter.drawEllipse((100-diameter)//2, (100-diameter)//2, diameter, diameter)
        painter.drawLine(6, 50, 94, 50)
        painter.drawLine(50, 6, 50, 94)
    elif shape == "sliders":
        for x, y in ((20, 30), (50, 65), (80, 45)):
            painter.drawLine(x, 8, x, y)
            painter.drawLine(x, y+18, x, 92)
            painter.drawRect(x-8, y, 16, 18)
    else:
        points = {"diamond": [(50, 8), (92, 50), (50, 92), (8, 50), (50, 8)],
                  "hexagon": [(50, 8), (87, 29), (87, 71), (50, 92), (13, 71), (13, 29), (50, 8)],
                  "wave": [(5, 64), (23, 64), (33, 27), (44, 87), (59, 7), (72, 64), (95, 64)],
                  "bow": [(12, 20), (88, 80), (88, 20), (12, 80), (12, 20)]}[shape]
        path = QPainterPath()
        path.moveTo(*points[0])
        for point in points[1:]:
            path.lineTo(*point)
        painter.drawPath(path)
        if shape in ("diamond", "hexagon"):
            painter.drawRect(35, 35, 30, 30)
    painter.end()
    return QIcon(image)


class HomePage(Page):
    def __init__(self, hub, navigate, parent=None):
        super().__init__(hub, parent)
        keys = ["plc", "mqtt", "settings", "serial", "network", "netconfig", "master", "simulator", "workbench", "monitor"]
        card = self.card("让设备调试，清晰有序。", "连接、收发、模拟与分析，从一个工作台开始。本地设备 · 本地数据")
        card.addWidget(QLabel(f"{len(keys)} 个常用调试模块"))
        grid = QGridLayout()
        grid.setSpacing(16)
        for index, key in enumerate(keys):
            _, title, description, color, shape = next(module for module in MODULES if module[0] == key)
            tile = QFrame()
            tile.setObjectName("card")
            tile_layout = QVBoxLayout(tile)
            tile_layout.setContentsMargins(20, 18, 20, 18)
            tile_layout.setSpacing(12)
            meta = QLabel(f"{index+1:02d}  /  {key.upper()}")
            meta.setStyleSheet(f"color:{color};font-size:12px;")
            tile_layout.addWidget(meta)
            heading = QHBoxLayout()
            label = QLabel(title)
            label.setObjectName("cardTitle")
            label.setWordWrap(True)
            heading.addWidget(label, 1)
            icon = QLabel()
            icon.setPixmap(graphic(shape, color, 50).pixmap(50, 50))
            heading.addWidget(icon)
            tile_layout.addLayout(heading)
            description_label = QLabel(description)
            description_label.setObjectName("muted")
            description_label.setWordWrap(True)
            tile_layout.addWidget(description_label)
            tile_layout.addStretch()
            tile_layout.addWidget(button("打开模块  →", lambda checked=False, key=key: navigate(key)))
            grid.addWidget(tile, index//3, index%3)
        for column in range(3):
            grid.setColumnStretch(column, 1)
        self.layout.addLayout(grid)
        self.layout.addStretch()


class MainWindow(QMainWindow):
    def __init__(self, *, test_mode=False):
        super().__init__()
        self.test_mode = test_mode
        self._closing = self._closed = False
        self.dark = False
        self.hub = Hub(self)
        self.setWindowTitle(f"{APP_TITLE} · Python Qt {__version__}")
        self.setWindowIcon(graphic("diamond", "#405cf5"))
        self.setMinimumSize(1000, 680)
        available = QApplication.primaryScreen().availableGeometry()
        self.resize(min(1400, available.width()-40), min(900, available.height()-50))
        canvas = QWidget()
        canvas.setObjectName("canvas")
        self.setCentralWidget(canvas)
        outer = QVBoxLayout(canvas)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)
        header = QWidget()
        header.setObjectName("header")
        header_layout = QHBoxLayout(header)
        header_layout.setContentsMargins(25, 16, 25, 16)
        brand = QLabel(f"◇  {APP_NAME}")
        brand.setObjectName("brand")
        header_layout.addWidget(brand)
        sub = QLabel("｜" + APP_SUBTITLE)
        sub.setObjectName("muted")
        header_layout.addWidget(sub)
        header_layout.addStretch()
        header_layout.addWidget(button("切换主题", self.toggle_theme))
        header_layout.addWidget(button("保存配置", self.save_state))
        outer.addWidget(header)
        split = QHBoxLayout()
        split.setSpacing(0)
        outer.addLayout(split, 1)
        sidebar = QFrame()
        sidebar.setObjectName("sidebar")
        sidebar.setFixedWidth(218)
        navigation = QVBoxLayout(sidebar)
        navigation.setContentsMargins(16, 22, 16, 18)
        navigation.setSpacing(10)
        caption = QLabel("工作空间")
        caption.setObjectName("muted")
        navigation.addWidget(caption)
        nav_scroll = QScrollArea()
        nav_scroll.setWidgetResizable(True)
        nav_scroll.setFrameShape(QFrame.Shape.NoFrame)
        nav_body = QWidget()
        nav_body.setObjectName("header")
        nav_layout = QVBoxLayout(nav_body)
        nav_layout.setContentsMargins(0, 0, 6, 0)
        self.nav_buttons = {}
        for key, title, _, color, shape in MODULES:
            item = button(title, lambda checked=False, key=key: self.navigate(key))
            item.setProperty("nav", True)
            item.setCheckable(True)
            item.setIcon(graphic(shape, color, 22))
            self.nav_buttons[key] = item
            if key != "settings":
                nav_layout.addWidget(item)
        nav_layout.addStretch()
        nav_scroll.setWidget(nav_body)
        nav_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        navigation.addWidget(nav_scroll, 1)
        rule = QFrame()
        rule.setFrameShape(QFrame.Shape.HLine)
        navigation.addWidget(rule)
        navigation.addWidget(self.nav_buttons["settings"])
        footer = QLabel(f"本地设备 · 本地数据\n\n{APP_NAME}  /  {__version__}")
        footer.setObjectName("muted")
        navigation.addWidget(footer)
        split.addWidget(sidebar)
        workspace = QWidget()
        workspace_layout = QVBoxLayout(workspace)
        workspace_layout.setContentsMargins(28, 26, 22, 15)
        self.title = QLabel("工作台")
        self.title.setObjectName("title")
        self.subtitle = QLabel("连接设备，从这里开始")
        self.subtitle.setObjectName("muted")
        workspace_layout.addWidget(self.title)
        workspace_layout.addWidget(self.subtitle)
        workspace_layout.addSpacing(14)
        self.stack = QStackedWidget()
        workspace_layout.addWidget(self.stack, 1)
        split.addWidget(workspace, 1)
        self.pages = {
            "home": HomePage(self.hub, self.navigate), "serial": SerialPage(self.hub), "network": NetworkPage(self.hub),
            "master": MasterPage(self.hub), "simulator": SimulatorPage(self.hub), "workbench": WorkbenchPage(self.hub),
            "monitor": MonitorPage(self.hub), "plc": PlcPage(self.hub), "mqtt": MqttPage(self.hub),
            "netconfig": NetConfigPage(self.hub), "settings": SettingsPage(self.hub, test_mode=test_mode)}
        for key, *_ in MODULES:
            self.stack.addWidget(self.pages[key])
        self.settings = self.pages["settings"]
        self.settings.request_hide.connect(self.hide_to_tray)
        self.settings.controller.ocr_ready.connect(self.show_ocr)
        self.tray = None
        if not test_mode and QSystemTrayIcon.isSystemTrayAvailable():
            self.tray = QSystemTrayIcon(self.windowIcon(), self)
            self.tray.setToolTip(APP_TITLE)
            menu = QMenu()
            for title, callback in ((f"打开{APP_NAME}", self.show_main), ("截图", self.settings.controller.capture),
                                    ("贴图", self.settings.controller.pin_clipboard), ("系统配置", self.open_settings), ("退出", self.close)):
                action = QAction(title, menu)
                action.triggered.connect(callback)
                menu.addAction(action)
            self.tray_menu = menu
            self.tray.setContextMenu(menu)
            self.tray.activated.connect(lambda reason: self.show_main() if reason == QSystemTrayIcon.ActivationReason.DoubleClick else None)
            self.tray.show()
        self.counter_label = QLabel("TX  0 B     RX  0 B")
        self.statusBar().addPermanentWidget(self.counter_label)
        self.hub.counters_changed.connect(lambda tx, rx: self.counter_label.setText(f"TX  {tx} B     RX  {rx} B"))
        self.hub.notice.connect(lambda message: self.statusBar().showMessage(message[:500], 20000))
        self.state_path = self.settings.controller.store.path.with_name("ui-state.json") if hasattr(self.settings.controller, "store") else self._state_path()
        self.restore_state()
        self.navigate("home")
        self.statusBar().showMessage("准备就绪 · 连接与发送均由你主动操作")

    @staticmethod
    def _state_path():
        base = Path(os.environ.get("LOCALAPPDATA", Path.home()/"AppData/Local")) if sys.platform == "win32" else Path(os.environ.get("XDG_CONFIG_HOME", Path.home()/".config"))
        return base/"LinkLabQt"/"ui-state.json"

    def navigate(self, key):
        if key not in self.pages:
            key = "home"
        self.stack.setCurrentWidget(self.pages[key])
        for item, button_ in self.nav_buttons.items():
            button_.setChecked(item == key)
        module = next(module for module in MODULES if module[0] == key)
        self.title.setText(module[1])
        self.subtitle.setText(module[2])

    def open_settings(self):
        self.navigate("settings")
        self.show_main()

    def toggle_theme(self):
        self.dark = not self.dark
        QApplication.instance().setStyleSheet(stylesheet(self.dark))

    def show_main(self):
        if self._closing:
            return
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def hide_to_tray(self):
        if self.tray and self.tray.isVisible():
            self.hide()
        else:
            self.hub.set_notice("当前桌面没有可用系统托盘，已保留主窗口")

    def show_ocr(self, text):
        # The capture controller owns the selectable OCR result window.
        self.hub.set_notice(f"文字识别完成：{len(text)} 个字符")

    def state_fields(self):
        fields = {"serial": ["port", "baud", "bits", "parity", "stopbits"],
                  "network": ["mode", "host", "port", "local_host", "local_port"],
                  "master": ["mode", "host", "port", "serial", "baud", "unit", "parity", "stopbits", "function", "address", "count", "values", "interval", "type", "order"],
                  "simulator": ["mode", "host", "port", "serial", "baud", "unit", "parity", "stopbits"],
                  "plc": ["protocol", "role", "host", "port", "timeout", "rack", "slot", "address", "type", "count", "order", "interval", "listen_host", "listen_port"],
                  "mqtt": ["transport", "protocol", "host", "port", "client_id", "username", "keepalive", "timeout", "session_expiry", "ca_file", "cert_file", "key_file", "will_topic", "will_payload", "will_qos", "reconnect", "clean_session", "will_retain", "publish_topic", "publish_format", "publish_qos", "retain"],
                  "netconfig": ["target", "timeout"],
                  "workbench": ["mode", "function", "unit", "address", "count", "transaction", "values", "custom", "checksum"]}
        for page_key, names in fields.items():
            for name in names:
                yield page_key+"."+name, getattr(self.pages[page_key], name)

    def save_state(self):
        if self.test_mode:
            return
        try:
            data = {"dark": self.dark}
            for key, widget in self.state_fields():
                data[key] = widget.currentText() if isinstance(widget, QComboBox) else widget.value() if isinstance(widget, QSpinBox) else widget.isChecked() if isinstance(widget, QCheckBox) else widget.text()
            self.state_path.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.state_path.with_suffix(".tmp")
            temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
            temporary.replace(self.state_path)
            self.hub.set_notice("连接参数与界面偏好已保存；不会自动建立连接")
        except (OSError, ValueError) as exc:
            self.hub.set_notice(f"保存失败：{exc}")

    def restore_state(self):
        if self.test_mode:
            return
        try:
            data = json.loads(self.state_path.read_text(encoding="utf-8"))
            if not isinstance(data, dict):
                return
            for key, widget in self.state_fields():
                if key not in data:
                    continue
                if isinstance(widget, QComboBox):
                    widget.setCurrentText(str(data[key]))
                elif isinstance(widget, QSpinBox):
                    widget.setValue(int(data[key]))
                elif isinstance(widget, QCheckBox):
                    widget.setChecked(data[key] is True)
                else:
                    widget.setText(str(data[key]))
            if data.get("dark") is True:
                self.toggle_theme()
        except (OSError, ValueError, TypeError):
            pass

    def closeEvent(self, event):
        if self._closed:
            event.accept()
            if not self.test_mode:
                QTimer.singleShot(0, QApplication.instance().quit)
            return
        if self._closing:
            event.ignore()
            return
        event.ignore()
        self._closing = True
        self.setEnabled(False)
        for key in ("serial", "network"):
            self.pages[key].sender.stop()
        for key in ("master", "plc"):
            self.pages[key].poll.setChecked(False)
        self.save_state()
        self._finish_close()

    def _finish_close(self):
        # Never discard a background startup result: first let the bounded operation
        # return, then stop the concrete session that it created.
        if any(getattr(page, "busy", False) for page in self.pages.values()):
            QTimer.singleShot(50, self._finish_close)
            return
        for page in self.pages.values():
            try:
                page.close_resources()
            except Exception as exc:
                self.hub.set_notice(f"关闭连接时：{exc}")
        if self.tray:
            self.tray.hide()
        self._closed = True
        QTimer.singleShot(0, self.close)
