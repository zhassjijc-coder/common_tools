"""Capture desktop pixels without mixing Windows screen positions and logical sizes."""
from __future__ import annotations

from dataclasses import dataclass
import math
import sys

from PySide6.QtCore import QPointF, QRect, QRectF
from PySide6.QtGui import QCursor, QGuiApplication, QImage, QPainter


@dataclass
class CaptureScreen:
    screen: object
    geometry: QRect
    scene_rect: QRectF


@dataclass
class DesktopFrame:
    image: QImage
    geometry: QRect
    screens: tuple[tuple[QRect, QImage], ...] = ()
    views: tuple[CaptureScreen, ...] = ()


_MAX_PIXELS = 100_000_000
_CAPTURE_ERROR = "系统拒绝读取屏幕。Wayland 下请使用系统截图后打开图片或从剪贴板贴图。"


def capture_desktop() -> DesktopFrame:
    screens = QGuiApplication.screens()
    if not screens:
        raise RuntimeError("没有可读取的桌面屏幕")

    windows = sys.platform == "win32"
    geometry = QRect()
    captures = []
    for screen in screens:
        snapshot = screen.grabWindow(0).toImage()
        if snapshot.isNull():
            raise RuntimeError(_CAPTURE_ERROR)
        # All composition and export rectangles below are explicit pixel rectangles.
        snapshot.setDevicePixelRatio(1)
        logical = QRect(screen.geometry())
        # Qt keeps Windows screen positions in native coordinates, while scaling
        # their sizes. Native snapshot dimensions restore the physical rectangle.
        scene = QRect(logical.topLeft(), snapshot.size()) if windows else QRect(logical)
        geometry = geometry.united(scene)
        captures.append((screen, logical, scene, snapshot))
        if windows and geometry.width() * geometry.height() > _MAX_PIXELS:
            raise RuntimeError("屏幕尺寸超过 1 亿像素，请减少连接的显示器或使用系统截图")

    ratios = [screen.devicePixelRatio() for screen in screens]
    ratio = 1.0 if windows else max(ratios)
    width, height = math.ceil(geometry.width() * ratio), math.ceil(geometry.height() * ratio)
    if width * height > _MAX_PIXELS:
        raise RuntimeError("屏幕尺寸超过 1 亿像素，请减少连接的显示器或使用系统截图")

    image = QImage(width, height, QImage.Format.Format_ARGB32_Premultiplied)
    if image.isNull():
        raise RuntimeError("无法分配截图画布，请减少连接的显示器或使用系统截图")
    image.setDevicePixelRatio(1)
    image.fill(0xff000000)
    native_parts = []
    views = []
    mixed_dpi = max(ratios) - min(ratios) > .001
    painter = QPainter(image)
    try:
        for screen, logical, scene, snapshot in captures:
            local = QRectF(scene).translated(-geometry.x(), -geometry.y())
            views.append(CaptureScreen(screen, logical, local))
            target = QRectF(local.x() * ratio, local.y() * ratio,
                            local.width() * ratio, local.height() * ratio)
            painter.drawImage(target, snapshot, QRectF(snapshot.rect()))
            if not windows and mixed_dpi:
                native_parts.append((QRect(logical), snapshot))
    finally:
        painter.end()
    return DesktopFrame(image, geometry, tuple(native_parts), tuple(views))


def desktop_cursor_position() -> QPointF:
    """Return the cursor in the same global coordinate system as frame.geometry."""
    if sys.platform != "win32":
        return QPointF(QCursor.pos())

    import ctypes
    from ctypes import wintypes

    user = ctypes.WinDLL("user32", use_last_error=True)
    user.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
    user.GetCursorPos.restype = wintypes.BOOL
    point = wintypes.POINT()
    if not user.GetCursorPos(ctypes.byref(point)):
        raise ctypes.WinError(ctypes.get_last_error())
    return QPointF(point.x, point.y)
