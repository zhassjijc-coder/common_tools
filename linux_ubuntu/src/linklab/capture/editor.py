"""Qt screenshot editing. Export coordinates always refer to the original image."""
from __future__ import annotations

from dataclasses import dataclass, replace
import math
import threading

from PySide6.QtCore import QObject, QPoint, QPointF, QRect, QRectF, QSizeF, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QGuiApplication, QImage, QKeyEvent, QMouseEvent, QPainter, QPainterPath, QPalette, QPen, QTextCharFormat, QTextCursor, QTextDocument, QWheelEvent
from PySide6.QtWidgets import QApplication, QFileDialog, QFrame, QHBoxLayout, QLabel, QPushButton, QScrollArea, QSpinBox, QStyle, QTextEdit, QVBoxLayout, QWidget

from .hotkeys import GlobalHotkeys
from . import ocr


@dataclass
class DesktopFrame:
    image: QImage
    geometry: QRect
    screens: tuple[tuple[QRect, QImage], ...] = ()


@dataclass
class Mark:
    kind: str
    rect: QRectF
    color: str = "#ee4253"
    text: str = ""
    font_size: int = 20
    wrap_width: float = 300

    def clone(self):
        return replace(self, rect=QRectF(self.rect))


def _document(mark: Mark) -> QTextDocument:
    document = QTextDocument()
    document.setDocumentMargin(0)
    font = QFont(QApplication.font())
    font.setPixelSize(mark.font_size)
    document.setDefaultFont(font)
    document.setPlainText(mark.text)
    document.setTextWidth(max(2, mark.wrap_width))
    cursor = QTextCursor(document)
    cursor.select(QTextCursor.SelectionType.Document)
    style = QTextCharFormat()
    style.setForeground(QColor(mark.color))
    cursor.mergeCharFormat(style)
    return document


def _draw_mark(painter: QPainter, mark: Mark) -> None:
    if mark.kind == "text":
        painter.save()
        painter.translate(mark.rect.topLeft())
        _document(mark).drawContents(painter)
        painter.restore()
    else:
        painter.setPen(QPen(QColor(mark.color), 2))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawRect(mark.rect)


def capture_desktop() -> DesktopFrame:
    screens = QGuiApplication.screens()
    if not screens:
        raise RuntimeError("没有可读取的桌面屏幕")
    geometry = QRect()
    for screen in screens:
        geometry = geometry.united(screen.geometry())
    ratio = max(screen.devicePixelRatio() for screen in screens)
    mixed_dpi = max(screen.devicePixelRatio() for screen in screens) - min(screen.devicePixelRatio() for screen in screens) > .001
    native_parts = []
    width, height = math.ceil(geometry.width() * ratio), math.ceil(geometry.height() * ratio)
    if width * height > 100_000_000:
        raise RuntimeError("屏幕尺寸超过 1 亿像素，请减少连接的显示器或使用系统截图")
    image = QImage(width, height, QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(Qt.GlobalColor.black)
    painter = QPainter(image)
    try:
        for screen in screens:
            snapshot = screen.grabWindow(0).toImage()
            if snapshot.isNull():
                raise RuntimeError("系统拒绝读取屏幕。Wayland 下请使用系统截图后打开图片或从剪贴板贴图。")
            rect = screen.geometry()
            if mixed_dpi:
                snapshot.setDevicePixelRatio(1)
                native_parts.append((QRect(rect), snapshot))
            target = QRectF((rect.x() - geometry.x()) * ratio, (rect.y() - geometry.y()) * ratio, rect.width() * ratio, rect.height() * ratio)
            painter.drawImage(target, snapshot)
    finally:
        painter.end()
    return DesktopFrame(image, geometry, tuple(native_parts))


class _TextInput(QTextEdit):
    accepted = Signal()
    canceled = Signal()

    def keyPressEvent(self, event: QKeyEvent):
        if event.key() == Qt.Key.Key_Escape:
            self.canceled.emit()
        elif event.key() in (Qt.Key.Key_Return, Qt.Key.Key_Enter) and not event.modifiers() & Qt.KeyboardModifier.ShiftModifier:
            self.accepted.emit()
        else:
            super().keyPressEvent(event)


class CaptureOverlay(QWidget):
    completed = Signal(object, object, str)

    def __init__(self, frame: DesktopFrame, parent=None):
        super().__init__(parent, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool)
        self.frame = frame
        self.selection = QRectF()
        self.marks: list[Mark] = []
        self.history: list[tuple[QRectF, list[Mark]]] = []
        self.zoom = 1.0
        self.pan = QPointF()
        self.mode = "move"
        self.color = "#ee4253"
        self.font_size = 20
        self.selected = -1
        self._drag = ""
        self._edge = ""
        self._origin = QPointF()
        self._origin_rect = QRectF()
        self._pan_origin = QPointF()
        self._pan_before = QPointF()
        self._pending: Mark | None = None
        self._space = False
        self._editor: _TextInput | None = None
        self._editing = -1
        self._text_point = QPointF()
        self.setGeometry(frame.geometry)
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setWindowTitle("LinkLab 区域截图")
        self._tips = QLabel("拖动选择区域 · 滚轮缩放 · 空格拖动 / 中键平移 · Enter 复制 · Esc 取消", self)
        self._tips.setStyleSheet("background:white;color:#24334a;padding:10px;border-radius:6px;")
        self._tips.adjustSize()
        self._tips.move(16, 16)
        self._size = QLabel(self)
        self._size.setStyleSheet("background:white;color:#24334a;padding:5px;border-radius:4px;")
        self.toolbar = self._make_toolbar()
        self._update_ui()

    @property
    def scene_size(self) -> QSizeF:
        return QSizeF(self.frame.geometry.width(), self.frame.geometry.height())

    def _button(self, text, action, layout):
        button = QPushButton(text)
        button.setFocusPolicy(Qt.FocusPolicy.NoFocus)
        button.clicked.connect(lambda checked=False: action())
        layout.addWidget(button)
        return button

    def _make_toolbar(self):
        toolbar = QFrame(self)
        toolbar.setStyleSheet("QFrame{background:white;color:#24334a;border:1px solid #dce3ee;border-radius:7px;} QPushButton{background:#f8faff;color:#24334a;border:1px solid #dce3ee;border-radius:5px;padding:6px;} QPushButton:checked{background:#e6edff;border-color:#3664e8;} QSpinBox{background:white;color:#24334a;padding:3px;}")
        main = QVBoxLayout(toolbar)
        main.setContentsMargins(6, 6, 6, 6)
        tools = QHBoxLayout()
        tools.setSpacing(4)
        main.addLayout(tools)
        self.tool_buttons = {}
        for label, mode in (("移动 V", "move"), ("文字 T", "text"), ("矩形 R", "rect"), ("正方形 S", "square")):
            button = self._button(label, lambda checked=False, mode=mode: self.set_tool(mode), tools)
            button.setCheckable(True)
            self.tool_buttons[mode] = button
        for name, color in (("红", "#ee4253"), ("橙", "#ed9a23"), ("蓝", "#3065e8"), ("绿", "#14976d"), ("黑", "#182335"), ("白", "#ffffff")):
            button = self._button("", lambda checked=False, color=color: self.set_color(color), tools)
            button.setFixedSize(23, 23)
            button.setStyleSheet(f"background:{color};border:1px solid #aab6c8;border-radius:4px;")
            button.setToolTip(name + "色：修改选中标注及后续标注颜色")
        self.font_box = QSpinBox()
        self.font_box.setRange(10, 72)
        self.font_box.setValue(20)
        self.font_box.setPrefix("字号 ")
        self.font_box.valueChanged.connect(self.set_font_size)
        tools.addWidget(self.font_box)
        self._button("撤销", self.undo, tools)
        self._button("删除", self.delete_selected, tools)
        actions = QHBoxLayout()
        actions.setSpacing(4)
        main.addLayout(actions)
        for label, action in (("复制 ↵", "copy"), ("保存", "save"), ("贴图 F3", "pin"), ("文字识别", "ocr")):
            self._button(label, lambda checked=False, action=action: self.finish(action), actions)
        self._button("−", lambda: self.set_zoom(self.zoom / 1.2), actions)
        self.zoom_label = QLabel("100%")
        actions.addWidget(self.zoom_label)
        self._button("+", lambda: self.set_zoom(self.zoom * 1.2), actions)
        self._button("100%", lambda: self.set_zoom(1), actions)
        actions.addWidget(QLabel("Ctrl+Z 撤销 · 双击文字编辑 · 点选标注可切色"))
        toolbar.adjustSize()
        return toolbar

    def save_state(self):
        self.history.append((QRectF(self.selection), [mark.clone() for mark in self.marks]))
        self.history = self.history[-60:]

    def undo(self):
        if self._editor:
            self.cancel_text()
            return
        if self.history:
            self.selection, self.marks = self.history.pop()
            self.selected = -1
            self._update_ui()

    def delete_selected(self):
        if self._editor:
            self.cancel_text()
        elif 0 <= self.selected < len(self.marks):
            self.save_state()
            self.marks.pop(self.selected)
            self.selected = -1
            self._update_ui()

    def set_tool(self, mode):
        self.commit_text()
        self.mode = mode
        self.selected = -1
        self._update_ui()

    def set_color(self, color):
        self.color = QColor(color).name()
        if self._editor:
            palette = self._editor.palette()
            palette.setColor(QPalette.ColorRole.Text, QColor(self.color))
            background = "#273349" if QColor(self.color).lightness() > 190 else "#ffffff"
            palette.setColor(QPalette.ColorRole.Base, QColor(background))
            self._editor.setPalette(palette)
            self._editor.setStyleSheet(f"background:{background};color:{self.color};border:1px solid #3065e8;")
            selected = self._editor.textCursor()
            cursor = QTextCursor(self._editor.document())
            cursor.select(QTextCursor.SelectionType.Document)
            style = QTextCharFormat()
            style.setForeground(QColor(self.color))
            cursor.mergeCharFormat(style)
            self._editor.setTextCursor(selected)
            self._editor.setTextColor(QColor(self.color))
        elif 0 <= self.selected < len(self.marks):
            self.save_state()
            self.marks[self.selected].color = self.color
        self._update_ui()

    def set_font_size(self, size):
        self.font_size = int(size)
        if self._editor:
            font = self._editor.font()
            font.setPixelSize(round(size * self.zoom))
            self._editor.setFont(font)
            self._editor.setMinimumHeight(round(size * self.zoom) + 10)
        elif 0 <= self.selected < len(self.marks) and self.marks[self.selected].kind == "text":
            self.save_state()
            mark = self.marks[self.selected]
            mark.font_size = size
            mark.rect.setSize(_document(mark).size())
        self._update_ui()

    def _screen(self, point):
        return point * self.zoom + self.pan

    def _world(self, point):
        return (point - self.pan) / self.zoom

    def _clamp(self, point):
        return QPointF(max(0, min(point.x(), self.scene_size.width())), max(0, min(point.y(), self.scene_size.height())))

    def _limit_pan(self):
        w, h = self.scene_size.width() * self.zoom, self.scene_size.height() * self.zoom
        self.pan.setX((self.width() - w) / 2 if w <= self.width() else max(self.width() - w, min(0, self.pan.x())))
        self.pan.setY((self.height() - h) / 2 if h <= self.height() else max(self.height() - h, min(0, self.pan.y())))

    def set_zoom(self, zoom, anchor=None):
        self.commit_text()
        anchor = anchor or QPointF(self.width() / 2, self.height() / 2)
        world = self._world(anchor)
        self.zoom = max(.25, min(8., float(zoom)))
        self.pan = anchor - world * self.zoom
        self._limit_pan()
        self._update_ui()

    def move_selection(self, delta: QPointF, *, remember=True):
        if remember:
            self.save_state()
        rect = self.selection
        rect.moveTo(max(0, min(rect.x() + delta.x(), self.scene_size.width() - rect.width())), max(0, min(rect.y() + delta.y(), self.scene_size.height() - rect.height())))
        self._update_ui()

    def resize_selection(self, delta: QPointF, edge: str, *, remember=True, origin=None):
        if remember:
            self.save_state()
        rect = QRectF(origin or self.selection)
        left, top, right, bottom = rect.left(), rect.top(), rect.right(), rect.bottom()
        if "l" in edge:
            left = max(0, min(left + delta.x(), right - 2))
        if "r" in edge:
            right = max(left + 2, min(right + delta.x(), self.scene_size.width()))
        if "t" in edge:
            top = max(0, min(top + delta.y(), bottom - 2))
        if "b" in edge:
            bottom = max(top + 2, min(bottom + delta.y(), self.scene_size.height()))
        next_rect = QRectF(QPointF(left, top), QPointF(right, bottom))
        shift = self.selection.topLeft() - next_rect.topLeft()
        for mark in self.marks:
            mark.rect.translate(shift)
        self.selection = next_rect
        self._update_ui()

    def add_box(self, anchor: QPointF, end: QPointF, *, square=False, color=None):
        end = QPointF(max(0, min(end.x(), self.selection.width())), max(0, min(end.y(), self.selection.height())))
        if square:
            side = min(abs(end.x() - anchor.x()), abs(end.y() - anchor.y()))
            end = QPointF(anchor.x() + math.copysign(side, end.x() - anchor.x()), anchor.y() + math.copysign(side, end.y() - anchor.y()))
        rect = QRectF(anchor, end).normalized()
        if rect.width() >= 2 and rect.height() >= 2 and len(self.marks) < 500:
            self.save_state()
            self.marks.append(Mark("rect", rect, color or self.color))
            self.selected = len(self.marks) - 1
            self._update_ui()

    def begin_text(self, point: QPointF, index=-1):
        self.commit_text()
        if len(self.marks) >= 500 and index < 0:
            return
        mark = self.marks[index] if index >= 0 else None
        self._editing = index
        self._text_point = QPointF(point)
        if mark:
            self.color, self.font_size = mark.color, mark.font_size
        wrap = mark.wrap_width if mark else max(2, min(300, self.selection.width() - point.x()))
        editor = _TextInput(self)
        self._editor = editor
        editor.setAcceptRichText(False)
        editor.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOn)
        editor.document().setDocumentMargin(0)
        font = QFont(QApplication.font())
        font.setPixelSize(round(self.font_size * self.zoom))
        editor.setFont(font)
        editor.setPlainText(mark.text if mark else "")
        location = self._screen(self.selection.topLeft() + point)
        width = math.ceil(wrap * self.zoom) + editor.style().pixelMetric(QStyle.PixelMetric.PM_ScrollBarExtent) + 4
        height = max(round(self.font_size * self.zoom) + 12, min(180, round((self.selection.height() - point.y()) * self.zoom)))
        editor.setGeometry(round(location.x()), round(location.y()), width, height)
        editor.show()
        editor.resize(editor.width() + round(wrap * self.zoom) - editor.viewport().width(), editor.height())
        self.set_color(self.color)
        editor.accepted.connect(self.commit_text)
        editor.canceled.connect(self.cancel_text)
        if QGuiApplication.platformName() != "offscreen":
            editor.setFocus()
        self._update_ui()

    def commit_text(self):
        if not self._editor:
            return
        editor, index = self._editor, self._editing
        text, wrap = editor.toPlainText(), editor.viewport().width() / self.zoom
        self._editor, self._editing = None, -1
        editor.hide()
        editor.deleteLater()
        if text.strip():
            self.save_state()
            mark = Mark("text", QRectF(self._text_point, QSizeF(1, 1)), self.color, text, self.font_size, wrap)
            mark.rect.setSize(_document(mark).size())
            if index >= 0:
                self.marks[index] = mark
                self.selected = index
            else:
                self.marks.append(mark)
                self.selected = len(self.marks) - 1
        elif index >= 0:
            self.save_state()
            self.marks.pop(index)
            self.selected = -1
        self._update_ui()

    def cancel_text(self):
        if self._editor:
            self._editor.hide()
            self._editor.deleteLater()
        self._editor, self._editing = None, -1
        self._update_ui()

    def selected_image(self, annotated=True) -> QImage:
        self.commit_text()
        rect = self.selection
        if rect.width() < 2 or rect.height() < 2:
            raise ValueError("请先选择截图区域")
        source, source_rect, sx, sy = self._crop_source()
        left, top = math.floor(source_rect.left() * sx), math.floor(source_rect.top() * sy)
        right, bottom = math.ceil(source_rect.right() * sx), math.ceil(source_rect.bottom() * sy)
        raw = source.copy(QRect(left, top, right - left, bottom - top)).convertToFormat(QImage.Format.Format_ARGB32_Premultiplied)
        raw.setDevicePixelRatio(1)
        if annotated and self.marks:
            painter = QPainter(raw)
            painter.setRenderHint(QPainter.RenderHint.Antialiasing)
            painter.translate(source_rect.left() * sx - left, source_rect.top() * sy - top)
            painter.scale(sx, sy)
            for mark in self.marks:
                _draw_mark(painter, mark)
            painter.end()
        return raw

    def _crop_source(self):
        for geometry, image in self.frame.screens:
            local = QRectF(geometry).translated(-self.frame.geometry.x(), -self.frame.geometry.y())
            if local.contains(self.selection):
                return image, self.selection.translated(-local.x(), -local.y()), image.width() / local.width(), image.height() / local.height()
        return self.frame.image, self.selection, self.frame.image.width() / self.scene_size.width(), self.frame.image.height() / self.scene_size.height()

    def finish(self, action):
        if self.selection.width() < 2 or self.selection.height() < 2:
            return
        original = self.selected_image(False)
        image = self.selected_image(True)
        self.close()
        self.completed.emit(image, original, action)

    def _edge_at(self, point):
        rect, gap = self.selection, 7 / self.zoom
        if rect.isEmpty() or not rect.adjusted(-gap, -gap, gap, gap).contains(point):
            return ""
        result = "l" if abs(point.x() - rect.left()) <= gap else "r" if abs(point.x() - rect.right()) <= gap else ""
        result += "t" if abs(point.y() - rect.top()) <= gap else "b" if abs(point.y() - rect.bottom()) <= gap else ""
        return result

    def _mark_at(self, point):
        for index in range(len(self.marks) - 1, -1, -1):
            mark = self.marks[index]
            if mark.rect.adjusted(-5, -5, 5, 5).contains(point) and (mark.kind == "text" or not mark.rect.adjusted(5, 5, -5, -5).contains(point)):
                return index
        return -1

    def mousePressEvent(self, event: QMouseEvent):
        if event.button() == Qt.MouseButton.RightButton:
            if self._editor:
                self.cancel_text()
            elif self.selected >= 0:
                self.selected = -1
            elif self.selection.isEmpty():
                self.close()
            else:
                self.save_state()
                self.selection = QRectF()
                self.marks.clear()
            self._update_ui()
            return
        self.commit_text()
        if event.button() == Qt.MouseButton.MiddleButton or (event.button() == Qt.MouseButton.LeftButton and self._space):
            self._drag, self._pan_origin, self._pan_before = "pan", event.position(), QPointF(self.pan)
            return
        if event.button() != Qt.MouseButton.LeftButton:
            return
        point = self._clamp(self._world(event.position()))
        self._origin, self._origin_rect = point, QRectF(self.selection)
        self.selected = -1
        self._edge = self._edge_at(point)
        if self._edge:
            self.save_state()
            self._drag = "resize"
        elif self.selection.contains(point):
            local = point - self.selection.topLeft()
            if self.mode == "text":
                self.begin_text(local)
                return
            if self.mode in ("rect", "square"):
                self._drag = "square" if self.mode == "square" or event.modifiers() & Qt.KeyboardModifier.ShiftModifier else "rect"
            else:
                self.selected = self._mark_at(local)
                self.save_state()
                self._drag = "mark" if self.selected >= 0 else "move"
                if self.selected >= 0:
                    self._origin_rect = QRectF(self.marks[self.selected].rect)
                    self.color = self.marks[self.selected].color
        else:
            self.save_state()
            self._drag, self.mode = "new", "move"
            self.marks.clear()
            self.selection = QRectF(point, point)
        self._update_ui()

    def mouseDoubleClickEvent(self, event):
        point = self._world(event.position())
        if self.mode == "move" and self.selection.contains(point):
            index = self._mark_at(point - self.selection.topLeft())
            if index >= 0 and self.marks[index].kind == "text":
                self._drag = ""
                self.begin_text(self.marks[index].rect.topLeft(), index)

    def mouseMoveEvent(self, event):
        point = self._clamp(self._world(event.position()))
        delta = point - self._origin
        if self._drag == "pan":
            self.pan = self._pan_before + event.position() - self._pan_origin
            self._limit_pan()
        elif self._drag == "new":
            self.selection = QRectF(self._origin, point).normalized()
        elif self._drag == "move":
            self.selection = QRectF(self._origin_rect)
            self.move_selection(delta, remember=False)
        elif self._drag == "resize":
            self.resize_selection(delta, self._edge, remember=False, origin=self._origin_rect)
        elif self._drag == "mark" and self.selected >= 0:
            rect = QRectF(self._origin_rect)
            rect.translate(delta)
            self.marks[self.selected].rect = rect
        elif self._drag in ("rect", "square"):
            anchor, end = self._origin - self.selection.topLeft(), point - self.selection.topLeft()
            end = QPointF(max(0, min(end.x(), self.selection.width())), max(0, min(end.y(), self.selection.height())))
            if self._drag == "square":
                side = min(abs(end.x() - anchor.x()), abs(end.y() - anchor.y()))
                end = QPointF(anchor.x() + math.copysign(side, end.x() - anchor.x()), anchor.y() + math.copysign(side, end.y() - anchor.y()))
            self._pending = Mark("rect", QRectF(anchor, end).normalized(), self.color)
        else:
            edge = self._edge_at(point)
            cursor = Qt.CursorShape.SizeFDiagCursor if edge in ("lt", "rb") else Qt.CursorShape.SizeBDiagCursor if edge in ("lb", "rt") else Qt.CursorShape.SizeHorCursor if edge in ("l", "r") else Qt.CursorShape.SizeVerCursor if edge else Qt.CursorShape.IBeamCursor if self.mode == "text" else Qt.CursorShape.SizeAllCursor if self.selection.contains(point) else Qt.CursorShape.CrossCursor
            self.setCursor(cursor)
            return
        self._update_ui()

    def mouseReleaseEvent(self, event):
        if self._pending and self._pending.rect.width() >= 2 and self._pending.rect.height() >= 2 and len(self.marks) < 500:
            self.save_state()
            self.marks.append(self._pending)
            self.selected = len(self.marks) - 1
        self._pending, self._drag = None, ""
        self._update_ui()

    def wheelEvent(self, event: QWheelEvent):
        self.set_zoom(self.zoom * (1.2 if event.angleDelta().y() > 0 else 1 / 1.2), event.position())
        event.accept()

    def keyPressEvent(self, event):
        key, control = event.key(), bool(event.modifiers() & Qt.KeyboardModifier.ControlModifier)
        if key == Qt.Key.Key_Escape:
            self.close()
        elif key in (Qt.Key.Key_Return, Qt.Key.Key_Enter) or control and key == Qt.Key.Key_C:
            self.finish("copy")
        elif key == Qt.Key.Key_F3:
            self.finish("pin")
        elif key == Qt.Key.Key_Space:
            self._space = True
        elif control and key == Qt.Key.Key_Z:
            self.undo()
        elif control and key == Qt.Key.Key_S:
            self.finish("save")
        elif key == Qt.Key.Key_Delete:
            self.delete_selected()
        elif key in (Qt.Key.Key_Plus, Qt.Key.Key_Equal, Qt.Key.Key_Minus):
            self.set_zoom(self.zoom * (1 / 1.2 if key == Qt.Key.Key_Minus else 1.2))
        elif control and key == Qt.Key.Key_0:
            self.set_zoom(1)
        elif not control and key in (Qt.Key.Key_V, Qt.Key.Key_T, Qt.Key.Key_R, Qt.Key.Key_S):
            self.set_tool({Qt.Key.Key_V: "move", Qt.Key.Key_T: "text", Qt.Key.Key_R: "rect", Qt.Key.Key_S: "square"}[key])
        else:
            super().keyPressEvent(event)

    def keyReleaseEvent(self, event):
        if event.key() == Qt.Key.Key_Space:
            self._space = False
        super().keyReleaseEvent(event)

    def _update_ui(self):
        valid = self.selection.width() >= 2 and self.selection.height() >= 2
        self.toolbar.setVisible(valid and not self._drag)
        self._size.setVisible(valid)
        self._tips.setVisible(not valid)
        self.zoom_label.setText(f"{self.zoom:.0%}")
        for mode, button in self.tool_buttons.items():
            button.setChecked(self.mode == mode)
        if valid:
            _, crop, sx, sy = self._crop_source()
            self._size.setText(f"{math.ceil(crop.right()*sx)-math.floor(crop.left()*sx)} × {math.ceil(crop.bottom()*sy)-math.floor(crop.top()*sy)} px · {self.zoom:.0%} · 标注 {len(self.marks)}")
            self._size.adjustSize()
            top, bottom = self._screen(self.selection.topLeft()), self._screen(self.selection.bottomRight())
            self._size.move(max(4, min(round(top.x()), self.width() - self._size.width() - 4)), max(4, min(round(top.y() - 30), self.height() - 30)))
            self.toolbar.adjustSize()
            self.toolbar.move(max(4, min(round(bottom.x() - self.toolbar.width()), self.width() - self.toolbar.width() - 4)), max(4, min(round(bottom.y() + 8), self.height() - self.toolbar.height() - 4)))
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#152031"))
        painter.save()
        painter.translate(self.pan)
        painter.scale(self.zoom, self.zoom)
        painter.drawImage(QRectF(QPointF(), self.scene_size), self.frame.image)
        shade = QPainterPath()
        shade.addRect(QRectF(QPointF(), self.scene_size))
        if not self.selection.isEmpty():
            hole = QPainterPath()
            hole.addRect(self.selection)
            shade = shade.subtracted(hole)
        painter.fillPath(shade, QColor(0, 0, 0, 125))
        if self.selection.width() >= 2 and self.selection.height() >= 2:
            painter.save()
            painter.setClipRect(self.selection)
            painter.translate(self.selection.topLeft())
            painter.setRenderHint(QPainter.RenderHint.Antialiasing)
            for index, mark in enumerate(self.marks):
                if index != self._editing:
                    _draw_mark(painter, mark)
            if self._pending:
                _draw_mark(painter, self._pending)
            if 0 <= self.selected < len(self.marks) and self.selected != self._editing:
                painter.setPen(QPen(QColor("#3065e8"), 1 / self.zoom, Qt.PenStyle.DashLine))
                painter.setBrush(Qt.BrushStyle.NoBrush)
                painter.drawRect(self.marks[self.selected].rect.adjusted(-3, -3, 3, 3))
            painter.restore()
            painter.setPen(QPen(QColor("#3065e8"), 1.5 / self.zoom))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawRect(self.selection)
            painter.setBrush(Qt.GlobalColor.white)
            rect = self.selection
            for point in (rect.topLeft(), rect.topRight(), rect.bottomLeft(), rect.bottomRight(), QPointF(rect.center().x(), rect.top()), QPointF(rect.center().x(), rect.bottom()), QPointF(rect.left(), rect.center().y()), QPointF(rect.right(), rect.center().y())):
                size = 6 / self.zoom
                painter.drawRect(QRectF(point.x() - size / 2, point.y() - size / 2, size, size))
        painter.restore()


class PinnedImageWindow(QWidget):
    def __init__(self, image: QImage, copy, recognize, parent=None):
        super().__init__(parent, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool)
        self.image = image.copy()
        self.scale = 1.0
        self._drag = None
        self.setWindowTitle("LinkLab 贴图 · 滚轮缩放 · Ctrl+滚轮透明度")
        self.setStyleSheet("background:white;color:#24334a; QPushButton{background:#f6f8fc;color:#24334a;border:1px solid #dce3ee;border-radius:5px;padding:6px;}")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(5, 5, 5, 5)
        bar = QHBoxLayout()
        layout.addLayout(bar)
        for label, action in (("复制", lambda: copy(self.image)), ("识别", lambda: recognize(self.image)), ("−", lambda: self.set_scale(self.scale / 1.15)), ("+", lambda: self.set_scale(self.scale * 1.15)), ("100%", lambda: self.set_scale(1)), ("适应", self.fit), ("关闭", self.close)):
            button = QPushButton(label)
            button.clicked.connect(action)
            bar.addWidget(button)
        self.scale_label = QLabel()
        bar.addWidget(self.scale_label)
        self.picture = QLabel()
        self.picture.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.picture.setMouseTracking(True)
        self.picture.installEventFilter(self)
        self.scroll = QScrollArea()
        self.scroll.setWidget(self.picture)
        self.scroll.setWidgetResizable(False)
        layout.addWidget(self.scroll)
        layout.addWidget(QLabel("拖动移动 · 滚轮 / ± 缩放 · Ctrl+滚轮透明度 · Esc 关闭"))
        self.setMinimumSize(320, 160)
        self.set_scale(min(1., 850 / image.width(), 600 / image.height()))

    def set_scale(self, scale):
        from PySide6.QtGui import QPixmap
        self.scale = max(.1, min(8., float(scale)))
        ratio = self.devicePixelRatioF()
        pixmap = QPixmap.fromImage(self.image).scaled(max(1, round(self.image.width() * self.scale)), max(1, round(self.image.height() * self.scale)), Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
        pixmap.setDevicePixelRatio(ratio)
        self.picture.setPixmap(pixmap)
        self.picture.resize(round(pixmap.width() / ratio), round(pixmap.height() / ratio))
        screen = self.screen().availableGeometry()
        self.resize(max(320, min(self.picture.width() + 14, round(screen.width() * .9))), max(160, min(self.picture.height() + 100, round(screen.height() * .9))))
        self.scale_label.setText(f"{self.scale:.0%}")

    def fit(self):
        self.set_scale(min(self.scroll.viewport().width() * self.devicePixelRatioF() / self.image.width(), self.scroll.viewport().height() * self.devicePixelRatioF() / self.image.height()))

    def eventFilter(self, watched, event):
        from PySide6.QtCore import QEvent
        if event.type() == QEvent.Type.MouseButtonPress and event.button() == Qt.MouseButton.LeftButton:
            self._drag = event.globalPosition() - QPointF(self.pos())
            return True
        if event.type() == QEvent.Type.MouseMove and self._drag is not None:
            self.move((event.globalPosition() - self._drag).toPoint())
            return True
        if event.type() == QEvent.Type.MouseButtonRelease:
            self._drag = None
        if event.type() == QEvent.Type.Wheel:
            self.wheelEvent(event)
            return True
        return super().eventFilter(watched, event)

    def wheelEvent(self, event):
        if event.modifiers() & Qt.KeyboardModifier.ControlModifier:
            self.setWindowOpacity(max(.2, min(1., self.windowOpacity() + (.1 if event.angleDelta().y() > 0 else -.1))))
        else:
            self.set_scale(self.scale * (1.15 if event.angleDelta().y() > 0 else 1 / 1.15))
        event.accept()

    def keyPressEvent(self, event):
        if event.key() == Qt.Key.Key_Escape:
            self.close()
        elif event.key() in (Qt.Key.Key_Plus, Qt.Key.Key_Equal, Qt.Key.Key_Minus):
            self.set_scale(self.scale * (1 / 1.15 if event.key() == Qt.Key.Key_Minus else 1.15))
        elif event.key() == Qt.Key.Key_0 and event.modifiers() & Qt.KeyboardModifier.ControlModifier:
            self.set_scale(1)
        else:
            super().keyPressEvent(event)


class CaptureController(QObject):
    enabled_changed = Signal(bool)
    image_changed = Signal(object)
    ocr_ready = Signal(str)
    _ocr_finished = Signal(object, object)

    def __init__(self, hub, settings, parent=None, *, test_mode=False):
        super().__init__(parent)
        self.hub, self.settings, self.test_mode = hub, settings, test_mode
        self.store = settings
        self.enabled = False
        self._closed = False
        self._capturing = False
        self._generation = 0
        self._overlay: CaptureOverlay | None = None
        self._floating: list[QWidget] = []
        self.last_image = QImage()
        self.original_image = QImage()
        self._recognizing = False
        self._ocr_thread = None
        self._ocr_cancel = threading.Event()
        self.hotkeys = GlobalHotkeys(self, test_mode=test_mode)
        self.hotkeys.capture_requested.connect(self.capture)
        self.hotkeys.pin_requested.connect(self.pin_clipboard)
        self._ocr_finished.connect(self._on_ocr_finished)

    def _notice(self, text):
        self.hub.set_notice(str(text))

    def set_enabled(self, enabled: bool) -> bool:
        if self._closed:
            return False
        try:
            if enabled:
                self.hotkeys.enable()
            else:
                self._generation += 1
                self.hotkeys.close()
                if self._overlay:
                    self._overlay.close()
            self.enabled = bool(enabled)
            self.enabled_changed.emit(self.enabled)
            self._notice("F1 截图 / F3 贴图全局热键已开启" if enabled else "截图全局热键已关闭，页面按钮仍可使用")
            return True
        except Exception as exc:
            self.enabled = False
            self.enabled_changed.emit(False)
            self._notice(str(exc))
            return False

    def set_image(self, image: QImage, original: QImage | None = None):
        self.last_image = image.copy()
        self.original_image = (original if original is not None else image).copy()
        self.image_changed.emit(self.last_image)

    def capture(self):
        if self._closed or self._capturing:
            return
        if self.test_mode:
            self._notice("测试模式不会读取真实屏幕；可使用合成图测试编辑器")
            return
        self._capturing = True
        generation = self._generation
        owner = self.parent().window() if isinstance(self.parent(), QWidget) else None
        restore = bool(owner and owner.isVisible())
        if restore:
            owner.hide()

        def perform():
            if self._closed or generation != self._generation:
                self._capturing = False
                if restore and not self._closed:
                    owner.show()
                return
            try:
                self._overlay = CaptureOverlay(capture_desktop())
                self._overlay.completed.connect(self._completed)
                self._overlay.destroyed.connect(lambda: None)

                def closed():
                    self._overlay = None
                    self._capturing = False
                    if restore and not self._closed:
                        owner.show()

                self._overlay.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
                self._overlay.destroyed.connect(closed)
                self._overlay.show()
                self._overlay.activateWindow()
                self._overlay.setFocus()
            except Exception as exc:
                self._capturing = False
                self._notice(str(exc))
                if restore and not self._closed:
                    owner.show()

        QTimer.singleShot(150, perform)

    def _completed(self, image, original, action):
        self.set_image(image, original)
        if action == "copy":
            self.copy_image(image)
        elif action == "save":
            self.save_image(image)
        elif action == "pin":
            self.pin(image, original)
        elif action == "ocr":
            self.recognize(original)

    def copy_image(self, image=None):
        image = image if image is not None else self.last_image
        if image.isNull():
            self._notice("请先截图或打开图片")
            return
        QApplication.clipboard().setImage(image)
        self._notice("图片与标注已复制，F3 可贴图")

    def save_image(self, image=None):
        image = image if image is not None else self.last_image
        if image.isNull():
            self._notice("请先截图或打开图片")
            return
        filename, _ = QFileDialog.getSaveFileName(None, "保存截图", "LinkLab.png", "PNG 图片 (*.png)")
        if filename:
            if not image.save(filename, "PNG"):
                self._notice("保存图片失败，请检查目录权限")
            else:
                self._notice("截图已保存：" + filename)

    def open_image(self):
        filename, _ = QFileDialog.getOpenFileName(None, "打开图片", "", "图片 (*.png *.jpg *.jpeg *.bmp *.webp)")
        if filename:
            image = QImage(filename)
            if image.isNull() or image.width() * image.height() > 50_000_000:
                self._notice("图片无法读取或超过 5000 万像素")
                return
            self.set_image(image)
            self._notice("图片已打开，可贴图或本机文字识别")

    def pin_clipboard(self):
        if self._closed:
            return
        if self._overlay:
            self._overlay.finish("pin")
            return
        image = QApplication.clipboard().image()
        if image.isNull():
            image = self.last_image
        if image.isNull():
            self._notice("剪贴板没有图片，请先截图或打开图片")
            return
        original = self.original_image if image == self.last_image and not self.original_image.isNull() else image
        self.set_image(image, original)
        self.pin(image, original)

    def pin(self, image=None, original=None):
        if self._closed:
            return
        image = image if image is not None else self.last_image
        if image.isNull():
            return
        if len(self._floating) >= 12:
            self._notice("最多同时打开 12 个浮窗，请先关闭部分浮窗")
            return
        source = original if original is not None else image
        window = PinnedImageWindow(image, self.copy_image, lambda _: self.recognize(source))
        if self.test_mode:
            window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen)
        window.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        self._floating.append(window)
        window.destroyed.connect(lambda: self._floating.remove(window) if window in self._floating else None)
        window.show()
        self._notice("贴图已创建，滚轮缩放，Ctrl+滚轮调透明度")
        return window

    def recognize(self, image=None):
        if self._closed:
            return
        image = image if image is not None else self.original_image
        if image.isNull():
            self._notice("请先截图或打开图片")
            return
        if self._recognizing:
            self._notice("正在识别，请稍候")
            return
        self._recognizing = True
        self._ocr_cancel.clear()
        self._notice("正在本机识别文字，图片不会上传…")
        source = image.copy()
        language = self.settings.get("ocr_language", "chi_sim+eng")

        def work():
            try:
                result, error = ocr.recognize(source, language, cancel=self._ocr_cancel), None
            except Exception as exc:
                result, error = "", str(exc)
            if not self._closed:
                self._ocr_finished.emit(result, error)

        self._ocr_thread = threading.Thread(target=work, name="LinkLab-OCR", daemon=True)
        self._ocr_thread.start()

    def _on_ocr_finished(self, result, error):
        self._recognizing = False
        if self._closed:
            return
        if error:
            self._notice(error)
            return
        self.ocr_ready.emit(result)
        self._notice(f"识别完成：{len(result)} 个字符，可编辑后复制" if result else "没有识别到文字，请选择清晰的文字区域")
        if result and not self.test_mode:
            window = QWidget(None, Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool)
            window.setWindowTitle("LinkLab · 文字识别结果")
            window.setStyleSheet("background:white;color:#24334a;")
            window.resize(680, 420)
            layout = QVBoxLayout(window)
            layout.addWidget(QLabel("识别结果 · 可编辑后复制"))
            edit = QTextEdit()
            edit.setPlainText(result)
            layout.addWidget(edit)
            copy = QPushButton("复制全部文字")
            copy.clicked.connect(lambda checked=False: QApplication.clipboard().setText(edit.toPlainText()))
            layout.addWidget(copy)
            window.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
            self._floating.append(window)
            window.destroyed.connect(lambda: self._floating.remove(window) if window in self._floating else None)
            window.show()

    def close_resources(self):
        self._closed = True
        self._generation += 1
        self._ocr_cancel.set()
        self.hotkeys.close()
        self.enabled = False
        if self._overlay:
            self._overlay.close()
        for window in self._floating[:]:
            window.close()
        self._floating.clear()
        if self._ocr_thread and self._ocr_thread.is_alive():
            self._ocr_thread.join(timeout=1)
