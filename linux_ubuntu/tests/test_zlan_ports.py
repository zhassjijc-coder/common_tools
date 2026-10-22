"""Regress TCP Client ephemeral local ports without contacting physical devices."""
from dataclasses import replace
import socket
import struct
import unittest
from unittest.mock import Mock

from linklab.protocols.zlan import (export_profile, import_profile, parse_response, write_packet, ZlanClient)
from linklab.services.hub import Hub
from linklab.ui.netconfig_page import NetConfigPage
from test_capture import APP
from test_zlan_mqtt import frame, UdpDevice


def client_frame():
    raw = bytearray(frame())
    raw[3:7] = socket.inet_aton('192.168.31.130')
    raw[7:11] = socket.inet_aton('255.255.255.0')
    raw[11:15] = socket.inet_aton('192.168.31.1')
    raw[15:19] = socket.inet_aton('192.168.31.34')
    struct.pack_into('!HH', raw, 19, 0, 8201)
    raw[23] = 1; raw[59] = 1; raw[40] = 11
    raw[41:51] = b'PORT-ZERO\0'
    raw[69:99] = b'192.168.31.34'.ljust(30, b'\0')
    return bytes(raw)


class ZeroPortTests(unittest.TestCase):
    def test_client_zero_roundtrip_and_profile(self):
        device = parse_response(client_frame(), ('192.168.31.130', 1092))
        device.settings.validate()
        packet = write_packet(device, device.settings)
        self.assertEqual(packet[19:21], b'\0\0')
        self.assertEqual(packet[3:], device.parameters)
        self.assertEqual(import_profile(export_profile(device.settings)), device.settings)
        response = b'ZL\x01' + packet[3:]
        self.assertEqual(parse_response(response, device.peer).settings, device.settings)

    def test_local_port_limits_are_mode_aware_and_destination_stays_nonzero(self):
        settings = parse_response(client_frame(), ('192.168.31.130', 1092)).settings
        for mode in range(4):
            for port in (1, 65535):
                replace(settings, mode=mode, local_port=port).validate()
            for port in (-1, 65536, True, 1.5):
                with self.subTest(mode=mode, local=port), self.assertRaises(ValueError):
                    replace(settings, mode=mode, local_port=port).validate()
            if mode != 1:
                with self.subTest(mode=mode), self.assertRaisesRegex(ValueError, '本地端口'):
                    replace(settings, mode=mode).validate()
            for port in (0, -1, 65536, True):
                with self.subTest(mode=mode, destination=port), self.assertRaisesRegex(ValueError, '目标端口'):
                    replace(settings, mode=mode, local_port=1, destination_port=port).validate()

    def test_review_accepts_reported_configuration_without_sending(self):
        page = NetConfigPage(Hub()); self.addCleanup(page.close_resources)
        page.client = Mock()
        device = parse_response(client_frame(), ('192.168.31.130', 1092))
        page.devices.append(device); page._render(device.identity)
        self.assertEqual(page.local_port.value(), 0)
        page.review()
        self.assertIsNotNone(page._reviewed); self.assertTrue(page.apply_button.isEnabled())
        self.assertIn('本地端口 0（随机）', page.preview.toPlainText())
        self.assertEqual(page.settings(), device.settings)
        page.client.assert_not_called(); self.assertEqual(page.client.mock_calls, [])
        page.mode.setCurrentIndex(0); page.review()
        self.assertIsNone(page._reviewed); self.assertFalse(page.apply_button.isEnabled())
        self.assertIn('本地端口', page.status.text())
        page.mode.setCurrentIndex(1); page.review()
        self.assertIsNotNone(page._reviewed)

    def test_udp_mock_apply_preserves_ephemeral_port(self):
        server = UdpDevice(); self.addCleanup(server.close)
        client = ZlanClient(server.port)
        original = client.discover('127.0.0.1', '127.0.0.1', broadcast=False, timeout=.1)[0]
        requested = replace(original.settings, mode=1, local_port=0, destination_port=8201)
        result = client.apply(original, requested, bind='127.0.0.1', timeout=.1, verify_timeout=1)
        self.assertEqual(result.settings, requested); self.assertEqual(server.writes, 1)
        self.assertEqual(server.raw[19:21], b'\0\0')
