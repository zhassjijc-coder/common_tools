from __future__ import annotations

import ctypes
import os
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QPoint, QPointF, QRect, QRectF, QSize
from PySide6.QtGui import QColor, QImage
from PySide6.QtWidgets import QApplication

from linklab.capture import desktop


APP = QApplication.instance() or QApplication([])


class Screen:
    def __init__(self, geometry, pixels, ratio, color):
        self.logical = QRect(*geometry)
        self.ratio = ratio
        self.image = QImage(*pixels, QImage.Format.Format_ARGB32_Premultiplied)
        self.image.fill(QColor(color))
        self.image.setDevicePixelRatio(ratio)
        self.grabs = []

    def geometry(self):
        return QRect(self.logical)

    def devicePixelRatio(self):
        return self.ratio

    def grabWindow(self, identifier):
        self.grabs.append(identifier)
        return SimpleNamespace(toImage=lambda: self.image.copy())


class DesktopTests(unittest.TestCase):
    def capture(self, screens, platform="win32"):
        # Replace this module's sys reference, never the interpreter's sys.platform.
        with patch.object(desktop, "sys", SimpleNamespace(platform=platform)), \
             patch.object(desktop.QGuiApplication, "screens", return_value=screens):
            return desktop.capture_desktop()

    def assert_color(self, image, point, color):
        self.assertEqual(image.pixelColor(*point), QColor(color), point)

    def test_windows_real_mixed_dpi_islands_are_native_pixel_adjacent(self):
        first = Screen((0, 0, 1707, 1067), (2560, 1600), 1.5, "#ec5161")
        second = Screen((2560, 0, 2048, 1152), (2560, 1440), 1.25, "#2a9d79")
        # Distinct edge pixels detect accidental resizing as well as black bands.
        first.image.setPixelColor(2559, 1599, QColor("yellow"))
        second.image.setPixelColor(2559, 1439, QColor("cyan"))
        frame = self.capture([first, second])
        self.assertEqual(frame.geometry, QRect(0, 0, 5120, 1600))
        self.assertEqual(frame.image.size(), QSize(5120, 1600))
        self.assertEqual(frame.image.devicePixelRatio(), 1)
        self.assertEqual(frame.screens, ())
        self.assertEqual(first.grabs, [0]); self.assertEqual(second.grabs, [0])
        self.assertEqual(frame.views[0].geometry, first.logical)
        self.assertEqual(frame.views[1].geometry, second.logical)
        self.assertIs(frame.views[0].screen, first)
        self.assertIs(frame.views[1].screen, second)
        self.assertEqual(frame.views[0].scene_rect, QRectF(0, 0, 2560, 1600))
        self.assertEqual(frame.views[1].scene_rect, QRectF(2560, 0, 2560, 1440))
        for point in ((0, 0), (2559, 0), (2559, 1439)):
            self.assert_color(frame.image, point, "#ec5161")
        for point in ((2560, 0), (2560, 1439), (5119, 0)):
            self.assert_color(frame.image, point, "#2a9d79")
        self.assert_color(frame.image, (2559, 1599), "yellow")
        self.assert_color(frame.image, (5119, 1439), "cyan")
        # No monitor occupies the area below the shorter second screen.
        self.assert_color(frame.image, (5119, 1599), "black")

    def test_windows_equal_dpi_islands_do_not_create_a_gap(self):
        first = Screen((0, 0, 1280, 720), (1920, 1080), 1.5, "red")
        second = Screen((1920, 0, 1280, 720), (1920, 1080), 1.5, "green")
        frame = self.capture([first, second])
        self.assertEqual(frame.geometry, QRect(0, 0, 3840, 1080))
        self.assertEqual(frame.image.size(), QSize(3840, 1080))
        for y in (0, 500, 1079):
            self.assert_color(frame.image, (1919, y), "red")
            self.assert_color(frame.image, (1920, y), "green")
            self.assert_color(frame.image, (3839, y), "green")

    def test_windows_negative_origin_and_vertical_offset(self):
        left = Screen((-2560, -160, 2048, 1152), (2560, 1440), 1.25, "blue")
        main = Screen((0, 0, 1707, 1067), (2560, 1600), 1.5, "red")
        frame = self.capture([main, left])
        self.assertEqual(frame.geometry, QRect(-2560, -160, 5120, 1760))
        self.assertEqual(frame.views[0].scene_rect, QRectF(2560, 160, 2560, 1600))
        self.assertEqual(frame.views[1].scene_rect, QRectF(0, 0, 2560, 1440))
        self.assertEqual(frame.views[1].geometry, left.logical)
        self.assert_color(frame.image, (0, 0), "blue")
        self.assert_color(frame.image, (2559, 1439), "blue")
        self.assert_color(frame.image, (2560, 160), "red")
        self.assert_color(frame.image, (5119, 1759), "red")

    def test_non_windows_retains_logical_canvas_and_native_parts(self):
        low = Screen((-500, 0, 500, 400), (500, 400), 1, "red")
        high = Screen((0, 0, 500, 400), (1000, 800), 2, "blue")
        frame = self.capture([low, high], "linux")
        self.assertEqual(frame.geometry, QRect(-500, 0, 1000, 400))
        self.assertEqual(frame.image.size(), QSize(2000, 800))
        self.assertEqual(frame.views[0].scene_rect, QRectF(0, 0, 500, 400))
        self.assertEqual(frame.views[1].scene_rect, QRectF(500, 0, 500, 400))
        self.assertEqual(len(frame.screens), 2)
        self.assertEqual(frame.screens[0][0], low.logical)
        self.assertEqual(frame.screens[0][1].size(), QSize(500, 400))
        self.assertEqual(frame.screens[1][1].size(), QSize(1000, 800))
        self.assertEqual(frame.screens[1][1].devicePixelRatio(), 1)
        # A 100-logical-pixel crop retains 100/200 native pixels for the two screens.
        for (geometry, image), expected in zip(frame.screens, (100, 200)):
            local = QRectF(geometry).translated(-frame.geometry.x(), -frame.geometry.y())
            crop = QRectF(local.x() + 20, local.y() + 20, 100, 100)
            crop.translate(-local.x(), -local.y())
            ratio = image.width() / local.width()
            native = image.copy(QRect(round(crop.x() * ratio), round(crop.y() * ratio),
                                      round(crop.width() * ratio), round(crop.height() * ratio)))
            self.assertEqual(native.size(), QSize(expected, expected))
        self.assert_color(frame.image, (999, 799), "red")
        self.assert_color(frame.image, (1000, 799), "blue")

    def test_non_windows_uniform_dpi_needs_no_native_parts(self):
        frame = self.capture([Screen((0, 0, 320, 200), (640, 400), 2, "red")], "linux")
        self.assertEqual(frame.geometry, QRect(0, 0, 320, 200))
        self.assertEqual(frame.image.size(), QSize(640, 400))
        self.assertEqual(frame.screens, ())
        self.assert_color(frame.image, (639, 399), "red")

    def test_missing_or_denied_screen_keeps_actionable_errors(self):
        with self.assertRaisesRegex(RuntimeError, "没有可读取的桌面屏幕"):
            self.capture([])
        screen = Screen((0, 0, 10, 10), (0, 0), 1, "red")
        with self.assertRaisesRegex(RuntimeError, "系统拒绝读取屏幕.*Wayland"):
            self.capture([screen])

    def test_pixel_limit_is_checked_before_canvas_allocation(self):
        image = Mock()
        image.isNull.return_value = False
        image.size.return_value = QSize(10001, 10000)
        screen = SimpleNamespace(geometry=lambda: QRect(0, 0, 10001, 10000),
                                 devicePixelRatio=lambda: 1,
                                 grabWindow=lambda _: SimpleNamespace(toImage=lambda: image))
        with patch.object(desktop, "QImage") as constructor:
            with self.assertRaisesRegex(RuntimeError, "超过 1 亿像素"):
                self.capture([screen])
            constructor.assert_not_called()

    def test_non_windows_cursor_uses_qt_logical_coordinates(self):
        with patch.object(desktop, "sys", SimpleNamespace(platform="linux")), \
             patch.object(desktop, "QCursor", SimpleNamespace(pos=lambda: QPoint(-500, 123))):
            self.assertEqual(desktop.desktop_cursor_position(), QPointF(-500, 123))

    def test_windows_cursor_uses_native_point_and_declares_signature(self):
        def get_cursor(pointer):
            pointer._obj.x, pointer._obj.y = -2560, 1440
            return 1
        function = Mock(side_effect=get_cursor)
        loader = Mock(return_value=SimpleNamespace(GetCursorPos=function))
        with patch.object(desktop, "sys", SimpleNamespace(platform="win32")), \
             patch.object(ctypes, "WinDLL", loader, create=True):
            self.assertEqual(desktop.desktop_cursor_position(), QPointF(-2560, 1440))
        from ctypes import wintypes
        loader.assert_called_once_with("user32", use_last_error=True)
        self.assertEqual(function.argtypes, [ctypes.POINTER(wintypes.POINT)])
        self.assertIs(function.restype, wintypes.BOOL)


if __name__ == "__main__":
    unittest.main()
