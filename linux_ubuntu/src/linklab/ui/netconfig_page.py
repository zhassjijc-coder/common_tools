from __future__ import annotations

from dataclasses import asdict
from pathlib import Path
import threading
import time

from PySide6.QtCore import QSignalBlocker, Qt
from PySide6.QtNetwork import QAbstractSocket, QNetworkInterface
from PySide6.QtWidgets import (QCheckBox, QFormLayout, QGridLayout, QHBoxLayout,
    QLabel, QLineEdit, QMessageBox, QFileDialog, QSizePolicy, QVBoxLayout, QWidget)

from .common import (Page, button, combo, configure_columns, line, row,
    set_cell, spin, table, text_area)
from ..protocols.zlan import (APP_PROTOCOLS, BAUD_RATES, FLOW_CONTROLS, MODES, PARITIES,
    DeviceSettings, DeviceSnapshot, ZlanClient, export_profile, import_profile, write_packet)


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


def _group_title(text):
    label = QLabel(text)
    font = label.font(); font.setBold(True); label.setFont(font)
    label.setProperty("fieldGroup", True)
    return label


class NetConfigPage(Page):
    """ZLAN device management; no automatic scan and no OS adapter changes."""
    def __init__(self, hub):
        super().__init__(hub)
        self.setObjectName("netconfigPage")
        self.client = ZlanClient(on_traffic=lambda direction, data, note: hub.log("网口配置", direction, data, note))
        self.devices: list[DeviceSnapshot] = []
        self.selected: DeviceSnapshot | None = None
        self._reviewed: tuple[str, DeviceSettings] | None = None
        self._cancel = threading.Event()
        self._working = False
        self.card("设备发现与参数管理", "发现设备、读取参数，集中配置网络连接、串口与转化协议。")
        discovery = self.card("01  连接范围与设备发现", "选择连接设备的网卡，再搜索局域网或按 IP 定向查询。")
        self.interface = combo(()); self.interface.setMinimumWidth(280); self.target = line("192.168.1.200"); self.timeout = spin(2000, 200, 15000)
        controls = QGridLayout()
        controls.setContentsMargins(0, 0, 0, 0)
        controls.setHorizontalSpacing(14); controls.setVerticalSpacing(7)
        controls.setColumnStretch(0, 3); controls.setColumnStretch(1, 2)
        self.timeout.setMaximumWidth(144)
        for column, (label, widget) in enumerate((("本机 IPv4 网卡", self.interface), ("定向查询 IP", self.target), ("超时 / ms", self.timeout))):
            controls.addWidget(_field(label, widget), 0, column)
        discovery.addLayout(controls)
        self.refresh_button = button("刷新网卡", self.refresh_interfaces)
        self.search_button = button("搜索局域网设备", lambda: self.search(True), True)
        self.query_button = button("按 IP 查询", lambda: self.search(False))
        self.read_button = button("重读所选设备", self.read_selected)
        discovery_actions = QHBoxLayout(); discovery_actions.setSpacing(10)
        for widget in (self.search_button, self.query_button, self.read_button):
            discovery_actions.addWidget(widget)
        discovery_actions.addStretch(); discovery_actions.addWidget(self.refresh_button)
        discovery.addLayout(discovery_actions)
        self.status = QLabel("选择网卡并搜索设备，或输入 IP 定向查询。"); self.status.setWordWrap(True); self.status.setTextFormat(Qt.TextFormat.PlainText)
        self.status.setObjectName("statusBanner"); self.status.setProperty("statusTone", "neutral")
        self.status.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Minimum)
        discovery.addWidget(self.status)
        self.grid = table(("设备名", "DevID / MAC / 通道", "设备 IP", "端口", "模式", "支持状态", "最后应答"), min_height=250,
            empty_text="没有已发现的设备", empty_hint="点击搜索局域网设备，或输入设备 IP 进行查询。")
        configure_columns(self.grid, [None, 214, 150, 76, 126, 154, 106], stretch_last=False, minimum_stretch=160)
        self.grid.setSortingEnabled(False)
        self.grid.setObjectName("networkConfigDevices"); discovery.addWidget(self.grid)
        self.count = QLabel("0 台设备"); self.count.setObjectName("muted"); discovery.addWidget(self.count)
        self.grid.itemSelectionChanged.connect(self._select)
        edit = self.card("02  设备网络与串口参数", "先选择设备，再编辑并检查修改。未支持的参数格式仅供读取。")
        self.identity = QLabel("请先选择设备"); self.identity.setTextFormat(Qt.TextFormat.PlainText); self.identity.setWordWrap(True); edit.addWidget(self.identity)
        self.identity.setObjectName("selectionSummary")
        self.editor = QWidget(); fields = QGridLayout(self.editor)
        fields.setContentsMargins(0, 0, 0, 0)
        fields.setHorizontalSpacing(26); fields.setVerticalSpacing(12)
        fields.setColumnStretch(0, 1); fields.setColumnStretch(1, 1)
        self.name = line(); self.name.setMaxLength(9)
        self.address = line(); self.mask = line(); self.gateway = line()
        # Keep unsupported zero values visible when reading an existing device;
        # validation requires the user to correct them before any write.
        self.local_port = spin(4196, 0, 65535); self.destination = line(); self.destination_port = spin(4196, 0, 65535)
        self.mode = combo(MODES); self.baud = combo(BAUD_RATES, 9600); self.parity = combo(PARITIES)
        self.data_bits = combo((5, 6, 7, 8), 8); self.stop_bits = combo((1, 2), 1)
        self.flow_control = combo(FLOW_CONTROLS); self.app_protocol = combo(APP_PROTOCOLS)
        self.dhcp = QCheckBox("DHCP 自动获取 IP")
        self.password = line(); self.password.setEchoMode(QLineEdit.EchoMode.Password)
        self.password.setPlaceholderText("仅本次使用，不保存")
        fields.addWidget(_group_title("网络连接"), 0, 0)
        fields.addWidget(_group_title("串口与转化"), 0, 1)
        left = QGridLayout(); right = QGridLayout()
        for layout in (left, right):
            layout.setContentsMargins(0, 0, 0, 0); layout.setHorizontalSpacing(14); layout.setVerticalSpacing(14)
            layout.setColumnStretch(0, 1); layout.setColumnStretch(1, 1)
        network_fields = (("设备名 / 最多 9 位 ASCII", self.name), ("工作模式", self.mode), ("设备 IP", self.address), ("子网掩码", self.mask),
            ("网关 / 0.0.0.0 为无", self.gateway), ("本地端口", self.local_port), ("目标 IPv4 / 域名", self.destination), ("目标端口", self.destination_port))
        for index, (label, widget) in enumerate(network_fields):
            left.addWidget(_field(label, widget), index // 2, index % 2)
        serial_fields = (("波特率", self.baud), ("数据位", self.data_bits), ("校验", self.parity), ("停止位", self.stop_bits),
            ("流控", self.flow_control), ("转化协议", self.app_protocol))
        for index, (label, widget) in enumerate(serial_fields):
            right.addWidget(_field(label, widget), index // 2, index % 2)
        self.password_field = _field("设备配置密码（不保存）", self.password)
        right.addWidget(self.password_field, 3, 0, 1, 2)
        left.addWidget(self.dhcp, 4, 0, 1, 2)
        fields.addLayout(left, 1, 0, alignment=Qt.AlignmentFlag.AlignTop)
        fields.addLayout(right, 1, 1, alignment=Qt.AlignmentFlag.AlignTop)
        edit.addWidget(self.editor)
        self.review_button = button("检查修改", self.review, True); self.restore_button = button("恢复读取值", self.restore)
        self.save_button = button("导出参数模板", self.save_profile); self.load_button = button("载入参数模板", self.load_profile)
        edit_actions = QHBoxLayout(); edit_actions.setSpacing(10)
        edit_actions.addWidget(self.review_button); edit_actions.addWidget(self.restore_button)
        edit_actions.addStretch(); edit_actions.addWidget(self.save_button); edit_actions.addWidget(self.load_button)
        edit.addLayout(edit_actions)
        self.preview = text_area("", readonly=True, height=160); self.preview.setObjectName("networkConfigPreview"); self.preview.hide(); edit.addWidget(self.preview)
        self.apply_button = button("确认并写入设备", self.apply, True); self.apply_button.hide(); edit.addWidget(self.apply_button)
        note = QLabel("写入会重启设备。仅在回读到同一 DevID / 通道且参数匹配后显示成功；超时不自动重复写入。")
        note.setWordWrap(True); note.setObjectName("muted"); edit.addWidget(note)
        logs = self.card("03  操作记录", "认证区域和扩展区域在全局报文记录中隐藏；模板不包含密码、DevID 或原始设备报文。")
        self.log = text_area("尚未发送网络报文。", readonly=True, height=150); logs.addWidget(self.log)
        logs.addLayout(row(button("清空日志", self.log.clear)))
        for widget in (self.name, self.address, self.mask, self.gateway, self.destination):
            widget.textChanged.connect(self.invalidate_review)
        for widget in (self.local_port, self.destination_port):
            widget.valueChanged.connect(self.invalidate_review)
        for widget in (self.mode, self.baud, self.parity, self.data_bits, self.stop_bits, self.flow_control, self.app_protocol):
            widget.currentIndexChanged.connect(self.invalidate_review)
        self.dhcp.toggled.connect(self.invalidate_review)
        self.refresh_interfaces(); self._controls()

    def refresh_interfaces(self):
        previous = self.interface.currentData() or "0.0.0.0"
        self.interface.clear(); self.interface.addItem("自动选择路由网卡 · 0.0.0.0", "0.0.0.0")
        seen = {"0.0.0.0"}
        for interface in QNetworkInterface.allInterfaces():
            if not interface.flags() & QNetworkInterface.InterfaceFlag.IsUp:
                continue
            for entry in interface.addressEntries():
                address = entry.ip()
                if address.protocol() == QAbstractSocket.NetworkLayerProtocol.IPv4Protocol and address.toString() not in seen:
                    seen.add(address.toString()); self.interface.addItem(f"{interface.humanReadableName()} · {address.toString()}", address.toString())
        if "127.0.0.1" not in seen:
            self.interface.addItem("本机回环 · 127.0.0.1", "127.0.0.1")
        self.interface.setCurrentIndex(max(0, self.interface.findData(previous)))

    def _controls(self):
        idle = not self._working and not self.closed
        for widget in (self.search_button, self.query_button, self.refresh_button, self.interface, self.target, self.timeout, self.grid):
            widget.setEnabled(idle)
        can_edit = idle and self.selected is not None and self.selected.writable
        self.editor.setEnabled(can_edit)
        for widget in (self.review_button, self.restore_button, self.save_button, self.load_button):
            widget.setEnabled(can_edit)
        self.read_button.setEnabled(idle and self.selected is not None)
        self.apply_button.setEnabled(can_edit and self._reviewed is not None)
        self.dhcp.setEnabled(can_edit and self.selected is not None and self.selected.supports_dhcp)
        needs_password = self.selected is not None and self.selected.password_required
        self.password.setEnabled(can_edit and needs_password)
        self.password_field.setVisible(needs_password)

    def _status(self, text: str, *, error: bool = False):
        self.status.setText(text)
        tone = "error" if error else "success" if text.startswith(("写入成功", "设备参数已重读", "收到")) else "warning" if "未收到" in text or "未确认" in text else "info"
        if self.status.property("statusTone") != tone:
            self.status.setProperty("statusTone", tone)
            self.status.style().unpolish(self.status); self.status.style().polish(self.status); self.status.update()
        self.hub.set_notice(text)
        self.log.appendPlainText(f"[{time.strftime('%H:%M:%S')}] {text}")
        if self.log.document().blockCount() > 300:
            self.log.setPlainText("\n".join(self.log.toPlainText().splitlines()[-220:]))

    def _run(self, work, done):
        if self._working or self.closed:
            return False
        self._working = True; self._controls()
        def wrapped():
            try:
                return work(), None
            except Exception as exc:
                return None, str(exc) or type(exc).__name__
        def complete(result):
            self._working = False
            value, error = result
            if error:
                self._status(error, error=True)
            else:
                done(value)
            self._controls()
        started = self.submit(wrapped, complete)
        if not started:
            self._working = False; self._controls()
        return started

    def search(self, broadcast: bool = True):
        bind, target, timeout = self.interface.currentData() or "0.0.0.0", "255.255.255.255" if broadcast else self.target.text().strip(), self.timeout.value() / 1000
        self._status(f"正在{'搜索局域网' if broadcast else '按 IP 查询'}：{target}:1092…")
        def complete(devices):
            if broadcast:
                self.devices.clear(); self.selected = None
            for device in devices:
                self._upsert(device)
            self._render(devices[0].identity if devices else None)
            self._status(f"收到 {len(devices)} 台兼容设备应答。" if devices else "未收到兼容设备应答，请检查网卡、供电、网段与 UDP 1092 通信。")
        self._run(lambda: self.client.discover(bind, target, broadcast=broadcast, timeout=timeout, cancel=self._cancel), complete)

    def _upsert(self, device: DeviceSnapshot):
        for index, old in enumerate(self.devices):
            if old.identity == device.identity:
                self.devices[index] = device; return
        self.devices.append(device)

    def _render(self, identity: str | None = None):
        blocker = QSignalBlocker(self.grid)
        self.grid.setRowCount(len(self.devices))
        for index, device in enumerate(self.devices):
            values = (device.settings.name, device.identity, device.settings.address, str(device.settings.local_port),
                MODES[device.settings.mode] if device.settings.mode in range(4) else str(device.settings.mode),
                "可编辑" if device.writable else f"只读 · {len(device.parameters)} 字节", time.strftime("%H:%M:%S", time.localtime(device.seen)))
            for column, value in enumerate(values):
                alignment = Qt.AlignmentFlag.AlignCenter if column in (3, 4, 5, 6) else Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter
                tone = ("success" if device.writable else "warning") if column == 5 else None
                set_cell(self.grid, index, column, value, align=alignment, tone=tone, tooltip=value)
        self.grid.clearSelection(); del blocker
        if identity:
            index = next((index for index, device in enumerate(self.devices) if device.identity == identity), -1)
            if index >= 0:
                self.grid.selectRow(index)
        else:
            self.selected = None; self.identity.setText("请先选择设备"); self.invalidate_review()
        self.count.setText(f"{len(self.devices)} 台设备 · 按 DevID / 通道区分")
        self.grid.update_empty_state(); self._controls()

    def _select(self):
        index = self.grid.currentRow()
        if not 0 <= index < len(self.devices):
            return
        self.selected = self.devices[index]; self.password.clear()
        self.identity.setText(f"{self.selected.identity}  ·  原 IP {self.selected.settings.address}")
        self.fill(self.selected.settings); self._controls()

    def fill(self, settings: DeviceSettings):
        for widget, value in ((self.name, settings.name), (self.address, settings.address), (self.mask, settings.mask), (self.gateway, settings.gateway), (self.destination, settings.destination)):
            widget.setText(value)
        self.local_port.setValue(settings.local_port); self.destination_port.setValue(settings.destination_port)
        self.mode.setCurrentIndex(settings.mode); self.baud.setCurrentIndex(self.baud.findText(str(settings.baud))); self.parity.setCurrentIndex(settings.parity)
        self.data_bits.setCurrentIndex(self.data_bits.findText(str(settings.data_bits))); self.stop_bits.setCurrentIndex(self.stop_bits.findText(str(settings.stop_bits))); self.flow_control.setCurrentIndex(settings.flow_control)
        self.app_protocol.setCurrentIndex(settings.app_protocol); self.dhcp.setChecked(settings.dhcp); self.invalidate_review()

    def settings(self) -> DeviceSettings:
        value = DeviceSettings(self.name.text().strip(), self.address.text().strip(), self.mask.text().strip(), self.gateway.text().strip(), self.local_port.value(),
            self.destination.text().strip(), self.destination_port.value(), self.mode.currentIndex(), int(self.baud.currentText()), self.parity.currentIndex(), self.dhcp.isChecked(),
            int(self.data_bits.currentText()), int(self.stop_bits.currentText()), self.flow_control.currentIndex(), self.app_protocol.currentIndex())
        value.validate(); return value

    def invalidate_review(self, *_):
        self._reviewed = None; self.preview.clear(); self.preview.hide(); self.apply_button.hide(); self._controls()

    def restore(self):
        if self.selected:
            self.fill(self.selected.settings)

    def read_selected(self):
        selected = self.selected
        if selected is None:
            return
        bind, timeout = self.interface.currentData() or "0.0.0.0", self.timeout.value() / 1000
        self._status(f"正在重读 {selected.identity}…")
        def done(device):
            self._upsert(device); self._render(device.identity); self._status("设备参数已重读。")
        self._run(lambda: self.client.read(selected, bind=bind, timeout=timeout, cancel=self._cancel), done)

    def review(self):
        try:
            if not self.selected or not self.selected.writable:
                raise ValueError("请先选择可编辑的 167 字节参数设备。")
            value = self.settings(); write_packet(self.selected, value, self.password.text())
            self._reviewed = (self.selected.identity, value)
            self.preview.setPlainText(f"设备 {self.selected.identity} · 当前 IP {self.selected.settings.address}\n"
                f"设备名 {value.name} · 新 IP {value.address} / {value.mask}\n网关 {value.gateway} · 本地端口 {value.local_port} · {'DHCP' if value.dhcp else '静态 IP'}\n"
                f"{MODES[value.mode]} → {value.destination}:{value.destination_port}\n串口 {value.baud} / {value.data_bits}{PARITIES[value.parity]}{value.stop_bits} · {FLOW_CONTROLS[value.flow_control]}\n"
                f"转化协议 {APP_PROTOCOLS[value.app_protocol]}\n写入后将重启设备；只在回读确认后显示成功。")
            self.preview.show(); self.apply_button.show(); self._controls(); self._status("请核对设备身份、原 IP 和拟改参数，再确认写入。")
        except Exception as exc:
            self._status(str(exc), error=True)

    def apply(self):
        if self._working or self.selected is None or self._reviewed is None:
            return
        identity, settings = self._reviewed
        if identity != self.selected.identity or settings != self.settings():
            self.invalidate_review(); return
        answer = QMessageBox.question(self, "确认设备配置写入", self.preview.toPlainText() + "\n\n确认将这些参数写入此设备？", QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No, QMessageBox.StandardButton.No)
        if answer != QMessageBox.StandardButton.Yes:
            return
        selected, bind, timeout, password = self.selected, self.interface.currentData() or "0.0.0.0", self.timeout.value() / 1000, self.password.text()
        self._status("正在确认设备身份、写入并等待重启后的回读…")
        def complete(device):
            self._upsert(device); self._render(device.identity); self._status("写入成功：已回读相同 DevID / 通道，参数一致。")
        self._run(lambda: self.client.apply(selected, settings, password=password, bind=bind, timeout=timeout, cancel=self._cancel), complete)

    def save_profile(self):
        try:
            text = export_profile(self.settings())
            path, _ = QFileDialog.getSaveFileName(self, "导出设备参数模板（不含密码和设备身份）", "network-device.json", "JSON (*.json)")
            if path:
                Path(path).write_text(text, encoding="utf-8")
        except Exception as exc:
            self._status(str(exc), error=True)

    def load_profile(self):
        try:
            if self.selected is None:
                raise ValueError("请先选择目标设备，再载入参数模板。")
            path, _ = QFileDialog.getOpenFileName(self, "载入设备参数模板", "", "JSON (*.json)")
            if path:
                if Path(path).stat().st_size > 65536:
                    raise ValueError("参数模板文件过大。")
                self.fill(import_profile(Path(path).read_text(encoding="utf-8"))); self._status("模板已载入，尚未写入设备。")
        except Exception as exc:
            self._status(str(exc), error=True)

    def close_resources(self):
        if self.closed:
            return
        self._cancel.set(); super().close_resources()
