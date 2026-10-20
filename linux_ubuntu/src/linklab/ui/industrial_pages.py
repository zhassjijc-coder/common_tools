from __future__ import annotations

import json
import socket
from PySide6.QtCore import QTimer, Qt
from PySide6.QtWidgets import QCheckBox, QFileDialog, QGridLayout, QLabel, QVBoxLayout, QWidget
from .common import Page, combo, line, spin, button, row, form, text_area, table, configure_columns, set_cell
from ..protocols.modbus import ModbusClient, ModbusServer, Memory
from ..protocols.plc import PlcClient, PlcServer
from ..protocols import codec


FUNCTIONS = ["01 读取线圈", "02 读取离散输入", "03 读取保持寄存器", "04 读取输入寄存器", "05 写单线圈", "06 写单寄存器", "15 写多线圈", "16 写多寄存器"]


def parse_values(text):
    result = [int(part, 16) if part.lower().startswith("0x") else int(part) for part in text.replace(",", " ").split()]
    if not result:
        raise ValueError("请输入写入值，以空格或逗号分隔")
    return result


class MasterPage(Page):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.client = None
        self.polling = QTimer(self)
        self.polling.timeout.connect(self.execute)
        card = self.card("01  Modbus 主站", "RTU 使用已有串口；TCP 地址按协议从 0 起算，寄存器显示支持字节序转换。")
        self.mode = combo(["TCP", "RTU"])
        self.host = line("127.0.0.1")
        self.port = spin(1502, 1)
        self.serial = line("", "/dev/ttyUSB0 或 COM6")
        self.baud = spin(9600, 1200, 4000000)
        self.parity = combo(["N", "E", "O", "M", "S"])
        self.stopbits = combo(["1", "2"])
        self.unit = spin(1, 1, 247)
        self.open_button = button("准备连接", self.connect_client, True)
        card.addLayout(form(模式=self.mode, TCP地址=self.host, TCP端口=self.port, RTU串口=self.serial, 波特率=self.baud, 站号=self.unit, RTU校验=self.parity, RTU停止位=self.stopbits))
        card.addLayout(row(self.open_button, button("断开", self.disconnect_session)))
        card = self.card("02  读写与轮询")
        self.function = combo(FUNCTIONS, FUNCTIONS[2])
        self.address = spin(0)
        self.count = spin(3, 1, 2000)
        self.values = line("100 200 300")
        self.interval = spin(1000, 100, 3600000)
        self.type = combo(["UInt16", "Int16", "HEX", "UInt32", "Int32", "Float32"])
        self.order = combo(["ABCD", "BADC", "CDAB", "DCBA"])
        self.poll = QCheckBox("持续读取")
        self.poll.toggled.connect(self.toggle_poll)
        card.addLayout(form(功能码=self.function, 起始地址=self.address, 数量=self.count, 写入值=self.values))
        card.addLayout(row(button("执行一次", self.execute, True), self.poll, self.interval, "毫秒", self.type, self.order))
        self.result = table(["地址", "原始值", "转换值"], empty_text="尚未读取寄存器", empty_hint="设置功能码和地址，点击执行一次查看结果。")
        configure_columns(self.result, [130, None, None])
        card.addWidget(self.result)
        self.layout.addStretch()

    def connect_client(self):
        if self.busy:
            return
        if self.client is not None:
            self.disconnect_session()
        try:
            self.client = ModbusClient(host=self.host.text().strip(), port=self.port.value(), unit=self.unit.value(),
                serial_port=self.serial.text().strip() if self.mode.currentText() == "RTU" else None,
                baudrate=self.baud.value(), parity=self.parity.currentText(), stopbits=int(self.stopbits.currentText()), on_traffic=lambda direction, data, peer="": self.hub.log("Modbus 主站", direction, data, peer))
            self.hub.set_notice("Modbus 参数已就绪，执行读写时建立连接")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def toggle_poll(self, enabled):
        if enabled:
            if int(self.function.currentText().split()[0]) > 4:
                self.hub.set_notice("轮询只允许读取，请选择功能码 01～04")
                self.poll.setChecked(False)
                return
            self.polling.start(self.interval.value())
            self.execute()
        else:
            self.polling.stop()

    def execute(self):
        if self.busy or self.closed:
            return
        if self.client is None:
            self.connect_client()
        if self.client is None:
            return
        function = int(self.function.currentText().split()[0])
        if self.poll.isChecked() and function > 4:
            self.poll.setChecked(False)
            self.hub.set_notice("已停止轮询；写入请点击执行一次")
            return
        client, address, count = self.client, self.address.value(), self.count.value()
        try:
            values = parse_values(self.values.text()) if function > 4 else []
            def operation():
                if function <= 4:
                    return client.read(function, address, count)
                client.write(function, address, values)
                return values
            self.submit(operation, lambda values: self.show_values(address, values))
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def show_values(self, address, values):
        self.result.setRowCount(len(values))
        for index, value in enumerate(values):
            converted = str(value)
            try:
                if self.type.currentText() == "HEX":
                    converted = f"0x{int(value):04X}"
                elif self.type.currentText() not in ("UInt16", "HEX"):
                    raw = b"".join(int(v).to_bytes(2, "big") for v in values[index:])
                    converted = str(codec.unpack_values(raw[:4] if "32" in self.type.currentText() else raw[:2], self.type.currentText(), self.order.currentText())[0])
            except (ValueError, IndexError):
                converted = "—"
            for column, item in enumerate((address + index, value, converted)):
                set_cell(self.result, index, column, item, align=Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)
        self.hub.set_notice(f"Modbus 操作完成，{len(values)} 个值")

    def disconnect_session(self):
        self.poll.setChecked(False) if hasattr(self, "poll") else None
        self.polling.stop()
        client, self.client = self.client, None
        if client:
            client.close()

    def close_resources(self):
        super().close_resources()
        self.disconnect_session()


class SimulatorPage(Page):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.server = None
        self.memory = Memory()
        self.memory.write("holding", 0, [2300, 1234, 5678])
        card = self.card("01  Modbus 设备模拟", "TCP 服务端 / RTU 从站；内存独立于主站，不执行任何真实设备动作。")
        self.mode = combo(["TCP", "RTU"])
        self.host = line("127.0.0.1")
        self.port = spin(1502, 0)
        self.serial = line("", "/dev/ttyUSB1 或 COM7")
        self.baud = spin(9600, 1200, 4000000)
        self.parity = combo(["N", "E", "O", "M", "S"])
        self.stopbits = combo(["1", "2"])
        self.unit = spin(1, 1, 247)
        card.addLayout(form(模式=self.mode, 监听地址=self.host, 监听端口=self.port, RTU串口=self.serial, 波特率=self.baud, 站号=self.unit, RTU校验=self.parity, RTU停止位=self.stopbits))
        self.start_button = button("启动模拟", self.start, True)
        card.addLayout(row(self.start_button, button("停止", self.stop)))
        card = self.card("02  设备内存")
        self.area = combo(["holding", "input", "coils", "discrete"])
        self.address = spin(0)
        self.count = spin(16, 1, 200)
        self.values = line("2300 1234 5678")
        card.addLayout(row(self.area, "地址", self.address, "数量", self.count, button("刷新", self.refresh)))
        self.grid = table(["地址", "值"])
        configure_columns(self.grid, [130, None])
        card.addWidget(self.grid)
        card.addLayout(row(self.values, button("写入模拟内存", self.edit)))
        self.layout.addStretch()
        self.refresh()

    def start(self):
        if self.server or self.busy:
            return
        config = dict(memory=self.memory, host=self.host.text().strip(), port=self.port.value(), unit=self.unit.value(),
            serial_port=self.serial.text().strip() if self.mode.currentText() == "RTU" else None,
            baudrate=self.baud.value(), parity=self.parity.currentText(), stopbits=int(self.stopbits.currentText()), on_traffic=lambda direction, data, peer="": self.hub.log("Modbus 模拟", direction, data, peer))
        def started(server):
            self.server = server
            self.start_button.setEnabled(False)
            self.hub.set_notice(f"Modbus 模拟已启动，端口 {getattr(server, 'bound_port', self.serial.text())}")
        self.submit(lambda: ModbusServer(**config).start(), started)

    def stop(self):
        server, self.server = self.server, None
        if server:
            server.stop()
        self.start_button.setEnabled(True)

    def refresh(self):
        try:
            values = self.memory.read(self.area.currentText(), self.address.value(), self.count.value())
            self.grid.setRowCount(len(values))
            for index, value in enumerate(values):
                set_cell(self.grid, index, 0, self.address.value()+index)
                set_cell(self.grid, index, 1, value)
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def edit(self):
        try:
            self.memory.write(self.area.currentText(), self.address.value(), parse_values(self.values.text()))
            self.refresh()
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def close_resources(self):
        super().close_resources()
        self.stop()


class PlcPage(Page):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.client = self.server = None
        self._starting_server = False
        self._client_operation = None
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.read)
        card = self.card("PLC LAB / 读写、监视、模拟", "西门子 S7 TCP、三菱 MC 3E 二进制、欧姆龙 FINS/TCP。选择工作角色，连接设备或模拟 PLC。")
        self.protocol = combo(["S7", "MC", "FINS"])
        self.role = combo(["客户端 / 监视", "服务器 / 模拟内存"])
        self.role.setMinimumWidth(220)
        self.protocol.setObjectName("plcProtocol")
        self.role.setObjectName("plcRole")
        self.loopback_button = button("本机联调", self.loopback)
        choices = QGridLayout(); choices.setContentsMargins(0, 0, 0, 0)
        choices.setHorizontalSpacing(14)
        choices.setColumnStretch(0, 1); choices.setColumnStretch(1, 1)
        choices.addWidget(self._field("PLC 协议", self.protocol), 0, 0)
        choices.addWidget(self._field("工作角色", self.role), 0, 1)
        choices.addWidget(self.loopback_button, 0, 2, alignment=Qt.AlignmentFlag.AlignBottom)
        card.addLayout(choices)

        card = self.card("01  客户端连接", "设置目标 PLC 地址，点击连接 PLC 检查通信；也可直接执行一次读写。")
        self.client_panel = card.parentWidget()
        self.host = line("127.0.0.1")
        self.port = spin(1102, 1)
        self.timeout = spin(2000, 100, 60000)
        self.rack, self.slot = spin(0, 0, 7), spin(1, 0, 31)
        self.client_configuration = QWidget()
        connection = QGridLayout(self.client_configuration)
        connection.setContentsMargins(0, 0, 0, 0); connection.setHorizontalSpacing(14); connection.setVerticalSpacing(12)
        connection.setColumnStretch(0, 2); connection.setColumnStretch(1, 1); connection.setColumnStretch(2, 1)
        connection.addWidget(self._field("IP / 主机名", self.host), 0, 0)
        connection.addWidget(self._field("端口", self.port), 0, 1)
        connection.addWidget(self._field("连接 / 响应超时（ms）", self.timeout), 0, 2)
        self.s7_fields = QWidget()
        self.s7_fields.setLayout(form(机架=self.rack, 插槽=self.slot))
        self.s7_fields.layout().setContentsMargins(0, 0, 0, 0)
        connection.addWidget(self.s7_fields, 1, 0, 1, 3)
        card.addWidget(self.client_configuration)
        self.connect_button = button("连接 PLC", self.connect_client, True)
        self.prepare_button = self.connect_button
        self.disconnect_button = button("断开", self.disconnect_session)
        card.addLayout(row(self.connect_button, self.disconnect_button))
        self.client_status = QLabel("未连接 · 点击连接 PLC 后检查目标地址与协议。")
        self.client_status.setWordWrap(True)
        self.client_status.setTextFormat(Qt.TextFormat.PlainText)
        self.client_status.setObjectName("statusBanner"); self.client_status.setProperty("statusTone", "neutral")
        card.addWidget(self.client_status)

        card = self.card("01  虚拟 PLC 服务器", "启动后，外部上位机可按所选协议连接并读写数据区。模拟通信与内存，不执行 PLC 程序。")
        self.server_panel = card.parentWidget()
        self.listen_host = line("127.0.0.1")
        self.listen_port = spin(1102, 0)
        card.addLayout(form(监听IP=self.listen_host, 监听端口=self.listen_port))
        note = QLabel("127.0.0.1 仅本机；0.0.0.0 接受局域网连接。端口填 0 时自动分配空闲端口。")
        note.setWordWrap(True); note.setObjectName("muted"); card.addWidget(note)
        self.start_button = button("启动服务器", self.start_server, True)
        self.stop_button = button("停止服务器", self.stop_server)
        self.import_button = button("导入内存快照", self.import_memory)
        self.export_button = button("导出内存快照", self.export_memory)
        card.addLayout(row(self.start_button, self.stop_button, self.import_button, self.export_button))
        self.server_status = QLabel("尚未启动 · 设置监听地址和端口后，点击启动服务器。")
        self.server_status.setWordWrap(True)
        self.server_status.setTextFormat(Qt.TextFormat.PlainText)
        self.server_status.setObjectName("statusBanner"); self.server_status.setProperty("statusTone", "neutral")
        card.addWidget(self.server_status)

        card = self.card("02  地址与数据")
        self.address = line("DB1.DBW0")
        self.type = combo(["UInt16", "Int16", "UInt32", "Int32", "Float32", "Bool", "UInt8"])
        self.order = combo(["默认", "ABCD", "BADC", "CDAB", "DCBA"])
        self.count = spin(3, 1, 200)
        self.values = line("100 200 300")
        data = QGridLayout(); data.setHorizontalSpacing(14); data.setVerticalSpacing(14)
        data.setColumnStretch(0, 2); data.setColumnStretch(1, 1)
        for index, (title, field) in enumerate((("起始地址", self.address), ("数据类型", self.type), ("字节 / 字顺序", self.order), ("值的数量", self.count))):
            data.addWidget(self._field(title, field), index//2, index%2)
        data.addWidget(self._field("写入值 / 空格或逗号分隔", self.values), 2, 0, 1, 2)
        card.addLayout(data)
        self.data_hint = QLabel()
        self.data_hint.setObjectName("muted"); self.data_hint.setWordWrap(True); card.addWidget(self.data_hint)
        self.poll = QCheckBox("连续监视")
        self.interval = spin(1000, 100, 3600000)
        self.poll.toggled.connect(self._toggle_poll)
        self.read_button = button("读取", self.read, True)
        self.write_button = button("写入", self.write)
        self.client_actions = QWidget()
        self.client_actions.setLayout(row(self.read_button, self.write_button, self.poll, self.interval, "毫秒"))
        self.client_actions.layout().setContentsMargins(0, 0, 0, 0)
        card.addWidget(self.client_actions)
        self.read_memory_button = button("读取模拟内存", self.read_memory, True)
        self.write_memory_button = button("写入模拟内存", self.write_memory)
        self.server_actions = QWidget()
        self.server_actions.setLayout(row(self.read_memory_button, self.write_memory_button))
        self.server_actions.layout().setContentsMargins(0, 0, 0, 0)
        card.addWidget(self.server_actions)
        self.output = text_area(readonly=True)
        card.addWidget(self.output)
        self.layout.addStretch()
        self.protocol.currentTextChanged.connect(self._protocol_changed)
        self.role.currentIndexChanged.connect(self._role_changed)
        self.host.textChanged.connect(self._client_configuration_changed)
        for widget in (self.port, self.rack, self.slot, self.timeout):
            widget.valueChanged.connect(self._client_configuration_changed)
        self.busy_changed.connect(lambda _: self._update_controls())
        self._role_changed()

    @staticmethod
    def _field(title, widget):
        container = QWidget(); layout = QVBoxLayout(container)
        layout.setContentsMargins(0, 0, 0, 0); layout.setSpacing(7)
        label = QLabel(title); label.setObjectName("muted"); label.setProperty("fieldLabel", True)
        layout.addWidget(label); layout.addWidget(widget)
        return container

    def _role_changed(self, *_):
        client = self.role.currentIndex() == 0
        if not client:
            self.poll.setChecked(False)
            self.timer.stop()
        self.client_panel.setVisible(client)
        self.server_panel.setVisible(not client)
        self.client_actions.setVisible(client)
        self.server_actions.setVisible(not client)
        self.data_hint.setText("读取 / 写入目标 PLC；结果显示在下方。" if client else "先启动服务器，再读写本机模拟内存；外部客户端可访问同一数据区。")
        self.output.clear()
        self.output.setPlaceholderText("客户端读写结果" if client else "模拟内存读取结果")
        self.scroll.verticalScrollBar().setValue(0)
        self._update_controls()

    def _update_controls(self):
        idle = not self.busy and not self.closed
        running = self.server is not None
        self.role.setEnabled(idle); self.protocol.setEnabled(idle)
        self.loopback_button.setEnabled(idle)
        self.client_configuration.setEnabled(idle)
        self.connect_button.setEnabled(idle and not (self.client is not None and self.client.connected))
        self.disconnect_button.setEnabled(not self.closed and self.client is not None)
        self.read_button.setEnabled(idle)
        self.write_button.setEnabled(idle and not self.poll.isChecked())
        self.listen_host.setEnabled(idle and not running)
        self.listen_port.setEnabled(idle and not running)
        self.start_button.setEnabled(idle and not running)
        self.stop_button.setEnabled(idle and running)
        for widget in (self.read_memory_button, self.write_memory_button, self.import_button, self.export_button):
            widget.setEnabled(idle and running)
        self.s7_fields.setVisible(self.protocol.currentText() == "S7")

    def _server_notice(self, text, tone="neutral"):
        self.server_status.setText(text)
        self.server_status.setProperty("statusTone", tone)
        self.server_status.style().unpolish(self.server_status)
        self.server_status.style().polish(self.server_status)

    def _client_notice(self, text, tone="neutral"):
        self.client_status.setText(text)
        self.client_status.setProperty("statusTone", tone)
        self.client_status.style().unpolish(self.client_status)
        self.client_status.style().polish(self.client_status)

    def _client_configuration_changed(self, *_):
        if self.client is not None:
            self.disconnect_session()
            self._client_notice("连接参数已修改 · 请重新连接 PLC。")

    def _client_for_operation(self):
        if self.client is None:
            host = self.host.text().strip()
            if not host:
                raise ValueError("请输入 PLC 的 IP 地址或主机名")
            self.client = PlcClient(protocol=self.protocol.currentText(), host=host, port=self.port.value(),
                timeout=self.timeout.value()/1000, rack=self.rack.value(), slot=self.slot.value(),
                on_traffic=lambda direction, data, peer="": self.hub.log("PLC 客户端", direction, data, peer))
        return self.client

    @staticmethod
    def _connection_error(error, timeout):
        if isinstance(error, (TimeoutError, socket.timeout)):
            return f"{round(timeout*1000)} ms 内未响应，请检查 IP、端口及协议参数。"
        if isinstance(error, ConnectionRefusedError):
            return "目标拒绝连接，请检查端口及 PLC 通信服务是否已开启。"
        if isinstance(error, socket.gaierror):
            return "无法解析目标地址，请检查 IP 或主机名。"
        return str(error) or type(error).__name__

    def _run_client(self, action, operation, done=None):
        if self.busy or self.closed or self.role.currentIndex() != 0:
            return
        try:
            client = self._client_for_operation()
        except Exception as error:
            message = f"连接失败 · {self.host.text().strip()}:{self.port.value()} · {error}"
            self._client_notice(message, "error"); self.hub.set_notice(message)
            self.poll.setChecked(False)
            self._update_controls()
            return
        endpoint = f"{client.host}:{client.port}"
        self._client_operation = action
        self._client_notice(f"正在{action} PLC · {endpoint}…", "info")
        def work():
            try:
                return operation(client), None
            except Exception as error:
                client.close()
                return None, self._connection_error(error, client.timeout)
        def finished(result):
            if self.client is not client:
                client.close()
                return  # A cancelled operation cannot replace the disconnected state.
            self._client_operation = None
            value, error = result
            if error:
                self.client = None
                self.poll.setChecked(False)
                message = f"{'连接失败' if action == '连接' else 'PLC '+action+'失败'} · {endpoint} · {error}"
                self._client_notice(message, "error"); self.hub.set_notice(message)
            else:
                scope = "TCP 已连接" if client.protocol == "MC3E" else "已连接，协议握手成功"
                message = f"{self.protocol.currentText()} {scope} · {endpoint}"
                self._client_notice(message, "success")
                if done:
                    done(value)
                else:
                    self.hub.set_notice(message)
            self._update_controls()
        self.submit(work, finished)

    def operation_failed(self, message):
        super().operation_failed(message)
        if self._starting_server:
            self._starting_server = False
            self._server_notice("启动失败 · "+message, "error")

    def _protocol_changed(self, protocol):
        self.disconnect_session()
        self.stop_server()
        self.port.setValue({"S7": 1102, "MC": 5000, "FINS": 9600}[protocol])
        self.listen_port.setValue(self.port.value())
        self.address.setText({"S7": "DB1.DBW0", "MC": "D0", "FINS": "D0"}[protocol])
        self.output.clear()
        self._update_controls()

    def connect_client(self):
        if self.busy or self.closed or self.role.currentIndex() != 0:
            return
        if self.client is not None and self.client.connected:
            return
        self._run_client("连接", lambda client: client.connect())

    def args(self):
        return dict(address=self.address.text().strip(), data_type=self.type.currentText(), byte_order=None if self.order.currentIndex() == 0 else self.order.currentText())

    def read(self):
        if self.busy or self.closed or self.role.currentIndex() != 0:
            return
        args, count = self.args(), self.count.value()
        self._run_client("读取", lambda client: client.read(count=count, **args), self.show)

    def write(self):
        if self.busy or self.closed or self.role.currentIndex() != 0:
            return
        try:
            values = [float(x) if self.type.currentText() == "Float32" else int(x, 0) if x.lower().startswith("0x") else int(x) for x in self.values.text().replace(",", " ").split()]
            if not values:
                raise ValueError("请输入写入值")
            args = self.args()
            self._run_client("写入", lambda client: client.write(values=values, **args), lambda _: self.hub.set_notice("PLC 写入完成"))
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def show(self, values):
        self.output.setPlainText("\n".join(f"[{index}]  {value}" for index, value in enumerate(values)))
        self.hub.set_notice(f"PLC 读取完成：{len(values)} 个值")

    def _toggle_poll(self, enabled):
        if enabled:
            if self.role.currentIndex() != 0:
                self.poll.setChecked(False)
                return
            self.timer.start(self.interval.value())
            self.read()
        else:
            self.timer.stop()
        self._update_controls()

    def start_server(self):
        if self.busy or self.server or self.closed or self.role.currentIndex() != 1:
            return
        protocol, host, port = self.protocol.currentText(), self.listen_host.text().strip(), self.listen_port.value()
        self._starting_server = True
        self._server_notice("正在启动服务器…", "info")
        self.submit(lambda: PlcServer(protocol=protocol, host=host, port=port,
            on_traffic=lambda direction, data, peer="": self.hub.log("PLC 模拟", direction, data, peer)).start(), self._server_started)

    def _server_started(self, server):
        self._starting_server = False
        self.server = server
        self.listen_host.setText(server.host)
        self.listen_port.setValue(server.bound_port)
        text = f"{self.protocol.currentText()} 模拟已启动：{server.host}:{server.bound_port}"
        self._server_notice(text, "success")
        self.hub.set_notice(text)
        self._update_controls()

    def loopback(self):
        if self.busy:
            return
        self.role.setCurrentIndex(0)
        self.disconnect_session()
        self.stop_server()
        protocol = self.protocol.currentText()
        def create():
            server = PlcServer(protocol=protocol, host="127.0.0.1", port=0,
                on_traffic=lambda direction, data, peer="": self.hub.log("PLC 模拟", direction, data, peer)).start()
            try:
                address = {"S7": "DB1.DBW0", "MC": "D0", "FINS": "D0"}[protocol]
                server.memory.write(address, [2300, 1234, 5678], data_type="UInt16")
                return server
            except Exception:
                server.stop()
                raise
        def finish(server):
            self._server_started(server)
            self.host.setText("127.0.0.1")
            self.port.setValue(server.bound_port)
            self.address.setText({"S7": "DB1.DBW0", "MC": "D0", "FINS": "D0"}[protocol])
            self.type.setCurrentText("UInt16")
            self.count.setValue(3)
            QTimer.singleShot(0, self.read)
        self.submit(create, finish)

    def read_memory(self):
        if not self.server:
            self.hub.set_notice("请先启动 PLC 模拟服务器")
            return
        try:
            self.show(self.server.memory.read(count=self.count.value(), **self.args()))
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def write_memory(self):
        if not self.server:
            self.hub.set_notice("请先启动 PLC 模拟服务器")
            return
        try:
            values = [float(x) if self.type.currentText() == "Float32" else int(x) for x in self.values.text().replace(",", " ").split()]
            self.server.memory.write(values=values, **self.args())
            self.read_memory()
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def export_memory(self):
        if not self.server:
            self.hub.set_notice("请先启动 PLC 模拟服务器")
            return
        try:
            path, _ = QFileDialog.getSaveFileName(self, "导出 PLC 内存快照", "plc-memory.json", "JSON (*.json)")
            if path:
                with open(path, "w", encoding="utf-8") as stream:
                    json.dump(self.server.memory.snapshot(), stream, ensure_ascii=False, indent=2)
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def import_memory(self):
        if not self.server:
            self.hub.set_notice("请先启动同协议的 PLC 模拟服务器")
            return
        path, _ = QFileDialog.getOpenFileName(self, "导入 PLC 模拟内存", "", "JSON (*.json)")
        if not path:
            return
        try:
            from pathlib import Path
            if Path(path).stat().st_size > 20 * 1024 * 1024:
                raise ValueError("快照文件超过 20 MiB")
            with open(path, encoding="utf-8-sig") as stream:
                data = json.load(stream)
            self.server.memory.restore(data)
            self.read_memory()
            self.hub.set_notice("PLC 模拟内存已恢复；没有向客户端设备发送写入")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def stop_server(self):
        server, self.server = self.server, None
        if server:
            server.stop()
        self._starting_server = False
        self._server_notice("服务器已停止。" if server else "尚未启动 · 设置监听地址和端口后，点击启动服务器。")
        self._update_controls()

    def disconnect_session(self):
        self.timer.stop()
        if hasattr(self, "poll"):
            self.poll.setChecked(False)
        client, self.client = self.client, None
        self._client_operation = None
        if client:
            client.close()
        self._client_notice("客户端已断开。" if client else "未连接 · 点击连接 PLC 后检查目标地址与协议。")
        self._update_controls()

    def close_resources(self):
        super().close_resources()
        self.disconnect_session()
        self.stop_server()
