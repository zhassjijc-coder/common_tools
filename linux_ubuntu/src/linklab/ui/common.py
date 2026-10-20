from __future__ import annotations

from PySide6.QtCore import QObject, QRunnable, QThreadPool, Signal, Qt
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QLabel, QFrame,
    QPushButton, QComboBox, QLineEdit, QSpinBox, QFormLayout, QScrollArea,
    QPlainTextEdit, QTableWidget, QHeaderView, QAbstractItemView, QGridLayout)
from .widgets import SelectBox, DataTable, configure_columns, set_cell


class ResultSignals(QObject):
    done = Signal(object)
    failed = Signal(str)
    finished = Signal()


class Job(QRunnable):
    def __init__(self, work):
        super().__init__()
        self.work = work
        self.signals = ResultSignals()

    def run(self):
        try:
            self.signals.done.emit(self.work())
        except Exception as exc:
            self.signals.failed.emit(str(exc) or type(exc).__name__)
        finally:
            self.signals.finished.emit()


class Page(QWidget):
    busy_changed = Signal(bool)

    def __init__(self, hub, parent=None):
        super().__init__(parent)
        self.hub = hub
        self.busy = False
        self._jobs = set()
        self.closed = False
        self.outer = QVBoxLayout(self)
        self.outer.setContentsMargins(0, 0, 0, 0)
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setFrameShape(QFrame.Shape.NoFrame)
        self.body = QWidget()
        self.body.setObjectName("canvas")
        self.layout = QVBoxLayout(self.body)
        self.layout.setContentsMargins(2, 2, 12, 12)
        self.layout.setSpacing(18)
        self.scroll.setWidget(self.body)
        self.outer.addWidget(self.scroll)

    def submit(self, work, done=None):
        if self.busy or self.closed:
            return False
        self.busy = True
        job = Job(work)
        self._jobs.add(job)
        result = {}
        job.signals.done.connect(lambda value: result.update(value=value))
        def finished():
            self.busy = False
            self._jobs.discard(job)
            try:
                if done and not self.closed and "value" in result:
                    done(result["value"])
            except Exception as exc:
                self.hub.set_notice(str(exc))
                self.operation_failed(str(exc))
            finally:
                self.busy_changed.emit(self.busy)
        job.signals.failed.connect(self.hub.set_notice)
        job.signals.failed.connect(self.operation_failed)
        job.signals.finished.connect(finished)
        self.busy_changed.emit(True)
        QThreadPool.globalInstance().start(job)
        return True

    def operation_failed(self, message):
        # Stop periodic retries after an error; leave reconnection as an explicit action.
        stop_sender = getattr(getattr(self, "sender", None), "stop", None)
        if callable(stop_sender):
            stop_sender()
        poll = getattr(self, "poll", None)
        if poll is not None:
            poll.setChecked(False)

    def card(self, title, description=""):
        frame = QFrame()
        frame.setObjectName("card")
        layout = QVBoxLayout(frame)
        layout.setContentsMargins(22, 20, 22, 22)
        layout.setSpacing(14)
        heading = QLabel(title)
        heading.setObjectName("cardTitle")
        layout.addWidget(heading)
        if description:
            label = QLabel(description)
            label.setObjectName("muted")
            label.setWordWrap(True)
            layout.addWidget(label)
        self.layout.addWidget(frame)
        return layout

    def close_resources(self):
        self.closed = True


def combo(items, current=None, editable=False):
    box = SelectBox()
    box.addItems([str(x) for x in items])
    box.setEditable(editable)
    box.setMinimumWidth(135)
    box.setMaxVisibleItems(12)
    box.setSizeAdjustPolicy(QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon)
    box.setMinimumContentsLength(10)
    if current is not None:
        box.setCurrentText(str(current))
    return box


def line(text="", placeholder=""):
    edit = QLineEdit(str(text))
    edit.setPlaceholderText(placeholder)
    edit.setMinimumWidth(130)
    return edit


def spin(value=1, low=0, high=65535):
    box = QSpinBox()
    box.setRange(low, high)
    box.setValue(value)
    box.setMinimumWidth(100)
    return box


def button(title, action, primary=False):
    result = QPushButton(title)
    result.setCursor(Qt.CursorShape.PointingHandCursor)
    result.setProperty("primary", primary)
    result.clicked.connect(action)
    return result


def row(*widgets):
    layout = QHBoxLayout()
    layout.setSpacing(12)
    for widget in widgets:
        if isinstance(widget, str):
            layout.addWidget(QLabel(widget))
        else:
            layout.addWidget(widget)
    layout.addStretch()
    return layout


def form(**fields):
    layout = QGridLayout()
    layout.setHorizontalSpacing(14)
    layout.setVerticalSpacing(12)
    for index, (label, widget) in enumerate(fields.items()):
        column = (index % 2) * 2
        layout.addWidget(QLabel(label.replace("_", " ")), index // 2, column)
        layout.addWidget(widget, index // 2, column + 1)
    layout.setColumnStretch(1, 1)
    layout.setColumnStretch(3, 1)
    return layout


def text_area(text="", readonly=False, height=135):
    box = QPlainTextEdit(text)
    box.setReadOnly(readonly)
    box.setMinimumHeight(height)
    box.setProperty("code", True)
    return box


def table(headers, *, column_weights=None, min_height=220, empty_text="暂无数据", empty_hint="连接设备或执行读取后，结果会显示在这里。", compact=False):
    return DataTable(headers, column_weights=column_weights, min_height=min_height, empty_text=empty_text, empty_hint=empty_hint, compact=compact)
