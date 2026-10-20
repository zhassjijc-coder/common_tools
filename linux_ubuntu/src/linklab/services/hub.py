from __future__ import annotations

import threading
from datetime import datetime, timezone
from collections import deque
from PySide6.QtCore import QObject, Signal, Slot


class Hub(QObject):
    event = Signal(object)
    notice = Signal(str)
    connections_changed = Signal()
    counters_changed = Signal(int, int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.events = deque(maxlen=10000)
        self.connections = {}
        self.tx = self.rx = 0
        self._lock = threading.Lock()
        self.event.connect(self._record)

    def log(self, source, direction, data=b"", note=""):
        raw = bytes(data)
        self.event.emit({"time": datetime.now(timezone.utc).astimezone().isoformat(timespec="milliseconds"),
                         "source": str(source), "direction": str(direction), "data": raw,
                         "hex": raw.hex(" ").upper(), "text": raw.decode("utf-8", errors="replace"), "note": str(note)})

    @Slot(object)
    def _record(self, entry):
        self.events.append(entry)
        size = len(entry["data"])
        if entry["direction"].upper() in ("TX", "SEND", "发送"):
            self.tx += size
        elif entry["direction"].upper() in ("RX", "RECEIVE", "接收"):
            self.rx += size
        self.counters_changed.emit(self.tx, self.rx)

    def set_notice(self, message):
        self.notice.emit(str(message))

    def register(self, name, sender):
        self.connections[name] = sender
        self.connections_changed.emit()

    def unregister(self, name):
        self.connections.pop(name, None)
        self.connections_changed.emit()

    def clear(self):
        self.events.clear()
        self.tx = self.rx = 0
        self.counters_changed.emit(0, 0)
