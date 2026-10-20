from __future__ import annotations

import csv
import json
import struct
from datetime import datetime
from pathlib import Path
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication, QFileDialog, QCheckBox, QLabel
from .common import Page, combo, line, spin, button, row, form, text_area, table, configure_columns, set_cell
from .industrial_pages import FUNCTIONS, parse_values
from .communication_pages import CHECKSUMS
from ..protocols import codec
from ..protocols.modbus import build_pdu, build_rtu, build_tcp
from .. import APP_NAME


class WorkbenchPage(Page):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        card = self.card("01  报文生成", "地址从 0 起算。生成不会发送；选择已建立的原始串口或网络连接后才能发送。")
        self.mode = combo(["Modbus RTU", "Modbus TCP", "自定义 HEX"])
        self.function = combo(FUNCTIONS, FUNCTIONS[2])
        self.unit = spin(1, 1, 247)
        self.address = spin(0)
        self.count = spin(3, 1, 2000)
        self.transaction = spin(1)
        self.values = line("100 200 300")
        self.custom = line("01 03 00 00 00 03")
        self.checksum = combo(CHECKSUMS, "CRC16")
        card.addLayout(form(报文类型=self.mode, 功能码=self.function, 站号=self.unit, 地址=self.address, 数量=self.count,
                            事务ID=self.transaction, 写入值=self.values, 自定义HEX=self.custom, 自定义校验=self.checksum))
        card.addLayout(row(button("生成报文", self.generate, True), button("导入模板", self.load_template), button("保存模板", self.save_template)))
        card = self.card("02  预览与发送")
        self.frame = text_area("01 03 00 00 00 03 05 CB")
        self.target = combo([])
        hub.connections_changed.connect(self.refresh_targets)
        self.length = QLabel("8 字节")
        self.frame.textChanged.connect(self._length)
        card.addWidget(self.frame)
        card.addLayout(row(self.length, button("复制", lambda: QApplication.clipboard().setText(self.frame.toPlainText())), self.target, button("发送报文", self.send_frame, True)))
        self.layout.addStretch()

    def _length(self):
        try:
            self.length.setText(f"{len(codec.encode(self.frame.toPlainText(), 'HEX'))} 字节")
        except ValueError:
            self.length.setText("HEX 格式待修正")

    def generate(self):
        try:
            if self.mode.currentIndex() == 2:
                raw = codec.encode(self.custom.text(), "HEX")
                data = raw + codec.checksum(raw, self.checksum.currentText())
            else:
                function = int(self.function.currentText().split()[0])
                values = parse_values(self.values.text()) if function > 4 else None
                pdu = build_pdu(function, self.address.value(), self.count.value(), values)
                data = build_rtu(self.unit.value(), pdu) if self.mode.currentIndex() == 0 else build_tcp(self.transaction.value(), self.unit.value(), pdu)
            self.frame.setPlainText(data.hex(" ").upper())
            self.hub.set_notice("报文已生成，请确认目标后发送")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def refresh_targets(self):
        selected = self.target.currentText()
        self.target.clear()
        self.target.addItems(list(self.hub.connections))
        if selected in self.hub.connections:
            self.target.setCurrentText(selected)

    def send_frame(self):
        try:
            sender = self.hub.connections.get(self.target.currentText())
            if not sender:
                raise ValueError("请先在串口或 TCP / UDP 模块启动连接")
            data = codec.encode(self.frame.toPlainText(), "HEX")
            if not 1 <= len(data) <= 65507:
                raise ValueError("报文长度必须为 1～65507 字节")
            target = self.target.currentText()
            self.submit(lambda: sender(data), lambda _: self.hub.log(target, "TX", data, "报文工作台"))
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def save_template(self):
        try:
            codec.encode(self.frame.toPlainText(), "HEX")
            path, _ = QFileDialog.getSaveFileName(self, "保存报文模板", "frame.json", "JSON (*.json)")
            if path:
                Path(path).write_text(json.dumps({"name": Path(path).stem, "hex": self.frame.toPlainText()}, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def load_template(self):
        path, _ = QFileDialog.getOpenFileName(self, "导入报文模板", "", "JSON (*.json)")
        if not path:
            return
        try:
            if Path(path).stat().st_size > 2000000:
                raise ValueError("模板文件过大")
            data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
            value = data.get("hex", data.get("Hex"))
            raw = codec.encode(value, "HEX")
            self.mode.setCurrentIndex(2)
            self.custom.setText(raw.hex(" ").upper())
            self.checksum.setCurrentText("None")
            self.frame.setPlainText(self.custom.text())
        except Exception as exc:
            self.hub.set_notice(str(exc))


class MonitorPage(Page):
    def __init__(self, hub, parent=None):
        super().__init__(hub, parent)
        self.dirty = False
        card = self.card("01  通信时间线", "保留最近 10,000 条记录；界面显示筛选后的最近 500 条，导出包含全部匹配记录。")
        self.filter = line("", "来源、报文或文本")
        self.direction = combo(["全部", "TX", "RX", "ERR"])
        self.paused = QCheckBox("暂停刷新")
        self.filter.textChanged.connect(self.refresh)
        self.direction.currentTextChanged.connect(self.refresh)
        self.paused.toggled.connect(self.refresh)
        card.addLayout(row(self.filter, self.direction, self.paused, button("清空", self.clear), button("导出 CSV", lambda: self.export("csv")), button("导出 JSON", lambda: self.export("json"))))
        self.grid = table(["时间", "来源", "方向", "HEX / 内容", "说明"], min_height=280,
                          empty_text="还没有通信记录", empty_hint="启动串口或网络连接后，收发报文会显示在这里。")
        configure_columns(self.grid, [140, 150, 76, None, 190], minimum_stretch=240)
        self.grid.itemSelectionChanged.connect(self.selected)
        card.addWidget(self.grid)
        self.timer = QTimer(self)
        self.timer.setInterval(200)
        self.timer.timeout.connect(self._tick)
        self.timer.start()
        hub.event.connect(lambda _: setattr(self, "dirty", True))
        card = self.card("02  协议解析与校验")
        self.input = text_area("01 03 00 00 00 0A C5 CD", height=90)
        self.protocol = combo(["Modbus RTU", "Modbus TCP"])
        self.checksum = combo(CHECKSUMS, "CRC16")
        self.details = text_area(readonly=True, height=110)
        card.addWidget(self.input)
        card.addLayout(row(self.protocol, button("解析", self.analyze), self.checksum, button("计算校验", self.calculate)))
        card.addWidget(self.details)
        card = self.card("03  数值转换")
        self.number = line("1234")
        self.number_base = combo(["十进制", "十六进制", "二进制"])
        self.converted = text_area(readonly=True, height=80)
        card.addLayout(row(self.number, self.number_base, button("转换", self.convert)))
        card.addWidget(self.converted)
        self.layout.addStretch()
        self.displayed = []

    def _tick(self):
        if self.dirty and not self.paused.isChecked():
            self.refresh()
            self.dirty = False

    def entries(self):
        text, direction = self.filter.text().lower(), self.direction.currentText()
        return [entry for entry in self.hub.events if (direction == "全部" or entry["direction"] == direction)
                and (not text or text in (entry["source"]+entry["hex"]+entry["text"]+entry["note"]).lower())]

    def refresh(self):
        if self.paused.isChecked():
            return
        self.displayed = self.entries()[-500:]
        self.grid.setRowCount(len(self.displayed))
        for index, entry in enumerate(self.displayed):
            for column, key in enumerate(("time", "source", "direction", "hex", "note")):
                value = str(entry[key])
                tone = {"TX": "info", "RX": "success", "ERR": "error"}.get(value) if key == "direction" else None
                shown = value[:1200]
                if key == "time":
                    try:
                        shown = datetime.fromisoformat(value).strftime("%H:%M:%S.%f")[:-3]
                    except ValueError:
                        pass
                set_cell(self.grid, index, column, shown, tone=tone, tooltip=value)
        if not self.displayed and (self.filter.text() or self.direction.currentIndex()):
            self.grid.set_empty_text("没有匹配的记录", "调整来源、方向或搜索内容后重试。")
        else:
            self.grid.set_empty_text("还没有通信记录", "启动串口或网络连接后，收发报文会显示在这里。")
        self.grid.scrollToBottom()

    def clear(self):
        self.hub.clear()
        self.refresh()

    def selected(self):
        index = self.grid.currentRow()
        if 0 <= index < len(self.displayed):
            entry = self.displayed[index]
            self.input.setPlainText(entry["hex"])
            self.details.setPlainText(f"{entry['source']} · {entry['note']}\nUTF-8：{entry['text']}")

    def export(self, extension):
        path, _ = QFileDialog.getSaveFileName(self, "导出通信记录", APP_NAME+"-通信记录."+extension, f"{extension.upper()} (*.{extension})")
        if not path:
            return
        try:
            entries = [{key: value for key, value in entry.items() if key != "data"} for entry in self.entries()]
            if extension == "json":
                Path(path).write_text(json.dumps(entries, ensure_ascii=False, indent=2), encoding="utf-8")
            else:
                with open(path, "w", encoding="utf-8-sig", newline="") as stream:
                    writer = csv.DictWriter(stream, fieldnames=["time", "source", "direction", "hex", "text", "note"])
                    writer.writeheader()
                    for entry in entries:
                        # Keep spreadsheet software from treating device-controlled text as formulas.
                        writer.writerow({key: "'"+str(value) if str(value).startswith(("=", "+", "-", "@")) else value for key, value in entry.items()})
            self.hub.set_notice(f"已导出 {len(entries)} 条记录")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def analyze(self):
        try:
            data = codec.encode(self.input.toPlainText(), "HEX")
            if self.protocol.currentIndex() == 0:
                if len(data) < 4:
                    raise ValueError("RTU 报文至少 4 字节")
                valid = codec.checksum(data[:-2], "CRC16") == data[-2:]
                unit, pdu = data[0], data[1:-2]
                prefix = f"RTU / CRC {'正确' if valid else '错误'}"
            else:
                if len(data) < 8:
                    raise ValueError("TCP 报文至少 8 字节")
                transaction, protocol, size, unit = struct.unpack(">HHHB", data[:7])
                if protocol != 0 or size != len(data)-6:
                    raise ValueError("MBAP 协议号或长度不匹配")
                pdu, prefix = data[7:], f"TCP / 事务 {transaction} / 长度 {size}"
            function = pdu[0]
            description = f"{prefix}\n站号 {unit} / 功能码 0x{function:02X}"
            if function & 0x80 and len(pdu) >= 2:
                description += f" / 异常码 {pdu[1]}"
            elif len(pdu) == 5:
                address, value = struct.unpack(">HH", pdu[1:5])
                description += f"\n地址 {address} / 数量或值 {value}"
            else:
                description += f"\nPDU：{pdu.hex(' ').upper()}"
            self.details.setPlainText(description)
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def calculate(self):
        try:
            raw = codec.encode(self.input.toPlainText(), "HEX")
            check = codec.checksum(raw, self.checksum.currentText())
            self.details.setPlainText(f"{self.checksum.currentText()}：{check.hex(' ').upper()}\n追加：{(raw+check).hex(' ').upper()}")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def convert(self):
        try:
            value = int(self.number.text().strip(), [10, 16, 2][self.number_base.currentIndex()])
            self.converted.setPlainText(f"十进制：{value}\n十六进制：{value:#x}\n二进制：{value:#b}")
        except Exception as exc:
            self.hub.set_notice(str(exc))

    def close_resources(self):
        super().close_resources()
        self.timer.stop()
