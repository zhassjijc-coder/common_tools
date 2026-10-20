from __future__ import annotations

import os
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from PySide6.QtCore import QCoreApplication, QEvent, QObject, QPointF, QRect, QRectF, Signal, Qt
from PySide6.QtGui import QColor, QFont, QFontDatabase, QImage, QKeyEvent, QMouseEvent, QPainter
from PySide6.QtWidgets import QApplication, QPushButton

from linklab.capture.editor import CaptureController, CaptureOverlay, CaptureScreen, CaptureSession, DesktopFrame, PinnedImageWindow
from linklab.capture.hotkeys import GlobalHotkeys
from linklab.capture import ocr
from linklab.services.settings import AutostartService, SettingsStore
from linklab.ui.settings_page import SettingsPage


APP = QApplication.instance() or QApplication([])
# Qt's Windows offscreen backend does not enumerate installed fonts itself.
if os.name == "nt" and APP.platformName() == "offscreen":
    fonts = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
    for name in ("arial.ttf", "segoeui.ttf", "msyh.ttc"):
        if (fonts / name).exists():
            QFontDatabase.addApplicationFont(str(fonts / name))
    APP.setFont(QFont("Segoe UI", 10))


class Hub(QObject):
    event = Signal(object)
    notice = Signal(str)

    def __init__(self):
        super().__init__()
        self.last_notice = ""

    def set_notice(self, text):
        self.last_notice = text
        self.notice.emit(text)

    def log(self, source, direction, data, note=""):
        pass


def sample(width=1000, height=456):
    image = QImage(width, height, QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(Qt.GlobalColor.white)
    return image


class CaptureTests(unittest.TestCase):
    def setUp(self):
        self.windows = []

    def tearDown(self):
        for window in self.windows:
            window.close()
        # Exercise actual Qt destruction, not just visibility/state flags.
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
        APP.processEvents()

    def overlay(self):
        overlay = CaptureOverlay(DesktopFrame(sample(), QRect(0, 0, 500, 228)))
        overlay.show()
        APP.processEvents()
        self.windows.append(overlay)
        overlay.selection = QRectF(10.25, 8.5, 100.5, 60.25)
        overlay._update_ui()
        return overlay

    def test_fractional_dpi_crop_mark_pixels_and_zoom_independence(self):
        overlay = self.overlay()
        raw = overlay.selected_image(False)
        self.assertEqual((raw.width(), raw.height()), (202, 121))
        overlay.add_box(QPointF(5, 5), QPointF(25, 25), color="#ff00ff")
        marked = overlay.selected_image()
        edge = marked.pixelColor(10, 20)
        self.assertGreater(edge.red(), 220)
        self.assertGreater(edge.blue(), 220)
        self.assertLess(edge.green(), 40)
        self.assertNotEqual(ocr.png_bytes(marked), ocr.png_bytes(raw))
        overlay.set_zoom(2.5)
        self.assertEqual(ocr.png_bytes(marked), ocr.png_bytes(overlay.selected_image()))
        overlay.set_zoom(.5)
        self.assertEqual(ocr.png_bytes(marked), ocr.png_bytes(overlay.selected_image()))

    def test_move_resize_and_reverse_square(self):
        overlay = self.overlay()
        overlay.selection = QRectF(20, 30, 300, 180)
        overlay.add_box(QPointF(80, 80), QPointF(10, 35), square=True)
        self.assertEqual(overlay.marks[0].rect, QRectF(35, 35, 45, 45))
        overlay.move_selection(QPointF(-100, 500))
        self.assertEqual(overlay.selection, QRectF(0, 48, 300, 180))
        self.assertEqual(overlay.marks[0].rect, QRectF(35, 35, 45, 45))
        overlay.resize_selection(QPointF(10, -20), "lt")
        self.assertEqual(overlay.selection, QRectF(10, 28, 290, 200))
        self.assertEqual(overlay.marks[0].rect, QRectF(25, 55, 45, 45))
        overlay.undo()
        self.assertEqual(overlay.selection, QRectF(0, 48, 300, 180))

    def test_mixed_dpi_single_screen_uses_its_native_pixels(self):
        low, high = sample(500, 400), sample(1000, 800)
        frame = DesktopFrame(sample(2000, 800), QRect(-500, 0, 1000, 400), ((QRect(-500, 0, 500, 400), low), (QRect(0, 0, 500, 400), high)))
        overlay = CaptureOverlay(frame)
        self.windows.append(overlay)
        overlay.selection = QRectF(20, 20, 100, 100)
        self.assertEqual(overlay.selected_image(False).size().width(), 100)
        overlay.selection = QRectF(520, 20, 100, 100)
        self.assertEqual(overlay.selected_image(False).size().width(), 200)
        overlay.add_box(QPointF(5, 5), QPointF(30, 30), color="#ff00ff")
        self.assertLess(overlay.selected_image().pixelColor(10, 30).green(), 40)
        overlay.selection = QRectF(450, 20, 100, 100)
        self.assertEqual(overlay.selected_image(False).size().width(), 200)

    def multi_screen_session(self):
        image = sample(512, 160)
        painter = QPainter(image)
        painter.fillRect(QRect(0, 0, 256, 160), QColor("red"))
        painter.fillRect(QRect(256, 0, 256, 144), QColor("#00d040"))
        painter.end()
        frame = DesktopFrame(image, QRect(-256, -20, 512, 160), views=(
            CaptureScreen(None, QRect(-256, -20, 171, 107), QRectF(0, 0, 256, 160)),
            CaptureScreen(None, QRect(0, -20, 205, 115), QRectF(256, 0, 256, 144)),
        ))
        with patch("linklab.capture.editor.desktop_cursor_position", return_value=QPointF(-200, 0)):
            session = CaptureSession(frame)
        self.windows.append(session)
        return session

    def test_per_monitor_views_cover_each_logical_screen_and_share_zoom(self):
        session = self.multi_screen_session()
        first, second = session.windows
        self.assertEqual(first.geometry(), QRect(-256, -20, 171, 107))
        self.assertEqual(second.geometry(), QRect(0, -20, 205, 115))
        self.assertEqual(first._screen(QPointF(256, 160)), QPointF(171, 107))
        self.assertEqual(second._screen(QPointF(512, 144)), QPointF(205, 115))
        for view, point in ((first, QPointF(100, 60)), (second, QPointF(400, 80))):
            self.assertLess((view._world(view._screen(point)) - point).manhattanLength(), .0001)
        first.selection = QRectF(246, 30, 20, 60)
        first._update_ui()
        original = ocr.png_bytes(first.selected_image(False))
        first.set_zoom(2)
        self.assertEqual(second.zoom, 2)
        self.assertEqual(second.pan, first.pan)
        second.set_zoom(.5)
        self.assertEqual(first.zoom, .5)
        self.assertEqual(original, ocr.png_bytes(second.selected_image(False)))

    def test_cross_monitor_mouse_drag_and_native_pixel_export(self):
        session = self.multi_screen_session()
        first, second = session.windows
        def event(kind, button, buttons):
            # Deliberately wrong local coordinates: a grabbed window must use
            # the native global cursor when crossing to a different-DPI screen.
            return QMouseEvent(kind, QPointF(1, 1), QPointF(1, 1), button, buttons, Qt.KeyboardModifier.NoModifier)
        with patch("linklab.capture.editor.desktop_cursor_position", return_value=QPointF(-10, 10)):
            first.mousePressEvent(event(QEvent.Type.MouseButtonPress, Qt.MouseButton.LeftButton, Qt.MouseButton.LeftButton))
        with patch("linklab.capture.editor.desktop_cursor_position", return_value=QPointF(10, 70)):
            first.mouseMoveEvent(event(QEvent.Type.MouseMove, Qt.MouseButton.NoButton, Qt.MouseButton.LeftButton))
            first.mouseReleaseEvent(event(QEvent.Type.MouseButtonRelease, Qt.MouseButton.LeftButton, Qt.MouseButton.NoButton))
        self.assertEqual(first.selection, QRectF(246, 30, 20, 60))
        self.assertEqual(second.selection, first.selection)
        self.assertIs(session.active, second)
        raw = second.selected_image(False)
        self.assertEqual((raw.width(), raw.height()), (20, 60))
        self.assertEqual(raw.pixelColor(9, 59), QColor("red"))
        self.assertEqual(raw.pixelColor(10, 59), QColor("#00d040"))
        second.add_box(QPointF(2, 5), QPointF(18, 50), color="#0000ff")
        self.assertIs(first.marks, second.marks)
        self.assertEqual(ocr.png_bytes(first.selected_image()), ocr.png_bytes(second.selected_image()))
        second.undo()
        self.assertFalse(first.marks)

    def test_second_monitor_render_has_content_at_bottom_right(self):
        session = self.multi_screen_session()
        first, second = session.windows
        first.selection = QRectF(0, 0, 512, 160)
        first._update_ui()
        session.show()
        APP.processEvents()
        for view in session.windows:
            view.toolbar.hide()
            view._size.hide()
        image = second.grab().toImage()
        pixel = image.pixelColor(image.width() - 4, image.height() - 4)
        self.assertGreater(pixel.green(), 150)
        self.assertLess(pixel.red(), 40)
        self.assertEqual(second.selected_image(False).size(), session.frame.image.size())

    def test_session_completion_and_cancellation_close_every_monitor(self):
        session = self.multi_screen_session()
        results, closed = [], []
        session.completed.connect(lambda image, raw, action: results.append((image, raw, action)))
        session.closed.connect(lambda: closed.append(True))
        first, second = session.windows
        first.selection = QRectF(240, 20, 40, 60)
        first._update_ui()
        session.show()
        second.finish("ocr")
        self.assertEqual(len(results), 1)
        self.assertEqual(results[0][2], "ocr")
        self.assertEqual(results[0][0].width(), 40)
        self.assertEqual(len(closed), 1)
        self.assertTrue(all(not view.isVisible() for view in session.windows))
        session.close()
        self.assertEqual(len(closed), 1)

    def test_controller_tracks_active_monitor_and_cancels_all_views(self):
        frame = self.multi_screen_session().frame
        controller = CaptureController(Hub(), SettingsStore(test_mode=True))
        try:
            with patch("linklab.capture.editor.capture_desktop", return_value=frame), \
                 patch("linklab.capture.editor.desktop_cursor_position", return_value=QPointF(-200, 0)):
                controller.capture()
                limit = time.monotonic() + 2
                while controller._capture_session is None and time.monotonic() < limit:
                    APP.processEvents()
                    time.sleep(.005)
            self.assertIsNotNone(controller._capture_session)
            session = controller._capture_session
            session.activate(session.windows[1])
            self.assertIs(controller._overlay, session.windows[1])
            controller.set_enabled(False)
            self.assertIsNone(controller._capture_session)
            self.assertIsNone(controller._overlay)
            self.assertFalse(controller._capturing)
            self.assertTrue(all(not view.isVisible() for view in session.windows))
        finally:
            controller.close_resources()

    def test_cross_dpi_unchanged_text_edit_preserves_native_font_and_wrap(self):
        frame = self.multi_screen_session().frame
        frame.views = (
            CaptureScreen(None, QRect(-256, -20, 256, 160), QRectF(0, 0, 256, 160)),
            CaptureScreen(None, QRect(0, -20, 171, 96), QRectF(256, 0, 256, 144)),
        )
        with patch("linklab.capture.editor.desktop_cursor_position", return_value=QPointF(-200, 0)):
            session = CaptureSession(frame)
        self.windows.append(session)
        first, second = session.windows
        first.selection = QRectF(10, 10, 350, 130)
        first.begin_text(QPointF(10, 10))
        first._editor.setPlainText("DPI 150%\n" + "M" * 40)
        first.commit_text()
        font, wrap = first.marks[0].font_size, first.marks[0].wrap_width
        first.move_selection(QPointF(140, 0))
        before = ocr.png_bytes(first.selected_image())
        session.activate(second)
        second.begin_text(second.marks[0].rect.topLeft(), 0)
        second.commit_text()
        self.assertEqual(second.marks[0].font_size, font)
        self.assertEqual(second.marks[0].wrap_width, wrap)
        self.assertEqual(ocr.png_bytes(second.selected_image()), before)
        second.begin_text(second.marks[0].rect.topLeft(), 0)
        second.set_font_size(30)
        second.commit_text()
        self.assertEqual(second.marks[0].font_size, round(30 / second._view_sx))

    def test_space_pan_released_on_another_monitor_does_not_stick(self):
        session = self.multi_screen_session()
        first, second = session.windows
        first.keyPressEvent(QKeyEvent(QEvent.Type.KeyPress, Qt.Key.Key_Space, Qt.KeyboardModifier.NoModifier))
        self.assertTrue(first._space and second._space)
        session.activate(second)
        second.keyReleaseEvent(QKeyEvent(QEvent.Type.KeyRelease, Qt.Key.Key_Space, Qt.KeyboardModifier.NoModifier))
        self.assertFalse(first._space or second._space)

    def test_existing_color_text_wrap_and_undo(self):
        overlay = self.overlay()
        overlay.selection = QRectF(10, 10, 460, 200)
        overlay.add_box(QPointF(5, 5), QPointF(200, 60), color="#ff0000")
        before = ocr.png_bytes(overlay.selected_image())
        overlay.set_color("#0000ff")
        self.assertNotEqual(before, ocr.png_bytes(overlay.selected_image()))
        overlay.undo()
        self.assertEqual(before, ocr.png_bytes(overlay.selected_image()))
        overlay.begin_text(QPointF(15, 80))
        overlay._editor.setPlainText("M" * 80)
        overlay.commit_text()
        wrapped = ocr.png_bytes(overlay.selected_image())
        self.assertGreater(overlay.marks[1].rect.height(), overlay.font_size)
        overlay.begin_text(overlay.marks[1].rect.topLeft(), 1)
        overlay.commit_text()
        self.assertEqual(wrapped, ocr.png_bytes(overlay.selected_image()))
        overlay.begin_text(overlay.marks[1].rect.topLeft(), 1)
        overlay._editor.setPlainText("中文标记 25°C")
        overlay.commit_text()
        self.assertEqual(len(overlay.marks), 2)
        overlay.undo()
        self.assertEqual(wrapped, ocr.png_bytes(overlay.selected_image()))

    def test_completed_keeps_raw_ocr_source(self):
        overlay = self.overlay()
        original = ocr.png_bytes(overlay.selected_image(False))
        overlay.add_box(QPointF(5, 5), QPointF(40, 40))
        results = []
        overlay.completed.connect(lambda marked, raw, action: results.append((marked, raw, action)))
        overlay.finish("ocr")
        self.assertEqual(results[0][2], "ocr")
        self.assertEqual(ocr.png_bytes(results[0][1]), original)
        self.assertNotEqual(ocr.png_bytes(results[0][0]), original)

    def test_pin_zoom_and_controller_original_ocr(self):
        image = sample(200, 100)
        pin = PinnedImageWindow(image, lambda _: None, lambda _: None)
        self.windows.append(pin)
        pin.set_scale(2)
        self.assertEqual(pin.scale, 2)
        pin.set_scale(.5)
        self.assertEqual(pin.scale, .5)
        pin.set_scale(1)
        self.assertEqual(pin.image.size(), image.size())
        controller = CaptureController(Hub(), SettingsStore(test_mode=True), test_mode=True)
        raw = sample(80, 40)
        marked = raw.copy()
        marked.fill(Qt.GlobalColor.red)
        controller.set_image(marked, raw)
        received = []
        controller.ocr_ready.connect(received.append)
        with patch("linklab.capture.editor.ocr.recognize", side_effect=lambda source, language, **kwargs: "raw" if source == raw else "wrong"):
            controller.recognize()
            limit = time.monotonic() + 3
            while controller._recognizing and time.monotonic() < limit:
                APP.processEvents()
                time.sleep(.005)
        self.assertEqual(received, ["raw"])
        controller.close_resources()
        canceled = CaptureController(Hub(), SettingsStore(test_mode=True), test_mode=True)
        canceled.set_image(raw)
        def waiting(source, language, *, cancel):
            cancel.wait(3)
            return "late result"
        with patch("linklab.capture.editor.ocr.recognize", side_effect=waiting):
            canceled.recognize()
            canceled.close_resources()
            self.assertFalse(canceled._ocr_thread.is_alive())
            APP.processEvents()
            self.assertFalse(canceled._floating)

    def test_settings_safe_mode_defaults_and_persistence(self):
        temporary_root = Path(__file__).resolve().parents[1] / ".test-temp"
        temporary_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary_root) as directory:
            path = Path(directory) / "prefs.json"
            memory = SettingsStore(path, test_mode=True)
            memory.set("capture_on_startup", True)
            self.assertFalse(path.exists())
            store = SettingsStore(path)
            self.assertFalse(store.get("capture_on_startup", False))
            store.set("capture_on_startup", True)
            self.assertTrue(SettingsStore(path).get("capture_on_startup"))
            startup = AutostartService(test_mode=True, desktop_path=Path(directory) / "autostart.desktop")
            startup.set_enabled(True)
            self.assertTrue(startup.enabled)
            self.assertFalse(startup.desktop_path.exists())
            linux = AutostartService(desktop_path=Path(directory) / "isolated.desktop", platform="linux")
            linux.set_enabled(True)
            text = linux.desktop_path.read_text(encoding="utf-8")
            self.assertIn("--background", text)
            self.assertTrue(linux.enabled)
            linux.set_enabled(False)
            self.assertFalse(linux.desktop_path.exists())
        page = SettingsPage(Hub(), test_mode=True)
        self.windows.append(page)
        self.assertFalse(page.default_capture_checkbox.isChecked())
        self.assertFalse(page.controller.enabled)
        page.enabled_checkbox.setChecked(True)
        self.assertTrue(page.controller.enabled)
        page.controller.capture()
        self.assertIsNone(page.controller._overlay)
        copy = next(button for button in page.findChildren(QPushButton) if button.text() == "复制图片")
        copy.click()  # clicked(bool) must never become the optional QImage argument.
        page.close_resources()

    def test_wayland_global_shortcut_limitation_is_explicit(self):
        with patch.dict(os.environ, {"XDG_SESSION_TYPE": "wayland", "WAYLAND_DISPLAY": "wayland-0"}), patch("linklab.capture.hotkeys.sys.platform", "linux"):
            self.assertIn("Wayland", GlobalHotkeys.limitation())
            with self.assertRaisesRegex(RuntimeError, "Wayland"):
                GlobalHotkeys().enable()

    @unittest.skipUnless(os.name == "nt", "Windows native registration regression")
    def test_native_hotkey_conflict_and_release_without_using_f1_f3(self):
        first = GlobalHotkeys(capture_key=0x86, pin_key=0x87)
        competitor = GlobalHotkeys(capture_key=0x86, pin_key=0x87)
        try:
            try:
                first.enable()
            except RuntimeError as exc:
                if "Windows 5" in str(exc):
                    self.skipTest("restricted desktop denies native hotkey registration; real-user desktop regression must run outside the sandbox")
                raise
            with self.assertRaisesRegex(RuntimeError, "占用"):
                competitor.enable()
            first.close()
            self.assertFalse(first._thread)
            competitor.enable()
        finally:
            first.close()
            competitor.close()

    def test_real_offline_english_ocr_when_engine_available(self):
        if not ocr._windows_engine() and not __import__("shutil").which("tesseract"):
            self.skipTest("Tesseract engine not installed; explicit unavailable state is retained")
        image = sample(1000, 200)
        painter = QPainter(image)
        painter.setPen(Qt.GlobalColor.black)
        font = QFont("Arial")
        font.setPixelSize(54)
        painter.setFont(font)
        painter.drawText(40, 105, "LinkLab MQTT 12345")
        painter.end()
        self.assertIn("12345", ocr.recognize(image, "eng"))


if __name__ == "__main__":
    unittest.main()
