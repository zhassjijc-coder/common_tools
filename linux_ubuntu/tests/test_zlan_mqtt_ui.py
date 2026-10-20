import os
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from dataclasses import replace
import threading
import time
import unittest
from unittest.mock import patch

from PySide6.QtWidgets import QApplication

from linklab.protocols.zlan import parse_response
from linklab.services.hub import Hub
from linklab.services.mqtt import MqttMessage
from linklab.ui.mqtt_page import MqttPage
from linklab.ui.netconfig_page import NetConfigPage
from test_zlan_mqtt import frame


_APP = QApplication.instance() or QApplication([])


def pump(predicate=lambda: True, timeout=2):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        _APP.processEvents()
        if predicate():
            return
        time.sleep(0.005)
    raise AssertionError("Timed out waiting for Qt worker result")


class PageTests(unittest.TestCase):
    def test_pages_construct_offline_and_templates_exclude_secrets(self):
        hub = Hub()
        with patch("linklab.services.mqtt.configured_client") as client, patch("linklab.protocols.zlan.socket.socket") as udp:
            mqtt = MqttPage(hub); net = NetConfigPage(hub)
            self.addCleanup(mqtt.close_resources); self.addCleanup(net.close_resources)
            client.assert_not_called(); udp.assert_not_called()
        self.assertFalse(mqtt.service.active); self.assertFalse(net._working)
        self.assertEqual(len(hub.events), 0)
        mqtt.password.setText("SECRET-PASSWORD"); mqtt.key_password.setText("SECRET-KEY-PASSWORD")
        template = mqtt.profile_dict()
        self.assertNotIn("password", template["connection"]); self.assertNotIn("key_password", template["connection"])
        mqtt.apply_profile(template)
        self.assertEqual(mqtt.password.text(), ""); self.assertEqual(mqtt.key_password.text(), "")
        self.assertFalse(mqtt.service.active)

    def test_network_identity_review_invalidation_and_readonly_format(self):
        page = NetConfigPage(Hub()); self.addCleanup(page.close_resources)
        device = parse_response(frame(), ("127.0.0.1", 1092))
        page.devices.append(device); page._render(device.identity)
        self.assertEqual(page.selected.identity, device.identity)
        page.local_port.setValue(502); page.review()
        self.assertIsNotNone(page._reviewed); self.assertIn(device.identity, page.preview.toPlainText())
        self.assertIn("当前 IP 127.0.0.1", page.preview.toPlainText())
        page.baud.setCurrentText("115200"); self.assertIsNone(page._reviewed)
        page.review(); self.assertIsNotNone(page._reviewed)
        page.address.setText("127.0.0.0"); page.review(); self.assertIsNone(page._reviewed)
        readonly = parse_response(frame() + b"\0", device.peer)
        page.devices[:] = [readonly]; page._render(readonly.identity)
        self.assertFalse(page.editor.isEnabled()); self.assertFalse(page.review_button.isEnabled())
        unknown = bytearray(frame()); unknown[40] = 99; unknown = parse_response(bytes(unknown), device.peer)
        page.devices[:] = [unknown]; page._render(unknown.identity)
        self.assertEqual(page.baud.currentIndex(), -1)
        page.review(); self.assertIsNone(page._reviewed)

    def test_network_read_is_background_and_close_cancels(self):
        page = NetConfigPage(Hub()); self.addCleanup(page.close_resources)
        started = threading.Event(); cancelled = threading.Event()
        class FakeClient:
            def discover(self, *args, **kwargs):
                started.set()
                while not kwargs["cancel"].wait(0.01):
                    pass
                cancelled.set(); return []
        page.client = FakeClient(); page.search()
        pump(started.is_set); self.assertTrue(page._working); self.assertFalse(page.search_button.isEnabled())
        before = time.monotonic(); page.close_resources()
        self.assertLess(time.monotonic() - before, 0.1)
        pump(cancelled.is_set); pump(lambda: not page.busy)
        page.close_resources()

    def test_mqtt_bounded_display_filter_details_and_acks(self):
        page = MqttPage(Hub()); self.addCleanup(page.close_resources)
        page._enqueue(MqttMessage(time.time(), "TX", "demo/json", b'{"value": 42}', 1, False, 13, 99, "已入队"))
        page._ack(99, "Broker 已确认"); page._drain()
        self.assertEqual(page.messages[0].status, "Broker 已确认")
        page.grid.selectRow(0); page.display_format.setCurrentText("JSON"); page.show_detail()
        self.assertIn('"value": 42', page.detail.toPlainText())
        page.direction.setCurrentText("RX"); self.assertTrue(page.grid.isRowHidden(0))
        page.direction.setCurrentText("全部"); page.filter.setText("no-match"); self.assertTrue(page.grid.isRowHidden(0))
        for index in range(600):
            page._enqueue(MqttMessage(time.time(), "RX", "bulk", bytes((index % 256,)), 0, False, 1))
        for _ in range(7):
            page._drain()
        self.assertEqual(len(page.messages), 500); self.assertEqual(page.grid.rowCount(), 500)
        page.close_resources(); page.close_resources()


if __name__ == "__main__":
    unittest.main()
