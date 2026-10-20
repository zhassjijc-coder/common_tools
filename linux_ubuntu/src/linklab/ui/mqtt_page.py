from __future__ import annotations

from dataclasses import replace
import csv
import json
from pathlib import Path
import queue
import time

from PySide6.QtCore import QObject, QTimer, Signal, Qt
from PySide6.QtWidgets import (QCheckBox, QFileDialog, QFormLayout, QGridLayout,
    QHBoxLayout, QLabel, QLineEdit, QSizePolicy, QVBoxLayout, QWidget)

from .common import (Page, button, combo, configure_columns, line, row,
    set_cell, spin, table, text_area)
from ..services.mqtt import MqttMessage, MqttService, MqttSettings, payload_bytes


class _Signals(QObject):
    state = Signal(str, bool)
    ack = Signal(int, str)


def _field(title, widget):
    container = QWidget()
    layout = QVBoxLayout(container)
    layout.setContentsMargins(0, 0, 0, 0)
    layout.setSpacing(7)
    label = QLabel(title)
    label.setObjectName("muted")
    label.setProperty("fieldLabel", True)
    layout.addWidget(label)
    layout.addWidget(widget)
    return container


def _compact_choice(widget, width=88):
    widget.setMinimumWidth(width)
    widget.setMaximumWidth(width)
    widget.setMinimumContentsLength(1)
    return widget


class MqttPage(Page):
    """MQTT workbench. Instantiation is intentionally offline."""
    def __init__(self, hub):
        super().__init__(hub)
        self.setObjectName("mqttPage")
        self._signals = _Signals(self)
        self._signals.state.connect(self._state)
        self._signals.ack.connect(self._ack)
        self._pending: queue.Queue[MqttMessage] = queue.Queue(maxsize=1000)
        self.messages: list[MqttMessage] = []
        self.dropped = 0
        self.service = MqttService(on_state=self._signals.state.emit,
            on_message=self._enqueue, on_ack=self._signals.ack.emit)
        hero = self.card("MQTT 调试")
        heading = hero.takeAt(0).widget()
        hero_row = QHBoxLayout()
        introduction = QVBoxLayout(); introduction.setSpacing(7)
        introduction.addWidget(heading)
        description = QLabel("连接 Broker，订阅设备消息，发布你的测试报文。")
        description.setObjectName("muted"); description.setWordWrap(True)
        introduction.addWidget(description)
        hero_row.addLayout(introduction, 1)
        hero_row.addWidget(button("保存模板", self.save_profile))
        hero_row.addWidget(button("载入模板", self.load_profile))
        hero.addLayout(hero_row)
        connection = self.card("01  连接配置", "MQTT 3.1.1 / 5.0，支持 TCP、TLS、WebSocket 和 WSS。")
        self.configuration = QWidget(); config = QGridLayout(self.configuration)
        config.setContentsMargins(0, 0, 0, 0)
        config.setHorizontalSpacing(14); config.setVerticalSpacing(16)
        for column, stretch in enumerate((2, 1, 1, 1)):
            config.setColumnStretch(column, stretch)
        self.transport = combo(("TCP", "TLS", "WS", "WSS"), "TCP")
        self.protocol = combo(("3.1.1", "5.0"), "5.0")
        self.host = line("127.0.0.1", "IP / 主机名；WS 填完整 URL")
        self.port = spin(1883, 1, 65535)
        self.client_id = line(MqttSettings().client_id)
        self.username = line(); self.password = line(); self.password.setEchoMode(QLineEdit.EchoMode.Password)
        self.password.setPlaceholderText("仅本次连接使用")
        config.addWidget(_field("Broker / 完整 WebSocket URL", self.host), 0, 0, 1, 2)
        config.addWidget(_field("TCP 端口", self.port), 0, 2)
        config.addWidget(_field("传输方式", self.transport), 0, 3)
        config.addWidget(_field("Client ID", self.client_id), 1, 0)
        config.addWidget(_field("协议版本", self.protocol), 1, 1)
        config.addWidget(_field("用户名", self.username), 1, 2)
        config.addWidget(_field("密码（不保存）", self.password), 1, 3)
        self.advanced = QWidget(); advanced = QFormLayout(self.advanced)
        self.keepalive = spin(60, 5, 65535); self.timeout = spin(5000, 200, 60000); self.session_expiry = spin(3600, 0, 604800)
        self.clean_session = QCheckBox("Clean Session / Clean Start"); self.clean_session.setChecked(True)
        self.reconnect = QCheckBox("断线自动重连并恢复已确认订阅"); self.reconnect.setChecked(True)
        self.ca_file = line(); self.cert_file = line(); self.key_file = line()
        self.key_password = line(); self.key_password.setEchoMode(QLineEdit.EchoMode.Password)
        self.will_topic = line(); self.will_payload = line("offline"); self.will_qos = combo((0, 1, 2), 0); self.will_retain = QCheckBox("遗嘱 Retain")
        advanced.addRow("Keep Alive / 秒", self.keepalive); advanced.addRow("连接超时 / ms", self.timeout)
        advanced.addRow("MQTT 5 会话过期 / 秒", self.session_expiry); advanced.addRow(self.clean_session); advanced.addRow(self.reconnect)
        for title, widget in (("CA PEM（留空用系统信任）", self.ca_file), ("客户端证书 PEM", self.cert_file), ("客户端私钥 PEM", self.key_file)):
            container = QWidget(); layout = QHBoxLayout(container); layout.setContentsMargins(0, 0, 0, 0); layout.addWidget(widget)
            layout.addWidget(button("选择", lambda checked=False, field=widget: self._certificate(field)))
            advanced.addRow(title, container)
        advanced.addRow("私钥密码（不保存）", self.key_password)
        advanced.addRow("遗嘱主题（留空关闭）", self.will_topic); advanced.addRow("遗嘱负载 / UTF-8", self.will_payload)
        advanced.addRow("遗嘱 QoS", self.will_qos); advanced.addRow(self.will_retain)
        self.advanced.setVisible(False)
        advanced_toggle = QHBoxLayout()
        advanced_toggle.addWidget(button("会话、TLS 证书与遗嘱 ▾", lambda: self.advanced.setVisible(not self.advanced.isVisible())))
        advanced_toggle.addStretch()
        config.addLayout(advanced_toggle, 2, 0, 1, 4)
        config.addWidget(self.advanced, 3, 0, 1, 4); connection.addWidget(self.configuration)
        self.connect_button = button("连接 Broker", self.toggle_connection, True)
        self.disconnect_button = button("取消 / 断开", self.disconnect)
        connection.addLayout(row(self.connect_button, self.disconnect_button))
        self.status = QLabel("准备就绪 · 点击连接后才开始网络通信"); self.status.setWordWrap(True); self.status.setTextFormat(Qt.TextFormat.PlainText)
        self.status.setObjectName("statusBanner"); self.status.setProperty("statusTone", "neutral")
        self.status.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Minimum)
        connection.addWidget(self.status)
        tls_note = QLabel("TLS 验证服务端证书及主机名；客户端双向认证使用 PEM 证书与私钥，不使用 Windows PFX。")
        tls_note.setObjectName("muted"); tls_note.setWordWrap(True); connection.addWidget(tls_note)

        subscription = self.card("02  订阅", "支持 + / # 通配符，例如 devices/+/status。收到 SUBACK 后才加入已确认列表。")
        self.subscribe_topic = line("linklab/#"); self.subscribe_qos = combo((0, 1, 2), 1)
        _compact_choice(self.subscribe_qos)
        self.subscribe_button = button("订阅", self.subscribe, True); self.unsubscribe_button = button("取消选中订阅", self.unsubscribe)
        subscribe_row = QHBoxLayout(); subscribe_row.setSpacing(10)
        subscribe_row.addWidget(self.subscribe_topic, 1); subscribe_row.addWidget(QLabel("QoS")); subscribe_row.addWidget(self.subscribe_qos)
        subscribe_row.addWidget(self.subscribe_button); subscribe_row.addWidget(self.unsubscribe_button)
        subscription.addLayout(subscribe_row)
        self.subscriptions = table(("已确认主题", "QoS"), min_height=172,
            empty_text="还没有已确认的订阅", empty_hint="连接 Broker 后添加主题，收到订阅应答后显示在这里。")
        configure_columns(self.subscriptions, [None, 88], stretch_last=False, minimum_stretch=240)
        self.subscriptions.setMaximumHeight(230); subscription.addWidget(self.subscriptions)
        publish = self.card("03  发布消息", "空负载 + Retain 可清除对应主题的保留消息；单条调试负载最多 1 MiB。")
        self.publish_topic = line("linklab/device/status"); self.publish_format = combo(("Text", "JSON", "HEX"), "JSON")
        self.publish_qos = combo((0, 1, 2), 1); self.retain = QCheckBox("Retain")
        _compact_choice(self.publish_qos); _compact_choice(self.publish_format, 108)
        publish_row = QHBoxLayout(); publish_row.setSpacing(12)
        publish_row.addWidget(_field("发布主题", self.publish_topic), 1)
        publish_row.addWidget(_field("负载格式", self.publish_format))
        publish_row.addWidget(_field("QoS", self.publish_qos))
        publish_row.addWidget(self.retain, 0, Qt.AlignmentFlag.AlignBottom)
        publish.addLayout(publish_row)
        self.payload = text_area('{\n  "device": "demo-01",\n  "temperature": 23.5\n}', height=145); publish.addWidget(self.payload)
        self.publish_button = button("发布消息 ↗", self.publish, True)
        publish.addLayout(row(self.publish_button, button("格式化 JSON", self.format_json)))
        timeline = self.card("04  消息时间线", "最近 500 条；单条最多展示 64 KiB。QoS 0 的已发送不表示 Broker 已确认。")
        self.filter = line("", "筛选主题或负载"); self.direction = combo(("全部", "TX", "RX"), "全部")
        _compact_choice(self.direction, 96)
        filter_row = QHBoxLayout(); filter_row.setSpacing(10)
        filter_row.addWidget(self.filter, 1); filter_row.addWidget(self.direction)
        filter_row.addWidget(button("导出筛选结果", self.export_messages)); filter_row.addWidget(button("清空", self.clear_messages))
        timeline.addLayout(filter_row)
        self.grid = table(("时间", "方向", "主题", "QoS", "Retain", "字节", "状态"), min_height=270,
            empty_text="等待第一条消息", empty_hint="连接 Broker 后订阅或发布消息，收发记录将显示在这里。")
        configure_columns(self.grid, [92, 68, None, 62, 76, 84, 200], stretch_last=False, minimum_stretch=260)
        self.grid.setObjectName("mqttMessages")
        self.grid.setSortingEnabled(False)
        timeline.addWidget(self.grid)
        self.count = QLabel("0 条消息"); self.count.setObjectName("muted"); timeline.addWidget(self.count)
        self.display_format = combo(("Text", "JSON", "HEX"), "Text"); _compact_choice(self.display_format, 108)
        detail_header = QHBoxLayout(); detail_header.addWidget(QLabel("消息详情")); detail_header.addStretch(); detail_header.addWidget(self.display_format)
        timeline.addLayout(detail_header)
        self.detail = text_area("选择消息查看完整展示负载。", readonly=True, height=170); timeline.addWidget(self.detail)
        self.filter.textChanged.connect(self.apply_filter); self.direction.currentTextChanged.connect(self.apply_filter)
        self.grid.itemSelectionChanged.connect(self.show_detail); self.display_format.currentTextChanged.connect(self.show_detail)
        self.timer = QTimer(self); self.timer.setInterval(100); self.timer.timeout.connect(self._drain); self.timer.start()
        self._controls()

    def settings(self) -> MqttSettings:
        settings = MqttSettings(host=self.host.text().strip(), port=self.port.value(), client_id=self.client_id.text().strip(),
            transport=self.transport.currentText(), protocol=self.protocol.currentText(), username=self.username.text(), password=self.password.text(),
            keepalive=self.keepalive.value(), clean_session=self.clean_session.isChecked(), session_expiry=self.session_expiry.value(),
            timeout=self.timeout.value() / 1000, auto_reconnect=self.reconnect.isChecked(), ca_file=self.ca_file.text().strip(),
            cert_file=self.cert_file.text().strip(), key_file=self.key_file.text().strip(), key_password=self.key_password.text(),
            will_topic=self.will_topic.text(), will_payload=self.will_payload.text(), will_qos=int(self.will_qos.currentText()), will_retain=self.will_retain.isChecked())
        settings.validate(); return settings

    def _safe(self, action):
        try:
            return action()
        except Exception as exc:
            self._set_status(str(exc), "error"); self.hub.set_notice(str(exc)); return None

    def _set_status(self, text, tone="neutral"):
        self.status.setText(text)
        if self.status.property("statusTone") != tone:
            self.status.setProperty("statusTone", tone)
            self.status.style().unpolish(self.status)
            self.status.style().polish(self.status)
            self.status.update()

    @staticmethod
    def _message_tone(status):
        if any(word in status for word in ("失败", "拒绝", "错误")):
            return "error"
        if any(word in status for word in ("已确认", "已接收")):
            return "success"
        if "无应答" in status:
            return "neutral"
        return "info"

    def toggle_connection(self):
        if self.service.active:
            self.disconnect()
        else:
            self._safe(lambda: self.service.connect(self.settings()))
        self._controls()

    def disconnect(self):
        self.service.disconnect(); self._controls()

    def _controls(self):
        active, connected = self.service.active, self.service.connected
        self.configuration.setEnabled(not active and not self.closed)
        self.connect_button.setText("断开连接" if active else "连接 Broker")
        self.disconnect_button.setEnabled(active)
        for widget in (self.publish_button, self.subscribe_button, self.unsubscribe_button):
            widget.setEnabled(connected and not self.closed)

    def _state(self, text: str, connected: bool):
        if self.closed:
            return
        tone = "error" if any(word in text for word in ("失败", "拒绝", "错误")) else "success" if connected else "warning" if self.service.active else "neutral"
        self._set_status(text, tone); self.hub.set_notice(text); self._controls(); self._subscriptions()

    def _subscriptions(self):
        values = self.service.subscriptions
        self.subscriptions.setRowCount(len(values))
        for index, (topic, qos) in enumerate(values.items()):
            set_cell(self.subscriptions, index, 0, topic, tooltip=topic)
            set_cell(self.subscriptions, index, 1, str(qos), align=Qt.AlignmentFlag.AlignCenter)
        self.subscriptions.update_empty_state()

    def subscribe(self):
        self._safe(lambda: self.service.subscribe(self.subscribe_topic.text(), int(self.subscribe_qos.currentText())))

    def unsubscribe(self):
        index = self.subscriptions.currentRow()
        if index >= 0 and self.subscriptions.item(index, 0):
            self._safe(lambda: self.service.unsubscribe(self.subscriptions.item(index, 0).text()))

    def publish(self):
        self._safe(lambda: self.service.publish(self.publish_topic.text(), payload_bytes(self.payload.toPlainText(), self.publish_format.currentText()), int(self.publish_qos.currentText()), self.retain.isChecked()))

    def format_json(self):
        self._safe(lambda: self.payload.setPlainText(json.dumps(json.loads(self.payload.toPlainText()), ensure_ascii=False, indent=2)))

    def _enqueue(self, message: MqttMessage):
        if self.closed:
            return
        try:
            self._pending.put_nowait(message)
        except queue.Full:
            self.dropped += 1
        self.hub.log("MQTT", message.direction, message.payload, f"{message.topic} · QoS {message.qos} · Retain {message.retain} · 原始 {message.original_length} B")

    def _drain(self):
        if self.closed:
            return
        changed = False; self.grid.setUpdatesEnabled(False)
        try:
            for _ in range(100):
                try:
                    message = self._pending.get_nowait()
                except queue.Empty:
                    break
                changed = True; self.messages.append(message)
                if len(self.messages) > 500:
                    self.messages.pop(0); self.grid.removeRow(0)
                index = self.grid.rowCount(); self.grid.insertRow(index)
                values = (time.strftime("%H:%M:%S", time.localtime(message.timestamp)), message.direction, message.topic,
                    str(message.qos), "保留" if message.retain else "—", str(message.original_length), message.status)
                for column, value in enumerate(values):
                    alignment = Qt.AlignmentFlag.AlignCenter if column in (0, 1, 3, 4) else Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter if column == 5 else Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter
                    tone = ("info" if message.direction == "TX" else "success") if column == 1 else self._message_tone(message.status) if column == 6 else None
                    set_cell(self.grid, index, column, value, align=alignment, tone=tone, tooltip=value)
            if changed:
                self.apply_filter()
                if self.grid.verticalScrollBar().value() >= self.grid.verticalScrollBar().maximum() - 30:
                    self.grid.scrollToBottom()
        finally:
            self.grid.setUpdatesEnabled(True)
        self.count.setText(f"{len(self.messages)} 条消息 · 最近 500 条 · 显示队列丢弃 {self.dropped} 条")
        self._controls()

    def _ack(self, mid: int, status: str):
        if self.closed:
            return
        # Signals may arrive before the bounded display queue is drained.
        for index in range(len(self.messages) - 1, -1, -1):
            message = self.messages[index]
            if message.direction == "TX" and message.mid == mid:
                self.messages[index] = replace(message, status=status)
                set_cell(self.grid, index, 6, status, tone=self._message_tone(status), tooltip=status)
                return
        pending = []
        while True:
            try:
                message = self._pending.get_nowait()
            except queue.Empty:
                break
            pending.append(replace(message, status=status) if message.direction == "TX" and message.mid == mid else message)
        for message in pending:
            try:
                self._pending.put_nowait(message)
            except queue.Full:
                self.dropped += 1

    def apply_filter(self, *_):
        needle, direction = self.filter.text().casefold(), self.direction.currentText()
        for index, message in enumerate(self.messages):
            hidden = (direction != "全部" and message.direction != direction) or (needle and needle not in message.topic.casefold() and needle not in message.payload.decode("utf-8", "replace").casefold())
            self.grid.setRowHidden(index, bool(hidden))
        no_matches = bool(self.messages) and all(self.grid.isRowHidden(index) for index in range(self.grid.rowCount()))
        self.grid.set_empty_text("没有匹配的消息" if no_matches else "等待第一条消息",
            "调整主题、负载或方向筛选后重试。" if no_matches else "连接 Broker 后订阅或发布消息，收发记录将显示在这里。")
        self.grid.update_empty_state()

    def show_detail(self, *_):
        index = self.grid.currentRow()
        if not 0 <= index < len(self.messages):
            return
        message = self.messages[index]
        kind = self.display_format.currentText()
        if kind == "HEX":
            text = message.payload.hex(" ").upper()
        else:
            text = message.payload.decode("utf-8", "replace")
            if kind == "JSON":
                try:
                    text = json.dumps(json.loads(text), ensure_ascii=False, indent=2)
                except ValueError:
                    text = "不是完整的 JSON：\n" + text
        if message.original_length > len(message.payload):
            text += f"\n\n[展示已截断：原始 {message.original_length} B，展示 {len(message.payload)} B]"
        self.detail.setPlainText(text)

    def clear_messages(self):
        self.messages.clear(); self.grid.setRowCount(0); self.detail.clear()
        while not self._pending.empty():
            try:
                self._pending.get_nowait()
            except queue.Empty:
                break
        self.apply_filter()

    def _certificate(self, field: QLineEdit):
        path, _ = QFileDialog.getOpenFileName(self, "选择 PEM 证书或私钥", "", "PEM files (*.pem *.crt *.cer *.key);;All files (*)")
        if path:
            field.setText(path)

    def profile_dict(self) -> dict:
        return {"format_version": 1, "connection": self.settings().safe_dict(), "subscriptions": self.service.subscriptions,
            "publish": {"topic": self.publish_topic.text(), "format": self.publish_format.currentText(), "payload": self.payload.toPlainText(), "qos": int(self.publish_qos.currentText()), "retain": self.retain.isChecked()}}

    def save_profile(self):
        def save():
            value = self.profile_dict()
            path, _ = QFileDialog.getSaveFileName(self, "保存 MQTT 模板（不含密码）", "mqtt-profile.json", "JSON (*.json)")
            if path:
                Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        self._safe(save)

    def load_profile(self):
        def load():
            if self.service.active:
                raise ValueError("请先断开连接后载入模板。")
            path, _ = QFileDialog.getOpenFileName(self, "载入 MQTT 模板", "", "JSON (*.json)")
            if not path:
                return
            if Path(path).stat().st_size > 2 * 1024 * 1024:
                raise ValueError("MQTT 模板文件过大。")
            self.apply_profile(json.loads(Path(path).read_text(encoding="utf-8")))
        self._safe(load)

    def apply_profile(self, value: dict):
        if self.service.active:
            raise ValueError("请先断开后载入模板。")
        if value.get("format_version") != 1:
            raise ValueError("MQTT 模板版本不受支持。")
        settings = MqttSettings.from_dict(value.get("connection", {}))
        subscriptions = value.get("subscriptions", {})
        if not isinstance(subscriptions, dict) or len(subscriptions) > 200:
            raise ValueError("订阅模板格式错误或数量过多。")
        self.service.set_subscriptions(subscriptions)
        for widget, setting in ((self.host, settings.host), (self.client_id, settings.client_id), (self.username, settings.username),
            (self.ca_file, settings.ca_file), (self.cert_file, settings.cert_file), (self.key_file, settings.key_file),
            (self.will_topic, settings.will_topic), (self.will_payload, settings.will_payload)):
            widget.setText(setting)
        self.password.clear(); self.key_password.clear()
        for widget, setting in ((self.port, settings.port), (self.keepalive, settings.keepalive), (self.timeout, int(settings.timeout * 1000)), (self.session_expiry, settings.session_expiry)):
            widget.setValue(setting)
        self.transport.setCurrentText(settings.transport); self.protocol.setCurrentText(settings.protocol); self.will_qos.setCurrentText(str(settings.will_qos))
        self.clean_session.setChecked(settings.clean_session); self.reconnect.setChecked(settings.auto_reconnect); self.will_retain.setChecked(settings.will_retain)
        publish = value.get("publish", {})
        if publish:
            if publish.get("format", "Text") not in ("Text", "JSON", "HEX") or publish.get("qos", 0) not in (0, 1, 2):
                raise ValueError("发布模板格式或 QoS 错误。")
            self.publish_topic.setText(str(publish.get("topic", ""))); self.payload.setPlainText(str(publish.get("payload", "")))
            self.publish_format.setCurrentText(publish.get("format", "Text")); self.publish_qos.setCurrentText(str(publish.get("qos", 0))); self.retain.setChecked(bool(publish.get("retain", False)))
        self._subscriptions(); self._set_status("模板已载入，尚未连接 Broker。", "info")

    def export_messages(self):
        def export():
            path, selected = QFileDialog.getSaveFileName(self, "导出筛选消息", "mqtt-messages.json", "JSON (*.json);;CSV (*.csv)")
            if not path:
                return
            messages = [message for index, message in enumerate(self.messages) if not self.grid.isRowHidden(index)]
            values = [dict(time=message.timestamp, direction=message.direction, topic=message.topic, qos=message.qos,
                retain=message.retain, length=message.original_length, payload_hex=message.payload.hex(), status=message.status,
                truncated=message.original_length > len(message.payload)) for message in messages]
            if selected.startswith("CSV"):
                with open(path, "w", encoding="utf-8-sig", newline="") as stream:
                    writer = csv.DictWriter(stream, fieldnames=("time", "direction", "topic", "qos", "retain", "length", "payload_hex", "status", "truncated"))
                    writer.writeheader(); writer.writerows(values)
            else:
                Path(path).write_text(json.dumps(values, ensure_ascii=False, indent=2), encoding="utf-8")
        self._safe(export)

    def close_resources(self):
        if self.closed:
            return
        super().close_resources(); self.timer.stop(); self.service.close()
