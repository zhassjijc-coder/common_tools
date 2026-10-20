from dataclasses import replace
import ipaddress
import json
import socket
import ssl
import struct
import threading
import time
import unittest
from unittest.mock import patch

import paho.mqtt.client as paho

from linklab.protocols.zlan import (DeviceSettings, ZlanClient, OperationCancelled, export_profile,
    import_profile, matches, parse_response, query_packet, redact_packet, write_packet)
from linklab.services.mqtt import MqttService, MqttSettings, configured_client, payload_bytes, validate_topic


def frame():
    value = bytearray(query_packet()); value[2] = 1
    p = memoryview(value)[3:]
    p[:4] = socket.inet_aton("127.0.0.1"); p[4:8] = socket.inet_aton("255.0.0.0")
    p[12:16] = socket.inet_aton("127.0.0.1"); struct.pack_into("!HH", p, 16, 4196, 4196)
    p[21:30] = b"smoke-key"; p[31:37] = bytes((2, 0x4C, 0x41, 0x42, 0, 1)); p[37] = 4
    p[38:45] = b"LL-DEMO"; p[48] = 1; p[49] = 3; p[66:75] = b"127.0.0.1"
    p[96], p[97], p[103], p[104] = 12, 60, 117, 0x22
    p[105:109] = socket.inet_aton("230.90.76.1"); p[112] = 2; p[114] = 0xA0
    p[115:121] = bytes((255, 3, 65, 66, 67, 0))
    return bytes(value)


class UdpDevice:
    def __init__(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); self.sock.bind(("127.0.0.1", 0)); self.sock.settimeout(0.1)
        self.port = self.sock.getsockname()[1]; self.stop = threading.Event(); self.raw = frame()
        self.queries = self.writes = 0; self.ignore_write = False; self.commands = []
        self.thread = threading.Thread(target=self.run, daemon=True); self.thread.start()

    def run(self):
        while not self.stop.is_set():
            try:
                data, peer = self.sock.recvfrom(4096)
            except socket.timeout:
                continue
            except OSError:
                return
            if len(data) != 170 or data[:2] != b"ZL":
                continue
            self.commands.append(data[2])
            if data[2] in (0, 4):
                self.queries += 1
                self.sock.sendto(b"noise", peer); self.sock.sendto(self.raw, peer); self.sock.sendto(self.raw, peer)
            elif data[2] == 2:
                self.writes += 1
                if not self.ignore_write and data[34:40] == self.raw[34:40]:
                    self.raw = data[:2] + b"\x01" + data[3:]

    def close(self):
        self.stop.set(); self.sock.close(); self.thread.join(1)


class ZlanTests(unittest.TestCase):
    def test_golden_offsets_and_unedited_fields(self):
        self.assertEqual(query_packet(True), b"ZL\x00" + bytes(167))
        snapshot = parse_response(frame(), ("127.0.0.1", 1092))
        self.assertEqual(snapshot.identity, "02:4C:41:42:00:01")
        self.assertEqual(snapshot.settings.parity, 1)  # Official mapping: None, Even, Odd.
        settings = replace(snapshot.settings, name="NEW", baud=115200, parity=2, data_bits=7, stop_bits=2, local_port=502)
        write = write_packet(snapshot, settings)
        actual = parse_response(b"ZL\x01" + write[3:], snapshot.peer)
        self.assertTrue(matches(actual, snapshot, settings)); self.assertEqual(write[19:21], b"\x01\xf6")
        self.assertEqual(write[24:40], frame()[24:40]); self.assertEqual(write[118:], frame()[118:])
        self.assertEqual(write[117] & 0xFE, 0xA0)
        self.assertEqual(snapshot.parameters, frame()[3:])

    def test_capabilities_channels_password_and_templates(self):
        original = parse_response(frame(), ("127.0.0.1", 1092))
        with self.assertRaises(ValueError):
            write_packet(original, replace(original.settings, app_protocol=1))
        capable = bytearray(frame()); capable[107] |= 12
        capable = parse_response(bytes(capable), original.peer)
        self.assertEqual(write_packet(capable, replace(capable.settings, app_protocol=2))[63], 2)
        channel = bytearray(frame()); channel[103] = 1
        self.assertNotEqual(parse_response(bytes(channel), original.peer).identity, original.identity)
        protected = bytearray(frame()); protected[113] |= 4; protected = parse_response(bytes(protected), original.peer)
        with self.assertRaises(ValueError):
            write_packet(protected, protected.settings)
        secret = write_packet(protected, protected.settings, "test-pass")
        self.assertEqual(redact_packet(secret)[24:34], bytes(10)); self.assertEqual(redact_packet(secret)[118:], bytes(52))
        text = export_profile(original.settings)
        self.assertEqual(import_profile(text), original.settings)
        self.assertNotIn("parameters", text); self.assertNotIn("password", text); self.assertNotIn("identity", text)
        readonly = parse_response(frame() + b"\0", original.peer)
        self.assertFalse(readonly.writable)
        with self.assertRaises(ValueError):
            write_packet(readonly, readonly.settings)
        multicast = bytearray(frame()); multicast[23] = 3; multicast = parse_response(bytes(multicast), original.peer)
        multicast_write = write_packet(multicast, replace(multicast.settings, baud=19200))
        self.assertEqual(multicast_write[15:19], frame()[15:19]); self.assertEqual(multicast_write[69:99], frame()[69:99])

    def test_strict_addresses_and_dhcp_readback(self):
        settings = parse_response(frame(), ("127.0.0.1", 1092)).settings
        for changes in ({"address": "1.2"}, {"address": "010.0.0.1"}, {"mask": "255.0.255.0"},
                        {"address": "192.168.1.0", "mask": "255.255.255.0"}, {"address": "192.168.1.255", "mask": "255.255.255.0"},
                        {"destination": "1.2"}, {"destination": "a" * 30}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                replace(settings, **changes).validate()
        original = parse_response(frame(), ("127.0.0.1", 1092)); requested = replace(settings, dhcp=True)
        leased = replace(original, settings=replace(requested, address="10.8.9.10", gateway="10.8.0.1", mask="255.255.0.0"))
        self.assertTrue(matches(leased, original, requested))

    def test_real_udp_discover_read_write_timeout_and_race(self):
        device = UdpDevice(); self.addCleanup(device.close)
        traffic = []; client = ZlanClient(device.port, lambda *value: traffic.append(value))
        self.assertEqual(device.queries, 0)
        discovered = client.discover("127.0.0.1", "127.0.0.1", broadcast=True, timeout=0.1)
        self.assertEqual(len(discovered), 1); self.assertEqual(device.commands[0], 0)
        selected = discovered[0]; selected = client.read(selected, bind="127.0.0.1", timeout=0.1)
        self.assertEqual(device.commands[-1], 4)
        requested = replace(selected.settings, name="UPDATED", local_port=502)
        confirmed = client.apply(selected, requested, bind="127.0.0.1", timeout=0.1, verify_timeout=1)
        self.assertEqual(confirmed.settings, requested); self.assertEqual(device.writes, 1)
        altered = bytearray(device.raw); altered[41:51] = b"EXTERNAL\0\0"; device.raw = bytes(altered)
        with self.assertRaises(ValueError):
            client.apply(confirmed, replace(requested, name="RACE"), bind="127.0.0.1", timeout=0.1)
        self.assertEqual(device.writes, 1)
        latest = client.read(confirmed, bind="127.0.0.1", timeout=0.1); device.ignore_write = True
        with self.assertRaises(TimeoutError):
            client.apply(latest, replace(latest.settings, name="NO-ACK"), bind="127.0.0.1", timeout=0.1, verify_timeout=0.5)
        self.assertEqual(device.writes, 2)  # No automatic resend of a write.
        self.assertTrue(all(data[24:34] == bytes(10) for _, data, _ in traffic))
        cancel = threading.Event(); cancel.set(); count = device.queries
        with self.assertRaises(OperationCancelled):
            client.discover("127.0.0.1", "127.0.0.1", broadcast=False, timeout=0.1, cancel=cancel)
        self.assertEqual(device.queries, count)


def _varint(value):
    data = bytearray()
    while True:
        digit = value % 128; value //= 128; data.append(digit | (128 if value else 0))
        if not value:
            return bytes(data)


def _read_varint(data, index):
    value = 0; multiplier = 1
    for _ in range(4):
        digit = data[index]; index += 1; value += (digit & 127) * multiplier
        if not digit & 128:
            return value, index
        multiplier *= 128
    raise ValueError("Malformed remaining length")


def _read_exact(sock, length, stop):
    data = bytearray()
    while len(data) < length and not stop.is_set():
        try:
            part = sock.recv(length - len(data))
        except socket.timeout:
            continue
        if not part:
            return None
        data.extend(part)
    return bytes(data) if len(data) == length else None


class Broker:
    """Small loopback-only MQTT peer, independent of the Paho client encoder."""
    def __init__(self):
        self.socket = socket.socket(); self.socket.bind(("127.0.0.1", 0)); self.socket.listen(); self.socket.settimeout(0.1)
        self.port = self.socket.getsockname()[1]; self.stop = threading.Event(); self.connections = []; self.workers = []
        self.connects = []; self.published = []; self.retained = {}
        self.thread = threading.Thread(target=self.accept, daemon=True); self.thread.start()

    def accept(self):
        while not self.stop.is_set():
            try:
                client, _ = self.socket.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            client.settimeout(0.1); self.connections.append(client)
            worker = threading.Thread(target=self.run, args=(client,), daemon=True); self.workers.append(worker); worker.start()

    def run(self, client):
        protocol = 4; subscriptions = {}; pending_qos2 = {}
        def send(header, body):
            client.sendall(bytes((header,)) + _varint(len(body)) + body)
        def deliver(topic, payload, retained=False):
            if any(paho.topic_matches_sub(pattern, topic) for pattern in subscriptions):
                encoded = topic.encode(); send(0x31 if retained else 0x30, struct.pack("!H", len(encoded)) + encoded + (b"\0" if protocol == 5 else b"") + payload)
        def published(topic, payload, retain):
            self.published.append((topic, payload, retain))
            if retain:
                if payload:
                    self.retained[topic] = payload
                else:
                    self.retained.pop(topic, None)
            deliver(topic, payload)
        try:
            while not self.stop.is_set():
                first = _read_exact(client, 1, self.stop)
                if first is None:
                    return
                encoded = bytearray()
                for _ in range(4):
                    digit = _read_exact(client, 1, self.stop)
                    if digit is None:
                        return
                    encoded.extend(digit)
                    if not digit[0] & 128:
                        break
                length, _ = _read_varint(encoded, 0); body = _read_exact(client, length, self.stop)
                if body is None:
                    return
                command = first[0] >> 4
                if command == 1:
                    protocol = body[6]; self.connects.append(body)
                    send(0x20, b"\0\0\0" if protocol == 5 else b"\0\0")
                elif command in (8, 10):
                    mid = body[:2]; index = 2
                    if protocol == 5:
                        size, index = _read_varint(body, index); index += size
                    size = struct.unpack_from("!H", body, index)[0]; index += 2; topic = body[index:index + size].decode(); index += size
                    if command == 8:
                        qos = body[index] & 3; subscriptions[topic] = qos
                        send(0x90, mid + (b"\0" if protocol == 5 else b"") + bytes((qos,)))
                        for retained_topic, payload in list(self.retained.items()):
                            deliver(retained_topic, payload, True)
                    else:
                        subscriptions.pop(topic, None); send(0xB0, mid + (b"\0\0" if protocol == 5 else b""))
                elif command == 3:
                    size = struct.unpack_from("!H", body)[0]; topic = body[2:2 + size].decode(); index = size + 2; qos = (first[0] >> 1) & 3
                    mid = body[index:index + 2] if qos else b""; index += 2 if qos else 0
                    if protocol == 5:
                        prop_size, index = _read_varint(body, index); index += prop_size
                    payload = body[index:]; retained = bool(first[0] & 1)
                    if qos == 2:
                        pending_qos2[mid] = (topic, payload, retained); send(0x50, mid)
                    else:
                        if qos:
                            send(0x40, mid)
                        published(topic, payload, retained)
                elif command == 6:
                    mid = body[:2]; send(0x70, mid)
                    if mid in pending_qos2:
                        published(*pending_qos2.pop(mid))
                elif command == 12:
                    send(0xD0, b"")
                elif command == 14:
                    return
        except (OSError, ValueError, IndexError):
            return
        finally:
            client.close()

    def drop_connections(self):
        for client in list(self.connections):
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    def close(self):
        self.stop.set(); self.socket.close(); self.drop_connections(); self.thread.join(1)
        for worker in self.workers:
            worker.join(1)


def wait_for(predicate, timeout=4):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if predicate():
            return
        time.sleep(0.01)
    raise AssertionError("Timed out waiting for local test peer")


class MqttTests(unittest.TestCase):
    def test_validation_template_secrets_and_transport_configuration(self):
        settings = MqttSettings(password="hidden", key_password="also-hidden")
        self.assertNotIn("password", settings.safe_dict()); self.assertNotIn("key_password", settings.safe_dict())
        self.assertEqual(MqttSettings.from_dict({**settings.safe_dict(), "password": "must-ignore"}).password, "")
        self.assertEqual(payload_bytes("01 03 FF", "HEX"), b"\x01\x03\xff")
        with self.assertRaises(ValueError):
            validate_topic("a/#/b", subscription=True)
        with self.assertRaises(ValueError):
            validate_topic("a/+", subscription=False)
        class FakeClient:
            def __init__(self, **kwargs): self.options = kwargs; self.calls = {}
            def __getattr__(self, name):
                return lambda *args, **kwargs: self.calls.update({name: (args, kwargs)})
        client = configured_client(MqttSettings(host="wss://localhost:9443/mqtt", transport="WSS"), factory=FakeClient)
        self.assertEqual(client.options["transport"], "websockets")
        self.assertEqual(client.calls["ws_set_options"][1]["path"], "/mqtt")
        context = client.calls["tls_set_context"][0][0]
        self.assertTrue(context.check_hostname); self.assertEqual(context.verify_mode, ssl.CERT_REQUIRED)
        self.assertGreaterEqual(context.minimum_version, ssl.TLSVersion.TLSv1_2)

    def test_real_mqtt_311_and_5_qos_retained_subscription_acks(self):
        for protocol in ("3.1.1", "5.0"):
            with self.subTest(protocol=protocol):
                broker = Broker(); self.addCleanup(broker.close)
                messages = []; acknowledgements = []; statuses = []
                service = MqttService(on_message=messages.append, on_ack=lambda *value: acknowledgements.append(value), on_state=lambda *value: statuses.append(value))
                self.addCleanup(service.close)
                self.assertFalse(service.active); self.assertEqual(broker.connects, [])
                service.connect(MqttSettings(port=broker.port, protocol=protocol, auto_reconnect=False,
                    will_topic="linklab/status", will_payload="offline", session_expiry=120))
                wait_for(lambda: service.connected)
                service.subscribe("linklab/#", 2); wait_for(lambda: "linklab/#" in service.subscriptions)
                for qos in (0, 1, 2):
                    payload = f"hello-{qos}".encode(); mid = service.publish("linklab/test", payload, qos, qos == 1)
                    wait_for(lambda: any(message.direction == "RX" and message.payload == payload for message in messages))
                    wait_for(lambda: any(ack[0] == mid for ack in acknowledgements))
                self.assertEqual(len(broker.published), 3)
                self.assertTrue(any("QoS 0" in ack[1] for ack in acknowledgements))
                self.assertTrue(any("Broker" in ack[1] for ack in acknowledgements))
                service.unsubscribe("linklab/#"); wait_for(lambda: not service.subscriptions)
                service.subscribe("linklab/#", 1); wait_for(lambda: any(message.retain and message.payload == b"hello-1" for message in messages))
                self.assertTrue(broker.connects[0][7] & 4)  # Will flag was encoded.
                if protocol == "5.0":
                    self.assertIn(b"\x11\x00\x00\x00\x78", broker.connects[0])  # Session Expiry property.
                service.disconnect(wait=1); wait_for(lambda: not service.active)

    def test_disconnect_reconnect_restores_confirmed_subscriptions(self):
        broker = Broker(); self.addCleanup(broker.close)
        messages = []; service = MqttService(on_message=messages.append); self.addCleanup(service.close)
        service.connect(MqttSettings(port=broker.port, auto_reconnect=True)); wait_for(lambda: service.connected)
        service.subscribe("restore/#", 1); wait_for(lambda: "restore/#" in service.subscriptions)
        broker.drop_connections(); wait_for(lambda: len(broker.connects) >= 2 and service.connected, timeout=5)
        time.sleep(0.1); service.publish("restore/test", b"reconnected", 1)
        wait_for(lambda: any(message.direction == "RX" and message.payload == b"reconnected" for message in messages))


if __name__ == "__main__":
    unittest.main()
