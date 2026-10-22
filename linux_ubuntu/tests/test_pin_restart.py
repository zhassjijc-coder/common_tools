from dataclasses import replace
import threading
import unittest
from unittest.mock import Mock, patch

from PySide6.QtCore import QPoint, QPointF, Qt, QEvent
from PySide6.QtGui import QKeyEvent, QWheelEvent
from PySide6.QtWidgets import QMessageBox, QPushButton, QScrollArea

from linklab.capture.editor import PinnedImageWindow
from linklab.protocols.zlan import OperationCancelled, ZlanClient, parse_response, restart_packet
from linklab.services.hub import Hub
from linklab.ui.netconfig_page import NetConfigPage
from test_capture import APP, sample
from test_zlan_mqtt import UdpDevice, frame
from test_zlan_mqtt_ui import pump


class PinRestartTests(unittest.TestCase):
    def test_pin_fixed_aspect_wheel_and_original_pixels(self):
        for width, height in ((200, 100), (80, 800), (3000, 100), (1200, 1600)):
            with self.subTest(size=(width, height)):
                source = sample(width, height); copied = []; recognized = []
                pin = PinnedImageWindow(source, copied.append, recognized.append)
                try:
                    pin.show(); APP.processEvents()
                    self.assertFalse(pin.findChildren(QScrollArea))
                    for scale in (.25, .5, 1., 2., 8.):
                        pin.set_scale(scale); APP.processEvents()
                        pixmap = pin.picture.pixmap()
                        # Raster dimensions may differ from the ideal aspect by one pixel.
                        short_side = pixmap.height() if width >= height else pixmap.width()
                        expected = pixmap.width() * height / width if width >= height else pixmap.height() * width / height
                        self.assertLessEqual(abs(short_side - expected), 1)
                        self.assertEqual(pin.minimumSize(), pin.maximumSize())
                        self.assertTrue(pin.rect().contains(pin.picture.geometry()))
                        self.assertLessEqual(pin.width(), pin.screen().availableGeometry().width() * .9 + 2)
                        self.assertLessEqual(pin.height(), pin.screen().availableGeometry().height() * .9 + 2)
                    previous = pin.scale
                    APP.sendEvent(pin, QKeyEvent(QEvent.Type.KeyPress, Qt.Key.Key_Minus, Qt.KeyboardModifier.NoModifier))
                    self.assertEqual(pin.scale, previous)
                    wheel = QWheelEvent(QPointF(10, 10), QPointF(10, 10), QPoint(), QPoint(0, -120), Qt.MouseButton.NoButton, Qt.KeyboardModifier.NoModifier, Qt.ScrollPhase.NoScrollPhase, False)
                    APP.sendEvent(pin.picture, wheel)
                    self.assertLess(pin.scale, previous)
                    self.assertIn('%', pin.zoom_label.text())
                    buttons = pin.findChildren(QPushButton)
                    self.assertEqual([b.text() for b in buttons], ['复制', '识别'])
                    for button in buttons: button.click()
                    self.assertEqual(copied, [source]); self.assertEqual(recognized, [source])
                    self.assertEqual(pin.image, source)
                finally: pin.close()

    def test_pin_fixed_aspect_with_application_theme(self):
        from linklab.ui.theme import stylesheet
        previous = APP.styleSheet()
        APP.setStyleSheet(stylesheet(False))
        try:
            self.test_pin_fixed_aspect_wheel_and_original_pixels()
        finally:
            APP.setStyleSheet(previous)

    def test_restart_packet_preserves_raw_parameters_and_auth(self):
        raw = frame(); device = parse_response(raw, ('127.0.0.1', 1092))
        packet = restart_packet(device)
        self.assertEqual(packet, b'ZL\x02' + raw[3:])
        unknown = bytearray(raw); unknown[40] = 99; unknown[59] = 128
        self.assertEqual(restart_packet(parse_response(bytes(unknown), device.peer))[3:], bytes(unknown)[3:])
        protected = bytearray(raw); protected[113] |= 4
        protected = parse_response(bytes(protected), device.peer)
        with self.assertRaises(ValueError): restart_packet(protected)
        packet = restart_packet(protected, 'test-pass')
        self.assertEqual(packet[24:34], b'test-pass\0')
        self.assertEqual(packet[34:], protected.parameters[31:])
        for bad in (replace(device, identity='other'), parse_response(raw + b'\0', device.peer)):
            with self.assertRaises(ValueError): restart_packet(bad)

    def test_restart_loopback_fresh_parameters_identity_cancel_and_timeout(self):
        server = UdpDevice(); self.addCleanup(server.close)
        client = ZlanClient(server.port)
        selected = client.discover('127.0.0.1', '127.0.0.1', broadcast=False, timeout=.1)[0]
        fresh = bytearray(server.raw); fresh[41:51] = b'FRESH'.ljust(10, b'\0'); server.raw = bytes(fresh)
        result = client.restart(selected, bind='127.0.0.1', timeout=.1, verify_timeout=1)
        self.assertEqual(result.settings.name, 'FRESH'); self.assertEqual(server.writes, 1)
        self.assertEqual(server.raw, bytes(fresh))
        with self.assertRaises(TimeoutError): client.restart(replace(selected, identity='wrong-device'), bind='127.0.0.1', timeout=.1)
        self.assertEqual(server.writes, 1)
        cancel = threading.Event(); cancel.set()
        with self.assertRaises(OperationCancelled): client.restart(selected, bind='127.0.0.1', timeout=.1, cancel=cancel)
        self.assertEqual(server.writes, 1)
        discover = client.discover
        def silent_after_command(*args, **kwargs):
            return [] if server.writes > 1 else discover(*args, **kwargs)
        with patch.object(client, 'discover', side_effect=silent_after_command):
            with self.assertRaisesRegex(TimeoutError, '重启指令已发送'):
                client.restart(result, bind='127.0.0.1', timeout=.1, verify_timeout=.5)
        self.assertEqual(server.writes, 2)

    def test_restart_ui_selection_confirmation_busy_and_unsaved_fields(self):
        page = NetConfigPage(Hub()); self.addCleanup(page.close_resources)
        page.client = Mock()
        self.assertFalse(page.restart_button.isEnabled())
        device = parse_response(frame(), ('127.0.0.1', 1092))
        page.devices.append(device); page._render(device.identity)
        self.assertTrue(page.restart_button.isEnabled())
        page.address.setText('invalid-unsaved-value')
        with patch('linklab.ui.netconfig_page.QMessageBox.question', return_value=QMessageBox.StandardButton.No):
            page.restart_selected()
        page.client.restart.assert_not_called()
        started = threading.Event(); finish = threading.Event()
        def restart(expected, **kwargs):
            self.assertEqual(expected, device); started.set(); finish.wait(2); return device
        page.client.restart.side_effect = restart
        with patch('linklab.ui.netconfig_page.QMessageBox.question', return_value=QMessageBox.StandardButton.Yes) as question:
            page.restart_selected()
            self.assertIn(device.identity, question.call_args.args[2])
            self.assertIn('未写入', question.call_args.args[2])
        pump(started.is_set)
        self.assertFalse(page.restart_button.isEnabled()); self.assertTrue(page._working)
        finish.set(); pump(lambda: not page._working)
        self.assertEqual(page.selected, device); self.assertIn('重启指令已发送', page.status.text())
        page.client.apply.assert_not_called()
        readonly = parse_response(frame() + b'\0', device.peer)
        page.devices[:] = [readonly]; page._render(readonly.identity)
        self.assertFalse(page.restart_button.isEnabled())
