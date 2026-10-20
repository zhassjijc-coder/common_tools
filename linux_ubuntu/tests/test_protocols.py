"""Real loopback protocol checks; no physical devices or root privileges needed."""
import contextlib
import copy
import socket
import struct
import threading
import time
import unittest
from unittest.mock import patch

from linklab.protocols.codec import checksum, crc16, decode, encode, pack_values, unpack_values
from linklab.protocols.modbus import Memory, ModbusClient, ModbusServer, ModbusError, build_pdu, build_rtu, build_tcp, process_pdu
from linklab.protocols.plc import PlcClient, PlcServer, PlcMemory, parse_address, _Session, _frame
from linklab.protocols.transport import NetworkTransport, SerialTransport, read_exact


def eventually(predicate, timeout=2):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.01)
    raise AssertionError("Condition did not become true before deadline")


class WorkerCheckedTest(unittest.TestCase):
    def setUp(self):
        self.worker_errors = []
        self.old_exception_hook = threading.excepthook
        threading.excepthook = lambda args: self.worker_errors.append(args.exc_value)

    def tearDown(self):
        threading.excepthook = self.old_exception_hook
        self.assertEqual(self.worker_errors, [], "A background worker crashed")


@contextlib.contextmanager
def reply_server(responder):
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    listener.settimeout(2)
    errors = []
    def run():
        try:
            with listener.accept()[0] as stream:
                stream.settimeout(2)
                responder(stream)
        except (OSError, EOFError):
            pass
        except Exception as exc:
            errors.append(exc)
    worker = threading.Thread(target=run, daemon=True)
    worker.start()
    try:
        yield listener.getsockname()[1]
    finally:
        listener.close()
        worker.join(3)
        if errors:
            raise errors[0]


class CodecTests(WorkerCheckedTest):
    def test_checksums_and_encodings(self):
        self.assertEqual(crc16(b"123456789"), 0x4B37)
        self.assertEqual(checksum(b"123456789", "CRC32"), bytes.fromhex("26 39 F4 CB"))
        self.assertEqual(checksum(bytes((1, 2, 3)), "SUM8"), b"\x06")
        self.assertEqual(checksum(bytes((1, 2, 3)), "XOR"), b"\x00")
        self.assertEqual(checksum(bytes((1, 2, 3)), "LRC"), b"\xfa")
        self.assertEqual(encode("0x01 0xAF,FF", "HEX"), b"\x01\xaf\xff")
        self.assertEqual(decode(encode("温度23", "UTF-8"), "UTF-8"), "温度23")
        self.assertEqual(decode(b"\xff", "ASCII"), "\ufffd")
        for text in ("0", "GG", "01HELLO", "01\x00"):
            with self.assertRaises(ValueError):
                encode(text)

    def test_types_orders_boundaries(self):
        for order, expected in (("ABCD", "12345678"), ("BADC", "34127856"), ("CDAB", "56781234"), ("DCBA", "78563412")):
            self.assertEqual(pack_values([0x12345678], "UInt32", order).hex(), expected)
            self.assertEqual(unpack_values(bytes.fromhex(expected), "UInt32", order), [0x12345678])
        for name, values in (("Int16", [-32768, 32767]), ("UInt16", [0, 65535]), ("Int32", [-2147483648, 2147483647]), ("Float32", [-12.5, 100.25]), ("Bool", [True, False])):
            for order in ("ABCD", "BADC", "CDAB", "DCBA"):
                self.assertEqual(unpack_values(pack_values(values, name, order), name, order), values)
        with self.assertRaises(ValueError):
            pack_values([65536], "UInt16")
        with self.assertRaises(ValueError):
            pack_values(["sometimes"], "Bool")


class ModbusTests(WorkerCheckedTest):
    def test_wire_vectors_and_validation(self):
        self.assertEqual(build_rtu(1, build_pdu(3, 0, 10)).hex(), "01030000000ac5cd")
        self.assertEqual(build_tcp(0x1234, 1, build_pdu(3, 0, 1)).hex(), "123400000006010300000001")
        for function, count in ((1, 2001), (3, 126)):
            with self.assertRaises(ValueError):
                build_pdu(function, 0, count)
        with self.assertRaises(ValueError):
            build_pdu(3, 65535, 2)
        with self.assertRaises(ValueError):
            build_pdu(5, 0, values=[2])
        self.assertEqual(process_pdu(Memory(10), build_pdu(3, 9, 2)), b"\x83\x02")
        self.assertEqual(process_pdu(Memory(), b"\x10\x00\x00\x00\x02\x02\x00\x01"), b"\x90\x03")
        self.assertEqual(process_pdu(Memory(), b"\x42"), b"\xc2\x01")

    def exercise(self, client, memory):
        memory.write("discrete", 4, [1, 0, 1])
        memory.write("input", 20, [65535, 12])
        self.assertEqual(client.read(2, 4, 3), [1, 0, 1])
        self.assertEqual(client.read(4, 20, 2), [65535, 12])
        client.write(5, 9, [1])
        self.assertEqual(client.read(1, 9, 1), [1])
        bits = [n % 3 == 0 for n in range(19)]
        client.write(15, 30, bits)
        self.assertEqual(client.read(1, 30, 19), bits)
        client.write(6, 0, [0xABCD])
        self.assertEqual(client.read(3, 0, 1), [0xABCD])
        client.write(16, 1, [12, 345, 65535])
        self.assertEqual(client.read(3, 0, 4), [0xABCD, 12, 345, 65535])

    def test_tcp_all_functions_and_shared_multi_client_memory(self):
        traffic = []
        with ModbusServer(port=0, on_traffic=lambda *args: traffic.append(args)) as server:
            with ModbusClient(port=server.bound_port) as client:
                self.exercise(client, server.memory)
                with ModbusClient(port=server.bound_port) as second:
                    self.assertEqual(second.read(3, 1, 3), [12, 345, 65535])
                client.unit = 5
                with self.assertRaises(ModbusError) as error:
                    client.read(3, 0)
                self.assertEqual(error.exception.code, 11)
        self.assertTrue(any(row[0] == "TX" for row in traffic))
        self.assertTrue(any(row[0] == "RX" for row in traffic))

    def test_partial_frames_transaction_refusal_and_timeout(self):
        def fragmented(stream):
            request = read_exact(stream, 12, time.monotonic() + 2)
            frame = build_tcp(struct.unpack(">H", request[:2])[0], 1, b"\x03\x02\x12\x34")
            for byte in frame:
                stream.sendall(bytes((byte,)))
                time.sleep(.001)
        with reply_server(fragmented) as port:
            with ModbusClient(port=port) as client:
                self.assertEqual(client.read(3, 0), [0x1234])
        def bad_transaction(stream):
            read_exact(stream, 12, time.monotonic() + 2)
            stream.sendall(build_tcp(999, 1, b"\x03\x02\x00\x01"))
        with reply_server(bad_transaction) as port:
            with ModbusClient(port=port) as client:
                with self.assertRaisesRegex(ValueError, "transaction"):
                    client.read(3, 0)
        def silent(stream):
            read_exact(stream, 12, time.monotonic() + 2)
            time.sleep(.15)
        with reply_server(silent) as port:
            with ModbusClient(port=port, timeout=.05) as client:
                with self.assertRaises(TimeoutError):
                    client.read(3, 0)

    def test_stop_interrupts_partial_idle_client_and_restart(self):
        server = ModbusServer(port=0).start()
        idle = socket.create_connection(("127.0.0.1", server.bound_port))
        idle.sendall(b"\x00")
        eventually(lambda: server.client_count == 1)
        before = time.monotonic()
        server.stop()
        self.assertLess(time.monotonic() - before, 1)
        idle.close()
        server.start()
        with ModbusClient(port=server.bound_port) as client:
            self.assertEqual(client.read(3, 0), [0])
        server.stop()

    def test_rtu_real_serial_stream_and_broadcast(self):
        try:
            import serial  # noqa: F401
        except ImportError:
            self.skipTest("pyserial not installed")
        # Transparent TCP-backed serial ports form a null-modem without an OS driver.
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(2)
        listener.settimeout(3)
        peers = []
        workers = []
        def relay():
            try:
                a, b = listener.accept()[0], listener.accept()[0]
                peers.extend((a, b))
                def pump(source, target):
                    try:
                        while True:
                            data = source.recv(4096)
                            if not data:
                                return
                            target.sendall(data)
                    except OSError:
                        pass
                for source, target in ((a, b), (b, a)):
                    worker = threading.Thread(target=pump, args=(source, target), daemon=True)
                    workers.append(worker)
                    worker.start()
            except OSError:
                pass
        worker = threading.Thread(target=relay, daemon=True)
        worker.start()
        url = f"socket://127.0.0.1:{listener.getsockname()[1]}"
        server = ModbusServer(serial_port=url).start()
        client = ModbusClient(serial_port=url)
        try:
            self.exercise(client, server.memory)
            client.unit = 0
            client.write(6, 100, [4321])
            eventually(lambda: server.memory.read("holding", 100) == [4321])
            with self.assertRaises(ValueError):
                client.read(3, 0)
            client.unit = 1
            self.assertEqual(client.read(3, 100), [4321])
        finally:
            client.close()
            server.stop()
            listener.close()
            for stream in peers:
                stream.close()
            worker.join(1)
            for thread in workers:
                thread.join(1)


class PlcTests(WorkerCheckedTest):
    def test_snapshot_roundtrip_and_rejection_is_atomic(self):
        for protocol, address, bit_address in (("S7", "DB12.4", "M100.7"), ("MC3E", "D100", "M100"), ("FINS", "D100", "CIO100.15")):
            with self.subTest(protocol=protocol):
                memory = PlcMemory(protocol)
                memory.write(address, [0x1234, 0x5678])
                memory.write(bit_address, [True], "Bool")
                original = memory.snapshot()
                restored = PlcMemory(protocol)
                restored.restore(original)
                self.assertEqual(restored.snapshot(), original)
                self.assertEqual(restored.read(address, 2), [0x1234, 0x5678])
                self.assertEqual(restored.read(bit_address, 1, "Bool"), [True])
                # Snapshot mutation cannot mutate a live bank by reference.
                modified = copy.deepcopy(original)
                modified["banks"][0]["data_hex"] = "00" * (len(modified["banks"][0]["data_hex"]) // 2)
                self.assertEqual(memory.snapshot(), original)
                invalid = []
                value = copy.deepcopy(original); value["protocol"] = "FINS" if protocol != "FINS" else "S7"; invalid.append(value)
                value = copy.deepcopy(original); value["format_version"] = True; invalid.append(value)
                value = copy.deepcopy(original); value["banks"] *= 33; invalid.append(value)
                value = copy.deepcopy(original); value["banks"].append(copy.deepcopy(value["banks"][0])); invalid.append(value)
                value = copy.deepcopy(original); value["banks"][0]["area"] = 0xFF; invalid.append(value)
                value = copy.deepcopy(original); value["banks"][0]["block"] = 65536; invalid.append(value)
                value = copy.deepcopy(original); value["banks"][0]["data_hex"] += "00"; invalid.append(value)
                value = copy.deepcopy(original); value["banks"][0]["data_hex"] = "GG" + value["banks"][0]["data_hex"][2:]; invalid.append(value)
                value = copy.deepcopy(original); value["banks"][0]["data_hex"] = "00 " + value["banks"][0]["data_hex"][3:]; invalid.append(value)
                if protocol == "MC3E":
                    value = copy.deepcopy(original)
                    bit_bank = next(bank for bank in value["banks"] if bank["area"] == 0x90)
                    bit_bank["data_hex"] = "02" + bit_bank["data_hex"][2:]
                    invalid.append(value)
                for value in invalid:
                    with self.assertRaises(ValueError):
                        restored.restore(value)
                    self.assertEqual(restored.snapshot(), original, "Rejected snapshot partially replaced memory")
                restored.restore({"format_version": 1, "protocol": restored.protocol, "banks": []})
                self.assertEqual(restored.read(address, 2), [0, 0])

    def test_live_server_snapshot_restore_changes_shared_client_memory(self):
        with PlcServer("FINS", port=0) as server:
            server.memory.write("D0", [1234])
            snapshot = server.memory.snapshot()
            with PlcClient("FINS", port=server.bound_port) as client:
                client.write("D0", [5678])
                self.assertEqual(client.read("D0"), [5678])
                server.memory.restore(snapshot)
                self.assertEqual(client.read("D0"), [1234])

    def test_requests_match_windows_fixed_wire_vectors(self):
        # Fixed vectors from windows/tests/LinkLab.Tests/PlcTests.cs; S7's read
        # sequence is 2 because this client first negotiates PDU with sequence 1.
        vectors = {
            "S7": "03 00 00 1F 02 F0 80 32 01 00 00 00 02 00 0E 00 00 04 01 12 0A 10 02 00 02 00 01 84 00 00 00",
            "MC3E": "50 00 00 FF FF 03 00 0C 00 0A 00 01 04 00 00 64 00 00 A8 02 00",
            "FINS": "46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 02 00 01 01 01 82 00 64 00 00 02",
        }
        for protocol in vectors:
            def peer(stream):
                session = _Session(PlcMemory(protocol))
                rounds = 3 if protocol == "S7" else 2 if protocol == "FINS" else 1
                for n in range(rounds):
                    request = _frame(stream, protocol, time.monotonic() + 2)
                    if n == rounds - 1:
                        self.assertEqual(request, bytes.fromhex(vectors[protocol]))
                    stream.sendall(session.process(request))
            with self.subTest(protocol=protocol), reply_server(peer) as port:
                with PlcClient(protocol, port=port) as client:
                    self.assertEqual(client.read("DB1.0" if protocol == "S7" else "D100", 1 if protocol == "S7" else 2), [0] if protocol == "S7" else [0, 0])

    def test_address_validation_and_bit_aliasing(self):
        for protocol, address, data_type in (("S7", "DB0.0", "UInt16"), ("S7", "M0.8", "Bool"), ("S7", "DB1.DBW0", "Float32"), ("MC3E", "M0", "UInt16"), ("MC3E", "D0", "Bool"), ("FINS", "D0.16", "Bool"), ("FINS", "D0.0", "UInt16")):
            with self.assertRaises(ValueError):
                parse_address(protocol, address, data_type)
        memory = PlcMemory("S7")
        memory.write("DB1.0", [0])
        memory.write("DB1.0.0", [True], "Bool")
        self.assertEqual(memory.read("DB1.0"), [256])
        memory = PlcMemory("FINS")
        memory.write("D0", [0])
        memory.write("D0.15", [True], "Bool")
        self.assertEqual(memory.read("D0"), [32768])

    def test_three_plc_protocols_real_loopback(self):
        for protocol, address, bits in (("S7", "DB1.0", "DB1.100.2"), ("MC3E", "D0", "M100"), ("FINS", "D0", "D100.2")):
            with self.subTest(protocol=protocol):
                traffic = []
                with PlcServer(protocol, port=0, on_traffic=lambda *args: traffic.append(args)) as server:
                    with PlcClient(protocol, port=server.bound_port) as client:
                        for data_type, values in (("UInt16", [0, 65535, 1234]), ("Int16", [-32768, 32767]), ("Int32", [-2147483648, 2147483647]), ("UInt32", [0, 0xF1234567]), ("Float32", [23.5, -12.25])):
                            client.write(address, values, data_type)
                            self.assertEqual(client.read(address, len(values), data_type), values)
                            self.assertEqual(server.memory.read(address, len(values), data_type), values)
                        values = [True] if protocol == "S7" else [True, False, True, True, False]
                        client.write(bits, values, "Bool")
                        self.assertEqual(client.read(bits, len(values), "Bool"), values)
                        with PlcClient(protocol, port=server.bound_port) as other:
                            self.assertEqual(other.read(bits, 1, "Bool"), [True])
                    self.assertTrue(any(row[0] == "TX" for row in traffic))
                    # A malicious partial frame must not prevent a prompt stop.
                    idle = socket.create_connection(("127.0.0.1", server.bound_port))
                    idle.sendall(b"\x03" if protocol == "S7" else b"F")
                    eventually(lambda: server.client_count >= 1)
                    before = time.monotonic()
                    server.stop()
                    self.assertLess(time.monotonic() - before, 1)
                    idle.close()

    def test_fins_sid_and_mc_route_reply_validation(self):
        for protocol in ("FINS", "MC3E", "S7"):
            def wrong_reply(stream):
                session = _Session(PlcMemory(protocol))
                rounds = 3 if protocol == "S7" else 2 if protocol == "FINS" else 1
                for n in range(rounds):
                    request = _frame(stream, protocol, time.monotonic() + 2)
                    reply = bytearray(session.process(request))
                    if n == rounds - 1:
                        reply[{"FINS": 25, "MC3E": 2, "S7": 11}[protocol]] ^= 1
                    stream.sendall(reply)
            with self.subTest(protocol=protocol), reply_server(wrong_reply) as port:
                with PlcClient(protocol, port=port) as client:
                    with self.assertRaises(ValueError):
                        client.read("DB1.0" if protocol == "S7" else "D0")


class TransportTests(WorkerCheckedTest):
    def test_client_close_cancels_pending_open_and_prevents_reopening(self):
        for client in (ModbusClient(), PlcClient("S7")):
            with self.subTest(client=type(client).__name__):
                entered, release = threading.Event(), threading.Event()
                discarded = []
                failures = []
                class PendingSocket:
                    def close(self):
                        discarded.append(True)
                def opening(*args, **kwargs):
                    entered.set()
                    if not release.wait(2):
                        raise TimeoutError("Open gate timed out")
                    return PendingSocket()
                def work():
                    try:
                        client.connect()
                    except OSError as exc:
                        failures.append(exc)
                with patch("socket.create_connection", opening):
                    worker = threading.Thread(target=work)
                    worker.start()
                    self.assertTrue(entered.wait(2))
                    client.close()
                    release.set()
                    worker.join(2)
                self.assertFalse(worker.is_alive())
                self.assertEqual(discarded, [True])
                self.assertEqual(len(failures), 1)
                self.assertIsNone(client._stream)
                with self.assertRaises(OSError):
                    client.connect()

    def test_tcp_multi_client_target_and_udp(self):
        messages = []
        server = NetworkTransport("TCP Server", port=0, local_host="127.0.0.1", on_data=lambda data, peer: messages.append((data, peer))).start()
        a, b = socket.create_connection(("127.0.0.1", server.bound_port)), socket.create_connection(("127.0.0.1", server.bound_port))
        a.settimeout(1)
        b.settimeout(1)
        try:
            a.sendall(b"first")
            eventually(lambda: bool(messages) and len(server.peers) == 2)
            server.send(b"targeted", peer=messages[0][1])
            self.assertEqual(a.recv(64), b"targeted")
            server.send(b"all")
            self.assertEqual(a.recv(64), b"all")
            self.assertEqual(b.recv(64), b"all")
        finally:
            a.close(); b.close(); server.stop()
        packets = []
        udp = NetworkTransport("UDP", port=9, local_host="127.0.0.1", on_data=lambda data, peer: packets.append((data, peer))).start()
        sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sender.bind(("127.0.0.1", 0))
        sender.settimeout(1)
        try:
            sender.sendto(b"udp", ("127.0.0.1", udp.bound_port))
            eventually(lambda: bool(packets))
            udp.send(b"reply", packets[0][1])
            self.assertEqual(sender.recv(64), b"reply")
        finally:
            sender.close(); udp.stop()

    def test_serial_loopback_can_stop(self):
        try:
            import serial  # noqa: F401
        except ImportError:
            self.skipTest("pyserial not installed")
        received = []
        transport = SerialTransport("loop://", on_data=lambda data, peer: received.append(data)).start()
        try:
            transport.send(b"\x00\xffRTU", peer="ignored")
            eventually(lambda: b"".join(received) == b"\x00\xffRTU")
        finally:
            transport.stop()


if __name__ == "__main__":
    unittest.main()
