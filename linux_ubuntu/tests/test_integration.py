"""Headless Qt integration against real, isolated loopback listeners."""
from __future__ import annotations

import os
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from pathlib import Path
import socket
import struct
import threading
import time
import unittest
from unittest.mock import patch

from PySide6.QtCore import Qt
from PySide6.QtTest import QTest
from PySide6.QtWidgets import QApplication, QPushButton

from linklab.ui.main_window import MainWindow
from linklab.ui.theme import configure_font, stylesheet
from linklab.protocols.modbus import ModbusServer
from linklab.protocols.plc import PlcClient, PlcServer, _frame
from linklab.protocols.transport import NetworkTransport, SerialTransport


APP = QApplication.instance() or QApplication([])
APP.setQuitOnLastWindowClosed(False)
configure_font(APP)
APP.setStyleSheet(stylesheet(False))


def wait(predicate, timeout=5, message="Qt operation did not finish"):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        APP.processEvents()
        if predicate():
            APP.processEvents()
            return
        QTest.qWait(10)
    raise AssertionError(message() if callable(message) else message)


def press(page, label):
    choices = [widget for widget in page.findChildren(QPushButton) if widget.text() == label]
    if len(choices) != 1:
        raise AssertionError(f"Expected one {label!r} button, got {len(choices)}")
    widget = choices[0]
    if hasattr(page, "scroll"):
        page.scroll.ensureWidgetVisible(widget)
    APP.processEvents()
    widget.click()
    APP.processEvents()


def released(port):
    with socket.socket() as stream:
        stream.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        stream.bind(("127.0.0.1", port))


class HandshakePeer:
    """Accept a real TCP connection, then stall or send a malformed PLC handshake."""
    def __init__(self, protocol, wrong=False):
        self.protocol, self.wrong = protocol, wrong
        self.accepted, self.received, self.closed = threading.Event(), threading.Event(), threading.Event()
        self.stop = threading.Event()
        self.listener = self.stream = self.thread = None
        self.port = 0

    def __enter__(self):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0)); self.listener.listen(1); self.listener.settimeout(2)
        self.port = self.listener.getsockname()[1]
        self.thread = threading.Thread(target=self.serve, name="Test PLC handshake", daemon=True)
        self.thread.start()
        return self

    def serve(self):
        try:
            stream, _ = self.listener.accept()
            self.stream = stream
            self.accepted.set()
            request = _frame(stream, self.protocol, time.monotonic() + 2)
            self.received.set()
            if self.wrong:
                if self.protocol == "S7":
                    response = bytearray(request)
                    if self.wrong == "wrong-pdu":
                        # COTP success is insufficient: PDU negotiation is also
                        # part of connecting to an S7 PLC.
                        response[5], response[6:8], response[8:10] = 0xD0, request[8:10], b"\x00\x01"
                        stream.sendall(response)
                        setup = _frame(stream, self.protocol, time.monotonic() + 2)
                        stream.sendall(bytes.fromhex("03 00 00 1B 02 F0 80 32 03 00 00") + setup[11:13]
                                       + bytes.fromhex("00 08 00 00 81 04 F0 00 00 01 00 01 01 E0"))
                    else:
                        # Valid TPKT envelope, invalid COTP destination reference.
                        response[5], response[6:8], response[8:10] = 0xD0, b"\x99\x99", b"\x00\x01"
                        stream.sendall(response)
                else:
                    # FINS command 1 response explicitly reports an error.
                    stream.sendall(b"FINS" + struct.pack(">IIIII", 16, 1, 0x20, 2, 1))
            stream.settimeout(.05)
            while not self.stop.is_set():
                try:
                    if not stream.recv(8192):
                        self.closed.set()
                        return
                except socket.timeout:
                    continue
        except (OSError, EOFError):
            pass
        finally:
            if self.stream:
                self.stream.close()

    def __exit__(self, *_):
        self.stop.set()
        if self.listener:
            self.listener.close()
        if self.stream:
            try:
                self.stream.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.stream.close()
        if self.thread:
            self.thread.join(2)


class QtIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.windows = []
        self.errors = []
        self.previous_hook = threading.excepthook
        threading.excepthook = lambda args: self.errors.append(args.exc_value)

    def tearDown(self):
        for window in self.windows:
            window.close()
        wait(lambda: all(window._closed for window in self.windows), message="MainWindow did not finish shutting down")
        for window in self.windows:
            window.close()  # Idempotent close after all workers/resources finish.
            window.deleteLater()
        APP.processEvents()
        threading.excepthook = self.previous_hook
        self.assertEqual(self.errors, [], "A protocol worker crashed during Qt interaction")

    def window(self):
        window = MainWindow(test_mode=True)
        window.resize(1280, 850)
        window.show()
        self.windows.append(window)
        APP.processEvents()
        return window

    def idle(self, page):
        wait(lambda: not page.busy, message=f"{type(page).__name__} stayed busy")

    def test_startup_is_passive_and_test_mode_never_saves(self):
        with patch("pathlib.Path.write_text", side_effect=AssertionError("Unexpected settings write")), \
             patch.object(NetworkTransport, "start", side_effect=AssertionError("Unexpected network start")) as network, \
             patch.object(SerialTransport, "start", side_effect=AssertionError("Unexpected serial start")) as serial, \
             patch.object(ModbusServer, "start", side_effect=AssertionError("Unexpected Modbus start")) as modbus, \
             patch.object(PlcServer, "start", side_effect=AssertionError("Unexpected PLC start")) as plc:
            window = self.window()
            self.assertEqual(window.hub.tx, 0)
            self.assertEqual(window.hub.rx, 0)
            self.assertEqual(window.hub.connections, {})
            self.assertFalse(window.settings.controller.enabled)
            self.assertIsNone(window.tray)
            self.assertNotIn("virtualserial", window.pages)
            window.save_state()
            QTest.qWait(100)
            network.assert_not_called(); serial.assert_not_called()
            modbus.assert_not_called(); plc.assert_not_called()

    def test_modbus_buttons_read_write_and_poll_stop(self):
        window = self.window()
        sim, master = window.pages["simulator"], window.pages["master"]
        window.navigate("simulator")
        sim.host.setText("127.0.0.1"); sim.port.setValue(0)
        press(sim, "启动模拟")
        self.idle(sim)
        self.assertIsNotNone(sim.server, window.statusBar().currentMessage())
        port = sim.server.bound_port
        window.navigate("master")
        master.host.setText("127.0.0.1"); master.port.setValue(port)
        press(master, "准备连接")
        press(master, "执行一次")
        self.idle(master)
        self.assertEqual([master.result.item(n, 1).text() for n in range(3)], ["2300", "1234", "5678"])
        master.function.setCurrentIndex(5); master.values.setText("4321")
        press(master, "执行一次"); self.idle(master)
        self.assertEqual(sim.memory.read("holding", 0), [4321])
        master.function.setCurrentIndex(2)
        master.interval.setValue(100)
        master.poll.setChecked(True)
        baseline = len(window.hub.events)
        wait(lambda: len(window.hub.events) >= baseline + 8)
        self.assertTrue(master.polling.isActive())
        master.poll.setChecked(False); self.idle(master)
        self.assertFalse(master.polling.isActive())
        baseline = window.hub.tx
        QTest.qWait(250); APP.processEvents()
        self.assertEqual(window.hub.tx, baseline, "Polling transmitted after it was stopped")
        window.navigate("monitor")
        monitor = window.pages["monitor"]
        monitor.refresh()
        self.assertGreater(monitor.grid.rowCount(), 0)
        window.close()
        wait(lambda: window._closed)
        released(port)

    def test_plc_loopback_buttons_each_vendor_and_memory(self):
        window = self.window()
        page = window.pages["plc"]
        window.navigate("plc")
        for protocol in ("S7", "MC", "FINS"):
            with self.subTest(protocol=protocol):
                page.protocol.setCurrentText(protocol)
                baseline = len(window.hub.events)
                press(page, "本机联调")
                wait(lambda: page.server is not None and page.client is not None and not page.busy,
                     message=lambda: f"{protocol} loopback: busy={page.busy}, server={page.server}, client={page.client}; status={window.statusBar().currentMessage()}; output={page.output.toPlainText()}")
                self.assertEqual(page.role.currentIndex(), 0, "Local loopback did not return to the client role")
                self.assertIn("2300", page.output.toPlainText(), window.statusBar().currentMessage())
                self.assertIn("5678", page.output.toPlainText())
                self.assertGreater(len(window.hub.events), baseline)
                page.values.setText("1357 2468 3690")
                press(page, "写入"); self.idle(page)
                page.output.clear()
                press(page, "读取"); self.idle(page)
                self.assertIn("2468", page.output.toPlainText(), window.statusBar().currentMessage())
                self.assertEqual(page.server.memory.read(page.address.text(), 3), [1357, 2468, 3690])
                old_port = page.server.bound_port
                press(page, "断开")
                page.role.setCurrentIndex(1); APP.processEvents()
                press(page, "停止服务器")
                released(old_port)

    def test_plc_server_role_external_clients_and_passive_role_switch(self):
        window = self.window()
        page = window.pages["plc"]
        window.navigate("plc")
        for protocol in ("S7", "MC", "FINS"):
            with self.subTest(protocol=protocol):
                page.protocol.setCurrentText(protocol)
                self.assertIsNone(page.server)
                self.assertIsNone(page.client)
                baseline = (window.hub.tx, window.hub.rx)
                page.role.setCurrentIndex(1); APP.processEvents()
                self.assertTrue(page.server_panel.isVisible())
                self.assertFalse(page.client_panel.isVisible())
                visible = {widget.text() for widget in page.findChildren(QPushButton) if widget.isVisible()}
                self.assertTrue({"启动服务器", "停止服务器", "读取模拟内存", "写入模拟内存"}.issubset(visible))
                self.assertFalse({"连接 PLC", "断开", "读取", "写入"} & visible, "Client actions remained visible in the server role")
                self.assertTrue(all(widget.isVisible() for widget in (page.address, page.type, page.count, page.order, page.values, page.output)))
                self.assertIsNone(page.server, "Selecting the server role started a listener")
                self.assertIsNone(page.client, "Selecting the server role prepared a client")
                self.assertEqual((window.hub.tx, window.hub.rx), baseline)

                page.listen_host.setText("127.0.0.1"); page.listen_port.setValue(0)
                page.type.setCurrentText("UInt16"); page.count.setValue(3)
                page.values.setText("101 202 303")
                press(page, "启动服务器"); self.idle(page)
                server = page.server
                self.assertIsNotNone(server, window.statusBar().currentMessage())
                port = server.bound_port
                self.assertGreater(port, 0)
                self.assertIsNone(page.client, "Starting a simulator also created a page client")
                press(page, "写入模拟内存")
                address = page.address.text()

                with PlcClient(protocol=protocol, host="127.0.0.1", port=port, timeout=1) as external:
                    self.assertEqual(external.read(address, count=3), [101, 202, 303])
                    external.write(address, [909, 808, 707])
                    page.output.clear(); press(page, "读取模拟内存")
                    self.assertIn("909", page.output.toPlainText())
                    self.assertIn("707", page.output.toPlainText())

                    # The simulator's final TX notice is queued from its worker
                    # separately from the independent client's socket return.
                    QTest.qWait(30); APP.processEvents()
                    baseline = (window.hub.tx, window.hub.rx)
                    page.role.setCurrentIndex(0); APP.processEvents()
                    self.assertTrue(page.client_panel.isVisible())
                    self.assertFalse(page.server_panel.isVisible())
                    visible = {widget.text() for widget in page.findChildren(QPushButton) if widget.isVisible()}
                    self.assertTrue({"连接 PLC", "断开", "读取", "写入"}.issubset(visible))
                    self.assertFalse({"启动服务器", "停止服务器", "读取模拟内存", "写入模拟内存"} & visible, "Server actions remained visible in the client role")
                    self.assertIs(page.server, server)
                    self.assertEqual(page.server.bound_port, port)
                    self.assertIsNone(page.client, "Role selection automatically connected a client")
                    self.assertEqual((window.hub.tx, window.hub.rx), baseline, "Role selection transmitted PLC traffic")
                    self.assertEqual(external.read(address, count=3), [909, 808, 707], "Role selection interrupted the running simulator")

                    page.host.setText("127.0.0.1"); page.port.setValue(port)
                    page.interval.setValue(100)
                    page.poll.setChecked(True); self.idle(page)
                    self.assertTrue(page.timer.isActive())
                    page.role.setCurrentIndex(1); self.idle(page)
                    self.assertFalse(page.poll.isChecked())
                    self.assertFalse(page.timer.isActive())
                    self.assertIs(page.server, server)
                    QTest.qWait(30); APP.processEvents()
                    baseline = (window.hub.tx, window.hub.rx)
                    QTest.qWait(180); APP.processEvents()
                    self.assertEqual((window.hub.tx, window.hub.rx), baseline, "Hidden client polling continued in the server role")

                page.disconnect_session()
                press(page, "停止服务器")
                released(port)

    def test_plc_server_port_conflict_recovers_and_releases_listeners(self):
        window = self.window()
        page = window.pages["plc"]
        window.navigate("plc")
        page.role.setCurrentIndex(1)
        page.listen_host.setText("127.0.0.1")
        with socket.socket() as occupied:
            if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
                occupied.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            occupied.bind(("127.0.0.1", 0)); occupied.listen(1)
            occupied_port = occupied.getsockname()[1]
            page.listen_port.setValue(occupied_port)
            press(page, "启动服务器"); self.idle(page)
            self.assertIsNone(page.server, "A failed bind left a simulator attached to the page")
            self.assertTrue(page.role.isEnabled(), "The role selector stayed disabled after startup failed")
            self.assertTrue(page.start_button.isEnabled(), "Startup could not be retried after an occupied port")
            self.assertTrue(page.listen_port.isEnabled())
            self.assertFalse(page.stop_button.isEnabled())
            self.assertIn("启动失败", page.server_status.text(), window.statusBar().currentMessage())

            page.listen_port.setValue(0)
            press(page, "启动服务器"); self.idle(page)
            self.assertIsNotNone(page.server, window.statusBar().currentMessage())
            retry_port = page.server.bound_port
            self.assertGreater(retry_port, 0)
            self.assertNotEqual(retry_port, occupied_port)
            self.assertIn("模拟已启动", page.server_status.text())
            self.assertTrue(page.stop_button.isEnabled())
            press(page, "停止服务器")
            self.assertIsNone(page.server)
            self.assertTrue(page.start_button.isEnabled())
            released(retry_port)
        released(occupied_port)

    def test_plc_explicit_connection_handshakes_only_and_reuses_socket(self):
        window = self.window()
        page = window.pages["plc"]
        window.navigate("plc")
        self.assertEqual(page.timeout.value(), 2000)
        for protocol in ("S7", "MC", "FINS"):
            with self.subTest(protocol=protocol):
                traffic = []
                with PlcServer(protocol, port=0, on_traffic=lambda direction, data, peer: traffic.append((direction, data))) as server:
                    address = "DB1.DBW0" if protocol == "S7" else "D0"
                    server.memory.write(address, [101, 202, 303])
                    before = server.memory.snapshot()
                    page.protocol.setCurrentText(protocol)
                    page.host.setText("127.0.0.1"); page.port.setValue(server.bound_port)
                    page.timeout.setValue(1000)
                    page.address.setText(address); page.count.setValue(3); page.type.setCurrentText("UInt16")
                    page.output.clear()
                    press(page, "连接 PLC"); self.idle(page)
                    self.assertIsNotNone(page.client, page.client_status.text())
                    self.assertTrue(page.client.connected)
                    self.assertEqual(page.client_status.property("statusTone"), "success")
                    self.assertTrue(page.disconnect_button.isEnabled())
                    self.assertEqual(page.output.toPlainText(), "", "Connecting automatically read PLC memory")
                    self.assertEqual(server.memory.snapshot(), before, "Connecting changed simulator memory")
                    wait(lambda: server.client_count == 1)
                    requests = [frame for direction, frame in traffic if direction == "RX"]
                    if protocol == "MC":
                        self.assertEqual(requests, [], "MC connection sent a read or invented handshake")
                    elif protocol == "S7":
                        self.assertEqual(len(requests), 2)
                        self.assertEqual(requests[0][5], 0xE0)
                        self.assertEqual(requests[1][17], 0xF0)
                    else:
                        self.assertEqual(len(requests), 1)
                        self.assertEqual(struct.unpack_from(">I", requests[0], 8)[0], 0)
                    client, stream = page.client, page.client._stream
                    press(page, "读取"); self.idle(page)
                    self.assertIn("202", page.output.toPlainText(), page.client_status.text())
                    page.values.setText("404 505 606")
                    press(page, "写入"); self.idle(page)
                    self.assertEqual(server.memory.read(address, 3), [404, 505, 606])
                    self.assertIs(page.client, client)
                    self.assertIs(page.client._stream, stream, "Read/write replaced a successfully connected socket")
                    self.assertTrue(client.connected)
                    press(page, "断开")
                    self.assertIsNone(page.client)
                    self.assertFalse(client.connected)
                    wait(lambda: server.client_count == 0)

    def test_plc_explicit_connection_refusal_handshake_errors_and_retry(self):
        window = self.window()
        page = window.pages["plc"]
        window.navigate("plc")
        for protocol, failure in (("MC", "refused"), ("S7", "silent"), ("FINS", "silent"), ("S7", "wrong-cotp"), ("S7", "wrong-pdu"), ("FINS", "wrong")):
            with self.subTest(protocol=protocol, failure=failure):
                page.protocol.setCurrentText(protocol)
                page.host.setText("127.0.0.1"); page.timeout.setValue(150)
                with socket.socket() as reserved:
                    reserved.bind(("127.0.0.1", 0))
                    refused_port = reserved.getsockname()[1]
                    peer = None
                    try:
                        if failure != "refused":
                            peer = HandshakePeer(protocol, wrong=failure if failure.startswith("wrong") else False).__enter__()
                        port = peer.port if peer else refused_port
                        page.port.setValue(port)
                        press(page, "连接 PLC"); self.idle(page)
                        self.assertIsNone(page.client, "Failed handshake left an attached PLC client")
                        self.assertIn("连接失败", page.client_status.text())
                        self.assertIn("127.0.0.1", page.client_status.text())
                        self.assertIn(str(port), page.client_status.text())
                        self.assertEqual(page.client_status.property("statusTone"), "error")
                        self.assertTrue(page.prepare_button.isEnabled())
                        if peer:
                            self.assertTrue(peer.accepted.is_set())
                            self.assertTrue(peer.received.is_set(), "Test endpoint did not receive the real protocol handshake")
                            wait(peer.closed.is_set, message="Failed PLC handshake left the TCP socket open")
                    finally:
                        if peer:
                            peer.__exit__(None, None, None)
                with PlcServer(protocol, port=0) as good:
                    page.port.setValue(good.bound_port); page.timeout.setValue(1000)
                    press(page, "连接 PLC"); self.idle(page)
                    self.assertIsNotNone(page.client, page.client_status.text())
                    self.assertTrue(page.client.connected)
                    self.assertEqual(page.client_status.property("statusTone"), "success")
                    press(page, "断开")
                    wait(lambda: good.client_count == 0)

    def test_plc_parameter_change_cancellation_and_close_during_connect(self):
        window = self.window()
        page = window.pages["plc"]
        window.navigate("plc")
        with PlcServer("S7", port=0) as server:
            page.host.setText("127.0.0.1"); page.port.setValue(server.bound_port)
            press(page, "连接 PLC"); self.idle(page)
            old = page.client
            self.assertIsNotNone(old, page.client_status.text())
            page.slot.setValue(2); APP.processEvents()
            self.assertIsNone(page.client, "Connection parameters changed without releasing the old client")
            self.assertFalse(old.connected)
            wait(lambda: server.client_count == 0)
            self.assertNotEqual(page.client_status.property("statusTone"), "success")
            page.slot.setValue(1)

            for action in ("cancel", "close"):
                with self.subTest(action=action):
                    entered, release = threading.Event(), threading.Event()
                    connected = []
                    original = PlcClient.connect
                    def delayed_completion(client):
                        result = original(client)  # A real TCP + S7 handshake.
                        connected.append(client)
                        entered.set()
                        if not release.wait(2):
                            raise TimeoutError("Test connection-completion gate timed out")
                        return result
                    with patch.object(PlcClient, "connect", delayed_completion):
                        press(page, "连接 PLC")
                        wait(entered.is_set)
                        self.assertTrue(page.busy)
                        if action == "cancel":
                            self.assertTrue(page.disconnect_button.isEnabled(), "A pending connection cannot be cancelled")
                            press(page, "断开")
                            self.assertTrue(page.busy, "Cancelling marked the page idle before the operation actually returned")
                            self.assertIsNone(page.client)
                        else:
                            window.close(); APP.processEvents()
                            self.assertTrue(window._closing)
                            self.assertFalse(window._closed)
                        release.set()
                        if action == "cancel":
                            self.idle(page)
                            self.assertIsNone(page.client, "A cancelled startup result became the current PLC connection")
                            self.assertNotEqual(page.client_status.property("statusTone"), "success")
                        else:
                            wait(lambda: window._closed)
                    self.assertEqual(len(connected), 1)
                    self.assertFalse(connected[0].connected)
                    wait(lambda: server.client_count == 0)

    def test_network_tcp_udp_workbench_and_monitor(self):
        receiver, sender = self.window(), self.window()
        a, b = receiver.pages["network"], sender.pages["network"]
        for window in (receiver, sender):
            window.navigate("network")
        a.mode.setCurrentText("TCP Server"); a.local_host.setText("127.0.0.1"); a.local_port.setValue(0)
        a.auto_reply.setCurrentIndex(1)
        press(a, "启动连接"); self.idle(a)
        self.assertIsNotNone(a.session, receiver.statusBar().currentMessage())
        b.mode.setCurrentText("TCP Client"); b.host.setText("127.0.0.1"); b.port.setValue(a.session.bound_port)
        b.local_host.setText("127.0.0.1"); b.local_port.setValue(0)
        press(b, "启动连接"); self.idle(b)
        self.assertIsNotNone(b.session, sender.statusBar().currentMessage())
        wait(lambda: bool(a.session.peers))
        workbench = sender.pages["workbench"]
        sender.navigate("workbench")
        workbench.mode.setCurrentText("Modbus TCP")
        workbench.count.setValue(3)
        press(workbench, "生成报文")
        data = bytes.fromhex(workbench.frame.toPlainText())
        self.assertEqual(len(data), 12)
        self.assertIn(b.source, sender.hub.connections)
        workbench.target.setCurrentText(b.source)
        press(workbench, "发送报文"); self.idle(workbench)
        wait(lambda: any(event["direction"] == "RX" and event["data"] == data for event in sender.hub.events))
        self.assertTrue(any(event["data"] == data for event in receiver.hub.events))
        monitor = sender.pages["monitor"]
        sender.navigate("monitor"); monitor.refresh()
        self.assertGreater(monitor.grid.rowCount(), 0)
        press(a, "停止"); press(b, "停止")
        for page in (a, b):
            page.output.clear(); page.mode.setCurrentText("UDP"); page.local_port.setValue(0)
        a.host.setText("127.0.0.1"); a.port.setValue(9)
        press(a, "启动连接"); self.idle(a)
        b.port.setValue(a.session.bound_port)
        press(b, "启动连接"); self.idle(b)
        sender.navigate("workbench")
        workbench.mode.setCurrentText("自定义 HEX"); workbench.custom.setText("AA BB 00 FF"); workbench.checksum.setCurrentText("None")
        press(workbench, "生成报文"); press(workbench, "发送报文"); self.idle(workbench)
        wait(lambda: "AA BB 00 FF" in b.output.toPlainText(), message=f"UDP auto-reply did not return: {sender.statusBar().currentMessage()}")
        self.assertIn("AA BB 00 FF", a.output.toPlainText())

    def test_close_during_startup_waits_for_real_session_then_releases(self):
        for key, kind, action in (("simulator", ModbusServer, "启动模拟"), ("network", NetworkTransport, "启动连接"), ("plc", PlcServer, "启动服务器")):
            with self.subTest(module=key):
                window = self.window()
                page = window.pages[key]
                window.navigate(key)
                if key == "network":
                    page.mode.setCurrentText("TCP Server"); page.local_host.setText("127.0.0.1"); page.local_port.setValue(0)
                elif key == "plc":
                    page.role.setCurrentIndex(1)
                    page.listen_host.setText("127.0.0.1"); page.listen_port.setValue(0)
                else:
                    page.host.setText("127.0.0.1"); page.port.setValue(0)
                entered, release = threading.Event(), threading.Event()
                sessions, ports = [], []
                original_start = kind.start
                def delayed_start(session):
                    entered.set()
                    if not release.wait(2):
                        raise TimeoutError("Test startup gate timed out")
                    result = original_start(session)
                    sessions.append(result); ports.append(result.bound_port)
                    return result
                with patch.object(kind, "start", delayed_start):
                    press(page, action)
                    wait(entered.is_set)
                    self.assertTrue(page.busy)
                    window.close(); APP.processEvents()
                    self.assertTrue(window._closing)
                    self.assertFalse(window._closed)
                    release.set()
                    wait(lambda: window._closed, message=f"Closing did not finish for {key}")
                self.assertEqual(len(sessions), 1)
                self.assertEqual(sessions[0].bound_port, 0, "Startup result leaked a listening socket")
                released(ports[0])
                self.assertEqual(window.hub.connections, {})
                window.close(); APP.processEvents()
                self.assertTrue(window._closed)


if __name__ == "__main__":
    unittest.main()
