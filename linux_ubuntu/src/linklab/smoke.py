"""Isolated UI render and lifecycle checks; no real device or startup changes."""
from __future__ import annotations

import json
import time
from pathlib import Path
from PySide6.QtCore import QPointF, QRect, QRectF, Qt
from PySide6.QtGui import QImage, QColor, QPainter, QFont
from PySide6.QtTest import QTest
from PySide6.QtWidgets import QComboBox, QScrollArea
from .ui.main_window import MainWindow, MODULES
from .capture.editor import CaptureOverlay, DesktopFrame
from .protocols.zlan import DeviceSettings, DeviceSnapshot


def run(app, output: Path):
    output.mkdir(parents=True, exist_ok=True)
    app.setQuitOnLastWindowClosed(False)
    window = MainWindow(test_mode=True)
    window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen)
    window.resize(1440, 1000)
    window.show()
    findings = []
    failures = []
    app.processEvents()
    try:
        assert len(window.pages) == 11
        assert "virtualserial" not in window.pages
        assert not window.settings.controller.enabled
        findings.append("PASS 1.0 / 10 tools, settings pinned below navigation; virtual serial hidden; capture default off.")
        for key, *_ in MODULES:
            window.navigate(key)
            app.processEvents()
            assert window.stack.currentWidget() is window.pages[key]
            assert window.grab().save(str(output / f"{key}-light.png"))
            page = window.pages[key]
            scroll = getattr(page, "scroll", None)
            if not isinstance(scroll, QScrollArea):
                scroll = None
            if scroll is not None and scroll.horizontalScrollBar().maximum():
                failures.append(f"{key}: normal-size content requires horizontal scrolling")
            for box in page.findChildren(QComboBox):
                if box.count() and box.currentIndex() >= 0:
                    assert box.currentText() != "", f"{key}: blank selected dropdown value"
            if scroll is not None:
                scroll.verticalScrollBar().setValue(scroll.verticalScrollBar().maximum())
                app.processEvents()
                window.grab().save(str(output / f"{key}-bottom.png"))
                scroll.verticalScrollBar().setValue(0)
        findings.append("PASS every module rendered; populated dropdown selections have visible text.")
        plc = window.pages["plc"]
        window.navigate("plc"); plc.role.setCurrentIndex(1); app.processEvents()
        assert plc.role.currentText() == "服务器 / 模拟内存"
        assert plc.server_panel.isVisible() and not plc.client_panel.isVisible()
        assert plc.server_actions.isVisible() and not plc.client_actions.isVisible()
        assert plc.server is None and plc.client is None and not plc.timer.isActive()
        window.grab().save(str(output / "plc-server-light.png"))
        plc.role.setCurrentIndex(0); app.processEvents()
        assert plc.client_panel.isVisible() and not plc.server_panel.isVisible()
        findings.append("PASS PLC role dropdown exposes server controls without starting connections; shared data remains available.")
        # Show a marked synthetic capture without taking over the user's desktop or clipboard.
        image = QImage(1100, 620, QImage.Format.Format_ARGB32_Premultiplied)
        image.fill(QColor("#ffffff"))
        painter = QPainter(image)
        painter.setPen(QColor("#22354c"))
        font = QFont(app.font()); font.setPixelSize(30)
        painter.setFont(font)
        painter.drawText(75, 115, "LinkLab / 设备调试记录")
        font.setPixelSize(20); painter.setFont(font)
        painter.drawText(75, 180, "Modbus TCP  ·  127.0.0.1:1502")
        painter.drawText(75, 230, "RX  01 03 06 08 FC 04 D2 16 2E")
        painter.end()
        overlay = CaptureOverlay(DesktopFrame(image, QRect(0, 0, 1100, 620)))
        overlay.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen)
        overlay.selection = QRectF(40, 50, 950, 480)
        overlay.add_box(QPointF(20, 70), QPointF(640, 210), color="#ee4253")
        overlay.begin_text(QPointF(40, 260))
        overlay._editor.setPlainText("确认站号、地址和寄存器值")
        overlay.commit_text()
        overlay.set_color("#405cf5")
        overlay.show(); app.processEvents()
        assert overlay.grab().save(str(output / "capture-editor.png"))
        marked, original = overlay.selected_image(), overlay.selected_image(False)
        assert not marked.isNull() and marked != original
        assert marked.size() == original.size()
        window.settings.controller.set_image(marked, original)
        pin = window.settings.controller.pin(marked, original)
        app.processEvents()
        assert pin is not None
        pin.grab().save(str(output / "pinned-image.png"))
        pin.close(); overlay.close()
        findings.append("PASS synthetic colored text/rectangle export and pin render; clipboard untouched.")
        net = window.pages["netconfig"]
        params = bytearray(167); params[31:37] = b"\x02\x00\x00\x00\x00\x01"; params[104] = 34
        device = DeviceSnapshot("02:00:00:00:00:01", DeviceSettings(name="DEMO-01"), bytes(params), ("192.168.1.200", 1092), time.time())
        net.devices = [device]; net._render(device.identity)
        net.name.setText("DEMO-02"); net.review()
        net._status("演示数据 · 未发送查询或写入")
        window.navigate("netconfig"); app.processEvents()
        net.scroll.verticalScrollBar().setValue(290); app.processEvents()
        window.grab().save(str(output / "network-config-demo.png"))
        for width, height in ((1050, 760), (1440, 1000)):
            window.resize(width, height)
            for key in ("home", "serial", "master", "plc", "mqtt", "netconfig", "settings"):
                window.navigate(key); app.processEvents()
                window.grab().save(str(output / f"{key}-{width}.png"))
                if key == "plc":
                    plc.role.setCurrentIndex(1); app.processEvents()
                    assert not plc.scroll.horizontalScrollBar().maximum(), "PLC server content overflowed the window"
                    window.grab().save(str(output / f"plc-server-{width}.png"))
                    plc.role.setCurrentIndex(0); app.processEvents()
            settings_button = window.nav_buttons["settings"]
            assert settings_button.isVisible()
            assert settings_button.mapTo(window, settings_button.rect().center()).y() < window.height()-40
        window.toggle_theme(); window.navigate("home"); app.processEvents()
        window.grab().save(str(output / "home-dark.png"))
        window.navigate("plc"); plc.role.setCurrentIndex(1); app.processEvents()
        window.grab().save(str(output / "plc-server-dark.png"))
        plc.role.setCurrentIndex(0)
        window.toggle_theme()
        findings.append("PASS normal/compact window and dark theme renders; settings remains visible at the bottom.")
    except Exception as exc:
        import traceback
        failures.append(traceback.format_exc())
    finally:
        window.close(); window.close()
        deadline = time.monotonic()+15
        while not window._closed and time.monotonic() < deadline:
            QTest.qWait(10)
        if not window._closed:
            failures.append("Window did not complete resource cleanup within 15 seconds")
        app.processEvents()
    findings.append("PASS repeated close and cleanup without startup/device changes." if window._closed else "FAIL cleanup")
    report = "\n".join(findings + ["FAIL "+item for item in failures])+"\n"
    (output / "result.txt").write_text(report, encoding="utf-8")
    print(report)
    return 1 if failures else 0
