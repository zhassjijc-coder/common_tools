from __future__ import annotations

from PySide6.QtCore import QTimer, Signal
from PySide6.QtWidgets import QWidget, QVBoxLayout, QCheckBox
from serial.tools import list_ports
from .common import Page, combo, line, spin, button, row, form, text_area
from ..protocols import codec
from ..protocols.transport import SerialTransport, NetworkTransport


CHECKSUMS = ["None", "CRC16", "CRC32", "SUM8", "XOR8", "LRC"]


class SendPanel(QWidget):
    def __init__(self, owner):
        super().__init__()
        self.owner = owner
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        self.mode = combo(["HEX", "UTF-8", "ASCII"])
        self.checksum = combo(CHECKSUMS)
        self.payload = text_area("01 03 00 00 00 03", height=110)
        self.period = spin(1000, 50, 3600000)
        self.periodic = QCheckBox("定时发送")
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.send)
        self.periodic.toggled.connect(self._toggle)
        self.period.valueChanged.connect(lambda value: self.timer.setInterval(value))
        layout.addLayout(row("编码", self.mode, "校验", self.checksum))
        layout.addWidget(self.payload)
        layout.addLayout(row(button("发送", self.send, True), self.periodic, self.period, "毫秒"))

    def _toggle(self, checked):
        if checked:
            self.timer.start(self.period.value())
        else:
            self.timer.stop()

    def data(self):
        raw = codec.encode(self.payload.toPlainText(), self.mode.currentText())
        return raw + codec.checksum(raw, self.checksum.currentText())

    def send(self):
        if self.owner.busy or self.owner.closed:
            return
        try:
            raw = self.data()
            if not raw:
                raise ValueError("请输入发送内容")
            if self.owner.session is None:
                raise ValueError("请先打开连接")
            session = self.owner.session
            self.owner.submit(lambda: session.send(raw), lambda _: self.owner.hub.log(self.owner.source, "TX", raw))
        except Exception as exc:
            self.timer.stop()
            self.periodic.setChecked(False)
            self.owner.hub.set_notice(str(exc))

    def stop(self):
        self.timer.stop()
        self.periodic.setChecked(False)


class StreamPage(Page):
    received = Signal(bytes, str)
    state_changed = Signal(str)

    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.session = None
        self.source = "连接"
        self.received.connect(self.on_receive)
        self.state_changed.connect(hub.set_notice)

    def add_io(self):
        card = self.card("02  收发工作区", "收发报文同时记录到监视与工具，可按模块筛选并导出。")
        self.sender = SendPanel(self)
        card.addWidget(self.sender)
        self.receive_mode = combo(["HEX", "UTF-8", "ASCII"])
        self.auto_reply = combo(["不自动回复", "原样回显", "固定回复"])
        self.reply = line("OK")
        card.addLayout(row("接收显示", self.receive_mode, "回复", self.auto_reply, self.reply))
        self.output = text_area(readonly=True, height=220)
        self.output.document().setMaximumBlockCount(1500)
        card.addWidget(self.output)
        card.addLayout(row(button("清空显示", self.output.clear)))
        self.layout.addStretch()

    def on_receive(self, data, peer):
        if self.closed:
            return
        self.hub.log(self.source, "RX", data, peer)
        shown = codec.decode(data, self.receive_mode.currentText())
        self.output.appendPlainText(f"{peer}  {shown}")
        if self.session and self.auto_reply.currentIndex() > 0:
            reply = data if self.auto_reply.currentIndex() == 1 else self.reply.text().encode("utf-8")
            session = self.session
            if not self.busy:
                self.submit(lambda: session.send(reply, peer=peer), lambda _: self.hub.log(self.source, "TX", reply, peer))

    def opened(self, session):
        if self.closed:
            session.stop()
            return
        self.session = session
        self.hub.register(self.source, session.send)
        self.hub.set_notice(f"{self.source} 已启动")
        self.open_button.setEnabled(False)
        self.close_button.setEnabled(True)

    def disconnect_session(self):
        self.sender.stop()
        session, self.session = self.session, None
        self.hub.unregister(self.source)
        if session:
            session.stop()
        self.open_button.setEnabled(True)
        self.close_button.setEnabled(False)

    def close_resources(self):
        super().close_resources()
        self.disconnect_session()


class SerialPage(StreamPage):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.source = "串口"
        card = self.card("01  串口 / RS485", "选择系统现有端口，打开连接后收发；RS485 方向切换由适配器及驱动负责。")
        self.port = combo([], editable=True)
        self.baud = combo([1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800], 9600, True)
        self.bits = combo([5, 6, 7, 8], 8)
        self.parity = combo(["N", "E", "O", "M", "S"])
        self.stopbits = combo(["1", "1.5", "2"])
        self.open_button = button("打开串口", self.connect_serial, True)
        self.close_button = button("关闭", self.disconnect_session)
        self.close_button.setEnabled(False)
        card.addLayout(form(端口=self.port, 波特率=self.baud, 数据位=self.bits, 校验位=self.parity, 停止位=self.stopbits))
        card.addLayout(row(button("刷新端口", self.refresh_ports), self.open_button, self.close_button))
        self.add_io()
        self.refresh_ports()

    def refresh_ports(self):
        current = self.port.currentText()
        self.port.clear()
        for item in list_ports.comports():
            self.port.addItem(item.device)
            self.port.setItemData(self.port.count()-1, item.description, 3)
        if current:
            self.port.setCurrentText(current)

    def connect_serial(self):
        if self.session or self.busy:
            return
        try:
            config = dict(port=self.port.currentText().strip(), baudrate=int(self.baud.currentText()),
                          bytesize=int(self.bits.currentText()), parity=self.parity.currentText(), stopbits=float(self.stopbits.currentText()))
            if not config["port"]:
                raise ValueError("请选择串口")
            self.submit(lambda: SerialTransport(**config, on_data=lambda data, peer="": self.received.emit(bytes(data), str(peer))).start(), self.opened)
        except Exception as exc:
            self.hub.set_notice(str(exc))


class NetworkPage(StreamPage):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.source = "TCP / UDP"
        card = self.card("01  网络连接", "TCP 客户端、多个客户端连接的服务器与 UDP 收发。服务器发送默认发给全部已连接客户端。")
        self.mode = combo(["TCP Client", "TCP Server", "UDP"])
        self.host = line("127.0.0.1")
        self.port = spin(9000, 1)
        self.local_host = line("0.0.0.0")
        self.local_port = spin(9000, 0)
        self.open_button = button("启动连接", self.connect_network, True)
        self.close_button = button("停止", self.disconnect_session)
        self.close_button.setEnabled(False)
        card.addLayout(form(模式=self.mode, 远端地址=self.host, 远端端口=self.port, 本地地址=self.local_host, 监听端口=self.local_port))
        card.addLayout(row(self.open_button, self.close_button))
        self.add_io()

    def connect_network(self):
        if self.session or self.busy:
            return
        config = dict(mode=self.mode.currentText(), host=self.host.text().strip(), port=self.port.value(),
                      local_host=self.local_host.text().strip(), local_port=self.local_port.value(),
                      on_data=lambda data, peer="": self.received.emit(bytes(data), str(peer)),
                      on_state=self.state_changed.emit)
        self.submit(lambda: NetworkTransport(**config).start(), self.opened)
