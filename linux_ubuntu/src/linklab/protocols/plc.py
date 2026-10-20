"""Siemens S7, Mitsubishi MC 3E binary and Omron FINS/TCP memory I/O.

The simulator implements memory access, not PLC execution or a vendor runtime.
Wire layouts and supported banks mirror LinkLab's independent Windows framing.
"""
from __future__ import annotations

from dataclasses import dataclass
import re
import socket
import struct
import threading
import time

from .codec import pack_values, unpack_values, type_size
from .transport import TcpService, notify, read_exact


def _protocol(value):
    key = re.sub(r"[^A-Z0-9]", "", str(value).upper())
    if key in ("S7", "SIEMENSS7", "SIEMENS", "S7TCP"):
        return "S7"
    if key in ("MC", "MC3E", "MITSUBISHIMC3E", "MITSUBISHI"):
        return "MC3E"
    if key in ("FINS", "FINSTCP", "OMRONFINSTCP", "OMRON"):
        return "FINS"
    raise ValueError("Protocol must be S7, MC3E or FINS")


_DEFAULT_ORDER = {"S7": "ABCD", "MC3E": "DCBA", "FINS": "CDAB"}
_MC_WORD = {"D": 0xA8, "W": 0xB4, "R": 0xAF}
_MC_BIT = {"M": 0x90, "X": 0x9C, "Y": 0x9D, "L": 0x92, "B": 0xA0}
_FINS = {"D": 0x82, "CIO": 0xB0, "W": 0xB1, "H": 0xB2, "A": 0xB3}


@dataclass(frozen=True)
class PlcAddress:
    protocol: str
    area: int
    block: int
    index: int
    bit: int = 0
    is_bit: bool = False

    @property
    def mc_bit(self):
        return self.protocol == "MC3E" and self.area in _MC_BIT.values()


def parse_address(protocol, address, data_type="UInt16"):
    protocol = _protocol(protocol)
    text, is_bit = str(address).strip().upper(), data_type == "Bool"
    size = type_size(data_type)
    if protocol == "S7":
        db = re.fullmatch(r"DB(\d+)\.(?:DB([BWDX]))?(\d+)(?:\.([0-7]))?", text)
        other = re.fullmatch(r"([MIQ])([BWD]?)(\d+)(?:\.([0-7]))?", text)
        match = db or other
        if not match:
            raise ValueError("S7: DB1.DBW0 / DB1.0 / M0; Bool: DB1.DBX0.0 / M0.0")
        block = int(match[1]) if db else 0
        area = 0x84 if db else {"M": 0x83, "I": 0x81, "Q": 0x82}[match[1]]
        suffix, index, bit = match[2] or "", int(match[3]), match[4]
        if db and not 1 <= block <= 65535 or index > 65535 or is_bit != (bit is not None):
            raise ValueError("S7 block, byte offset or bit address is invalid")
        if suffix in ("B", "W", "D") and (is_bit or size != {"B": 1, "W": 2, "D": 4}[suffix]) or suffix == "X" and not is_bit:
            raise ValueError("S7 address suffix does not match value type")
        return PlcAddress(protocol, area, block, index, int(bit or 0), is_bit)
    if protocol == "MC3E":
        match = re.fullmatch(r"(D|M|X|Y|W|R|L|B)([0-9A-F]+)", text)
        if not match:
            raise ValueError("MC: D100/W100 for words, M100/X10/Y10/L0/B0 for Bool")
        prefix = match[1]
        index = int(match[2], 16 if prefix in ("X", "Y", "W", "B") else 10)
        if is_bit != (prefix in _MC_BIT) or not is_bit and size % 2:
            raise ValueError("MC device bank and value type do not match")
        area = {**_MC_WORD, **_MC_BIT}[prefix]
        bit = 0
    else:
        match = re.fullmatch(r"(CIO|D|W|H|A)(\d+)(?:\.(\d{1,2}))?", text)
        if not match or is_bit != (match[3] is not None):
            raise ValueError("FINS: D100/CIO0/W0/H0/A0; Bool needs a .0..15 bit suffix")
        index, bit, area = int(match[2]), int(match[3] or 0), _FINS[match[1]]
        if bit > 15 or not is_bit and size % 2:
            raise ValueError("FINS bit or value type is invalid")
    if index > 65535:
        raise ValueError("PLC device index must be 0..65535")
    return PlcAddress(protocol, area, 0, index, bit, is_bit)


class PlcMemory:
    def __init__(self, protocol="S7"):
        self.protocol = _protocol(protocol)
        self._banks = {}
        self._lock = threading.RLock()

    def _validate(self, address, count):
        a = address
        if a.protocol != self.protocol or count < 1 or not 0 <= a.index <= 65535:
            raise ValueError("PLC memory protocol, index or length mismatch")
        if a.protocol == "S7":
            if a.area not in (0x81, 0x82, 0x83, 0x84) or (a.area == 0x84 and not 1 <= a.block <= 65535) or (a.area != 0x84 and a.block != 0) or not 0 <= a.bit <= 7:
                raise ValueError("Unsupported S7 bank")
        elif a.protocol == "MC3E":
            if a.area not in {**_MC_WORD, **_MC_BIT}.values() or a.mc_bit != a.is_bit or a.block != 0:
                raise ValueError("Unsupported MC bank/type")
        elif a.area not in _FINS.values() or a.block != 0 or not 0 <= a.bit <= 15:
            raise ValueError("Unsupported FINS bank")
        size = 65536 if a.protocol == "S7" or a.mc_bit else 131072
        offset = a.index if a.protocol == "S7" or a.mc_bit else a.index * 2
        if a.is_bit and not a.mc_bit:
            width = 8 if a.protocol == "S7" else 16
            end = offset + ((a.bit + count - 1) // width + 1) * (width // 8)
        else:
            end = offset + count
        if end > size:
            raise ValueError("PLC memory range exceeds bank")
        return (a.area, a.block), offset, size

    @staticmethod
    def _bit_location(a, n):
        bit = a.bit + n
        if a.protocol == "S7":
            return a.index + bit // 8, 1 << (bit % 8)
        return a.index * 2 + bit // 16 * 2 + (1 if bit % 16 < 8 else 0), 1 << (bit % 8)

    def read_raw(self, address, count):
        key, offset, _ = self._validate(address, count)
        with self._lock:
            bank = self._banks.get(key)
            if bank is None:
                return bytes(count)
            if address.is_bit and not address.mc_bit:
                return bytes(int(bool(bank[where] & mask)) for where, mask in (self._bit_location(address, n) for n in range(count)))
            return bytes(bank[offset:offset + count])

    def write_raw(self, address, data):
        key, offset, size = self._validate(address, len(data))
        if address.is_bit and any(value not in (0, 1) for value in data):
            raise ValueError("PLC bit values must be 0 or 1")
        with self._lock:
            if key not in self._banks:
                if len(self._banks) >= 64:
                    raise ValueError("Simulator supports at most 64 banks / DBs")
                self._banks[key] = bytearray(size)
            bank = self._banks[key]
            if address.is_bit and not address.mc_bit:
                for n, value in enumerate(data):
                    where, mask = self._bit_location(address, n)
                    bank[where] = bank[where] | mask if value else bank[where] & ~mask
            else:
                bank[offset:offset + len(data)] = data

    def read(self, address, count=1, data_type="UInt16", byte_order=None):
        a = parse_address(self.protocol, address, data_type)
        _request_size(a, count, data_type)
        return unpack_values(self.read_raw(a, count * type_size(data_type)), data_type, byte_order or _DEFAULT_ORDER[self.protocol])

    def write(self, address, values, data_type="UInt16", byte_order=None):
        values = _values(values)
        a = parse_address(self.protocol, address, data_type)
        _request_size(a, len(values), data_type)
        self.write_raw(a, pack_values(values, data_type, byte_order or _DEFAULT_ORDER[self.protocol]))

    def snapshot(self):
        """Return an independent, consistent JSON-compatible memory snapshot."""
        with self._lock:
            return {"format_version": 1, "protocol": self.protocol,
                    "banks": [{"area": area, "block": block, "data_hex": bank.hex()}
                              for (area, block), bank in sorted(self._banks.items())]}

    def restore(self, snapshot):
        """Validate completely before replacing banks, including on a live server."""
        if not isinstance(snapshot, dict) or set(snapshot) != {"format_version", "protocol", "banks"}:
            raise ValueError("Invalid PLC snapshot schema")
        if type(snapshot["format_version"]) is not int or snapshot["format_version"] != 1 or snapshot["protocol"] != self.protocol:
            raise ValueError("PLC snapshot version or protocol mismatch")
        banks = snapshot["banks"]
        if not isinstance(banks, list) or len(banks) > 64:
            raise ValueError("PLC snapshot supports at most 64 banks")
        candidate = {}
        for item in banks:
            if not isinstance(item, dict) or set(item) != {"area", "block", "data_hex"}:
                raise ValueError("Invalid PLC snapshot bank schema")
            area, block, data_hex = item["area"], item["block"], item["data_hex"]
            if type(area) is not int or type(block) is not int:
                raise ValueError("PLC snapshot area and block must be integers")
            bit = self.protocol == "MC3E" and area in _MC_BIT.values()
            key, _, capacity = self._validate(PlcAddress(self.protocol, area, block, 0, 0, bit), 1)
            if key in candidate:
                raise ValueError("Duplicate PLC snapshot bank")
            # Reject oversized data before decoding/allocating. Fixed lengths also
            # prevent a restored bank from silently growing/shrinking on writes.
            if not isinstance(data_hex, str) or len(data_hex) != capacity * 2:
                raise ValueError("PLC snapshot bank has an invalid data length")
            try:
                data = bytes.fromhex(data_hex)
            except ValueError as exc:
                raise ValueError("PLC snapshot bank contains invalid HEX") from exc
            if len(data) != capacity or bit and any(value > 1 for value in data):
                raise ValueError("PLC snapshot bank has invalid content")
            candidate[key] = bytearray(data)
        with self._lock:
            self._banks = candidate


def _values(values):
    return [values] if isinstance(values, (str, int, float, bool)) else list(values)


def _request_size(address, count, data_type):
    size = type_size(data_type)
    if not isinstance(count, int) or not 1 <= count <= 100 or count * size > 200:
        raise ValueError("PLC requests support 1..100 values, at most 200 bytes")
    if address.protocol == "S7" and address.is_bit and count != 1:
        raise ValueError("S7 Bool requests support one bit at a time")


def _be(data, offset):
    return struct.unpack_from(">H", data, offset)[0]


def _le(data, offset):
    return struct.unpack_from("<H", data, offset)[0]


def _u32(data, offset):
    return struct.unpack_from(">I", data, offset)[0]


def _frame(stream, protocol, deadline):
    header_size = {"S7": 4, "MC3E": 9, "FINS": 8}[protocol]
    header = read_exact(stream, header_size, deadline)
    rest = _be(header, 2) - 4 if protocol == "S7" else _le(header, 7) if protocol == "MC3E" else _u32(header, 4)
    if not 2 <= rest <= 8192 or protocol == "S7" and header[:2] != b"\x03\x00" or protocol == "FINS" and header[:4] != b"FINS":
        raise ValueError("Invalid PLC frame header/length")
    return header + read_exact(stream, rest, deadline)


def _s7_job(sequence, parameter, data=b""):
    return b"\x03\x00" + struct.pack(">H", 17 + len(parameter) + len(data)) + b"\x02\xf0\x80\x32\x01\x00\x00" + struct.pack(">HHH", sequence, len(parameter), len(data)) + parameter + data


def _s7_ack(request, parameter, data=b"", error=0):
    return b"\x03\x00" + struct.pack(">H", 19 + len(parameter) + len(data)) + b"\x02\xf0\x80\x32\x03\x00\x00" + request[11:13] + struct.pack(">HHH", len(parameter), len(data), error) + parameter + data


def _fins(command, body, error=0):
    return b"FINS" + struct.pack(">III", 8 + len(body), command, error) + body


def _pack_bits(data):
    result = bytearray((len(data) + 1) // 2)
    for n, value in enumerate(data):
        if value not in (0, 1):
            raise ValueError("MC bits must be 0/1")
        result[n // 2] |= value << (4 if n % 2 == 0 else 0)
    return bytes(result)


def _unpack_bits(data, count):
    if len(data) != (count + 1) // 2:
        raise ValueError("MC bit payload length mismatch")
    result = bytes((data[n // 2] >> (4 if n % 2 == 0 else 0)) & 15 for n in range(count))
    if any(value > 1 for value in result):
        raise ValueError("Invalid MC nibble bit encoding")
    return result


class PlcClient:
    def __init__(self, protocol="S7", host="127.0.0.1", port=None, timeout=2, rack=0, slot=1,
                 on_traffic=None, client_node=0, server_node=0, local_tsap=0x0100, remote_tsap=0,
                 network=0, station=255, io_number=1023, unit=0, source_network=0):
        self.protocol = _protocol(protocol)
        self.host, self.port = host, int(port if port is not None else {"S7": 102, "MC3E": 6000, "FINS": 9600}[self.protocol])
        if not .05 <= timeout <= 60 or not 0 <= rack <= 7 or not 0 <= slot <= 31:
            raise ValueError("Timeout/rack/slot out of range")
        if any(not 0 <= value <= 255 for value in (client_node, server_node, network, station, unit, source_network)) or not 0 <= io_number <= 65535:
            raise ValueError("PLC route out of range")
        if not 0 <= local_tsap <= 65535 or not 0 <= remote_tsap <= 65535:
            raise ValueError("TSAP out of range")
        self.timeout, self.rack, self.slot = float(timeout), rack, slot
        self.client_node, self.server_node = client_node, server_node
        self.local_tsap, self.remote_tsap = local_tsap, remote_tsap
        self.network, self.station, self.io_number, self.unit, self.source_network = network, station, io_number, unit, source_network
        self.on_traffic = on_traffic
        self._stream = None
        self._lock = threading.RLock()
        self._sequence = 0
        self._pdu = 240
        self._nodes = (2, 1)
        self._generation = 0
        self._state_lock = threading.Lock()
        self._closed = False
        self._ready = False

    def _next(self):
        self._sequence = (self._sequence + 1) & 65535
        return self._sequence

    @property
    def connected(self):
        with self._state_lock:
            return self._stream is not None and self._ready and not self._closed

    def _exchange(self, frame, deadline):
        self._stream.settimeout(max(.001, deadline - time.monotonic()))
        self._stream.sendall(frame)
        peer = f"{self.host}:{self.port}"
        notify(self.on_traffic, "TX", frame, peer)
        result = _frame(self._stream, self.protocol, deadline)
        notify(self.on_traffic, "RX", result, peer)
        return result

    def connect(self):
        with self._lock:
            if self._stream is not None:
                return self
            with self._state_lock:
                if self._closed:
                    raise OSError("PLC client is closed; create a new connection")
                generation = self._generation
            stream = socket.create_connection((self.host, self.port), self.timeout)
            with self._state_lock:
                if generation != self._generation:
                    stream.close()
                    raise OSError("Connection was cancelled while opening")
                self._stream = stream
            deadline = time.monotonic() + self.timeout
            try:
                stream.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                if self.protocol == "S7":
                    cotp = bytes.fromhex("03 00 00 16 11 E0 00 00 00 01 00 C1 02 01 00 C2 02 03 01 C0 01 0A")
                    cotp = cotp[:13] + struct.pack(">H", self.local_tsap) + cotp[15:17] + struct.pack(">H", self.remote_tsap or 0x0300 | self.rack * 32 + self.slot) + cotp[19:]
                    reply = self._exchange(cotp, deadline)
                    if len(reply) < 11 or reply[5] != 0xD0 or _be(reply, 6) != 1:
                        raise ValueError("S7 COTP connection refused")
                    sequence = self._next()
                    reply = self._exchange(_s7_job(sequence, bytes.fromhex("F0 00 00 01 00 01 01 E0")), deadline)
                    if len(reply) != 27 or reply[7:9] != b"\x32\x03" or _be(reply, 11) != sequence or _be(reply, 13) != 8 or _be(reply, 15) or _be(reply, 17) or reply[19] != 0xF0:
                        raise ValueError("S7 PDU negotiation failed")
                    self._pdu = _be(reply, 25)
                    if not 64 <= self._pdu <= 8192:
                        raise ValueError("Invalid negotiated PDU")
                elif self.protocol == "FINS":
                    reply = self._exchange(_fins(0, struct.pack(">I", self.client_node)), deadline)
                    if len(reply) != 24 or _u32(reply, 8) != 1 or _u32(reply, 12) or not 1 <= _u32(reply, 16) <= 254 or not 1 <= _u32(reply, 20) <= 254:
                        raise ValueError("FINS node negotiation failed")
                    self._nodes = (_u32(reply, 16), self.server_node or _u32(reply, 20))
                with self._state_lock:
                    if self._closed or generation != self._generation or self._stream is not stream:
                        raise OSError("Connection was cancelled while opening")
                    self._ready = True
            except Exception:
                self.close()
                raise
        return self

    def close(self):
        with self._state_lock:
            self._closed = True
            self._ready = False
            self._generation += 1
            stream, self._stream = self._stream, None
        if stream:
            try:
                stream.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            stream.close()

    stop = close

    def __enter__(self):
        return self.connect()

    def __exit__(self, *_):
        self.close()

    def read(self, address, count=1, data_type="UInt16", byte_order=None):
        return unpack_values(self._request(address, count, data_type), data_type, byte_order or _DEFAULT_ORDER[self.protocol])

    def write(self, address, values, data_type="UInt16", byte_order=None):
        values = _values(values)
        self._request(address, len(values), data_type, pack_values(values, data_type, byte_order or _DEFAULT_ORDER[self.protocol]))

    def _request(self, address, count, data_type, data=None):
        a = parse_address(self.protocol, address, data_type)
        _request_size(a, count, data_type)
        length = count * type_size(data_type)
        PlcMemory(self.protocol)._validate(a, length)
        wire_count = count if a.is_bit else length if self.protocol == "S7" else length // 2
        with self._lock:
            try:
                self.connect()
                sequence = self._next()
                if self.protocol == "S7":
                    bit_address = a.index * 8 + a.bit
                    parameter = bytes((5 if data is not None else 4, 1, 0x12, 10, 0x10, 1 if a.is_bit else 2)) + struct.pack(">HHB", wire_count, a.block, a.area) + bit_address.to_bytes(3, "big")
                    body = b"" if data is None else bytes((0, 3 if a.is_bit else 4)) + struct.pack(">H", count if a.is_bit else length * 8) + data
                    frame = _s7_job(sequence, parameter, body)
                    if len(frame) - 7 > self._pdu or 18 + length > self._pdu:
                        raise ValueError("Request exceeds negotiated S7 PDU")
                elif self.protocol == "MC3E":
                    body = struct.pack("<HHH", 10, 0x1401 if data is not None else 0x0401, int(a.is_bit)) + a.index.to_bytes(3, "little") + bytes((a.area,)) + struct.pack("<H", wire_count)
                    if data is not None:
                        body += _pack_bits(data) if a.is_bit else data
                    frame = b"\x50\x00" + bytes((self.network, self.station)) + struct.pack("<H", self.io_number) + bytes((self.unit,)) + struct.pack("<H", len(body)) + body
                else:
                    client_node, server_node = self._nodes
                    header = bytes((0x80, 0, 2, self.network, server_node, self.unit, self.source_network, client_node, 0, sequence & 255))
                    body = header + bytes((1, 2 if data is not None else 1, a.area - 0x80 if a.is_bit else a.area)) + struct.pack(">H", a.index) + bytes((a.bit,)) + struct.pack(">H", wire_count) + (data or b"")
                    frame = _fins(2, body)
                response = self._exchange(frame, time.monotonic() + self.timeout)
                return self._parse(response, frame, a, count, length, data)
            except Exception:
                self.close()
                raise

    def _parse(self, reply, request, address, count, length, data):
        if self.protocol == "S7":
            if len(reply) < 22 or reply[5:9] != b"\xf0\x80\x32\x03" or _be(reply, 11) != _be(request, 11) or 19 + _be(reply, 13) + _be(reply, 15) != len(reply) or _be(reply, 17):
                raise ValueError("S7 acknowledgement, sequence or error mismatch")
            if _be(reply, 13) != 2 or reply[19:21] != request[17:19] or reply[21] != 0xFF:
                raise ValueError(f"S7 item error / unsupported address: {reply[21]:02X}")
            if data is not None:
                if len(reply) != 22:
                    raise ValueError("S7 write acknowledgement length mismatch")
                return data
            if len(reply) != 25 + length or reply[22] != (3 if address.is_bit else 4) or _be(reply, 23) != (count if address.is_bit else length * 8):
                raise ValueError("S7 data type / length mismatch")
            result = reply[25:]
        elif self.protocol == "MC3E":
            if len(reply) < 11 or reply[:2] != b"\xd0\x00" or reply[2:7] != request[2:7] or _le(reply, 7) != len(reply) - 9:
                raise ValueError("MC response header, route or length mismatch")
            if _le(reply, 9):
                raise ValueError(f"MC end code {_le(reply, 9):04X}")
            expected = 0 if data is not None else (count + 1) // 2 if address.is_bit else length
            if len(reply) != 11 + expected:
                raise ValueError("MC response payload length mismatch")
            result = data if data is not None else _unpack_bits(reply[11:], count) if address.is_bit else reply[11:]
        else:
            if len(reply) < 30 or _u32(reply, 8) != 2 or _u32(reply, 12) or reply[16] != 0xC0 or reply[25:28] != request[25:28] or reply[19:22] != request[22:25] or reply[22:25] != request[19:22]:
                raise ValueError("FINS command, route or SID mismatch")
            if _be(reply, 28):
                raise ValueError(f"FINS end code {_be(reply, 28):04X}")
            if len(reply) != 30 + (0 if data is not None else length):
                raise ValueError("FINS response payload length mismatch")
            result = data if data is not None else reply[30:]
        if address.is_bit and any(value > 1 for value in result):
            raise ValueError("Invalid PLC bit response")
        return result


class _Session:
    def __init__(self, memory, assigned_node=2):
        self.memory, self.state, self.node, self.assigned_node = memory, 0, 0, assigned_node

    def process(self, frame):
        return {"S7": self.s7, "MC3E": self.mc, "FINS": self.fins}[self.memory.protocol](frame)

    def s7(self, frame):
        f = frame
        if self.state == 0:
            if len(f) < 11 or f[5] != 0xE0 or f[4] != len(f) - 5:
                raise ValueError("Expected a COTP connection request")
            reply = bytearray(f)
            reply[5], reply[6:8], reply[8:10] = 0xD0, f[8:10], b"\x00\x01"
            self.state = 1
            return bytes(reply)
        if len(f) < 18 or f[4:9] != b"\x02\xf0\x80\x32\x01" or len(f) != 17 + _be(f, 13) + _be(f, 15):
            raise ValueError("Malformed S7 request")
        if f[17] == 0xF0 and self.state == 1:
            if len(f) != 25 or _be(f, 13) != 8 or _be(f, 23) < 240:
                return _s7_ack(f, b"", error=0x8104)
            self.state = 2
            return _s7_ack(f, bytes.fromhex("F0 00 00 01 00 01") + struct.pack(">H", min(480, _be(f, 23))))
        if self.state != 2 or len(f) < 31 or f[17] not in (4, 5) or f[18] != 1 or _be(f, 13) != 14:
            return _s7_ack(f, b"", error=0x8104)
        write, bit = f[17] == 5, f[22] == 1
        try:
            if f[19:22] != b"\x12\x0a\x10" or f[22] not in (1, 2, 4, 6, 8):
                raise ValueError("Unsupported S7 transport type")
            count = _be(f, 23)
            length = count * {1: 1, 2: 1, 4: 2, 6: 4, 8: 4}[f[22]]
            address = int.from_bytes(f[28:31], "big")
            if not 1 <= length <= 400 or bit and count != 1 or not bit and address % 8:
                raise ValueError("Unsupported S7 quantity/address")
            a = PlcAddress("S7", f[27], _be(f, 25), address // 8, address % 8, bit)
            if write:
                if len(f) != 35 + length or f[31] != 0 or f[32] != (3 if bit else 4) or _be(f, 33) != (count if bit else length * 8):
                    raise ValueError("S7 write length mismatch")
                self.memory.write_raw(a, f[35:])
                return _s7_ack(f, b"\x05\x01", b"\xff")
            if len(f) != 31:
                raise ValueError("Read request must not contain data")
            data = self.memory.read_raw(a, length)
            return _s7_ack(f, b"\x04\x01", bytes((0xFF, 3 if bit else 4)) + struct.pack(">H", count if bit else length * 8) + data)
        except ValueError:
            return _s7_ack(f, bytes((f[17], 1)), b"\x05" if write else b"\x05\x00\x00\x00")

    def mc(self, f):
        if len(f) < 11 or f[:2] != b"\x50\x00":
            raise ValueError("Expected an MC 3E binary request")
        error, data = 0, b""
        try:
            if len(f) < 21:
                raise ValueError("Truncated MC command")
            command, sub, count = _le(f, 11), _le(f, 13), _le(f, 19)
            if command not in (0x0401, 0x1401) or sub not in (0, 1):
                error = 0xC059
                raise ValueError("Unsupported MC command")
            bit = sub == 1
            a = PlcAddress("MC3E", f[18], 0, int.from_bytes(f[15:18], "little"), 0, bit)
            if not 1 <= count <= 960:
                raise ValueError("MC quantity out of range")
            length = count if bit else count * 2
            if command == 0x1401:
                if len(f) != 21 + ((count + 1) // 2 if bit else length):
                    raise ValueError("MC write length mismatch")
                self.memory.write_raw(a, _unpack_bits(f[21:], count) if bit else f[21:])
            else:
                if len(f) != 21:
                    raise ValueError("MC read must not contain data")
                data = self.memory.read_raw(a, length)
                if bit:
                    data = _pack_bits(data)
        except ValueError:
            error, data = error or 0xC051, b""
        return b"\xd0\x00" + f[2:7] + struct.pack("<HH", len(data) + 2, error) + data

    def fins(self, f):
        if len(f) < 16 or _u32(f, 12):
            raise ValueError("Malformed FINS TCP request")
        if self.state == 0:
            if len(f) != 20 or _u32(f, 8) or _u32(f, 16) > 254:
                raise ValueError("Expected FINS node negotiation")
            self.node = f[19] or self.assigned_node
            if self.node == 1:
                return _fins(1, b"", 0x20)
            self.state = 1
            return _fins(1, struct.pack(">II", self.node, 1))
        if len(f) < 28 or _u32(f, 8) != 2:
            raise ValueError("Expected FINS data request")
        header = bytearray(f[16:28])
        header[0], header[3:6], header[6:9] = 0xC0, f[22:25], f[19:22]
        error, data = 0, b""
        try:
            if f[16] != 0x80 or f[20] != 1 or f[23] != self.node or f[21] or f[19] or f[22]:
                error = 0x0501
                raise ValueError("FINS route mismatch")
            if _be(f, 26) not in (0x0101, 0x0102):
                error = 0x0401
                raise ValueError("Unsupported FINS command")
            if len(f) < 34:
                raise ValueError("Truncated FINS request")
            bit, write = f[28] < 0x80, f[27] == 2
            a = PlcAddress("FINS", f[28] + 0x80 if bit else f[28], 0, _be(f, 29), f[31], bit)
            count = _be(f, 32)
            if not 1 <= count <= 999 or not bit and f[31]:
                raise ValueError("FINS quantity/bit mismatch")
            length = count if bit else count * 2
            if write:
                if len(f) != 34 + length:
                    raise ValueError("FINS write length mismatch")
                self.memory.write_raw(a, f[34:])
            else:
                if len(f) != 34:
                    raise ValueError("FINS read must not contain data")
                data = self.memory.read_raw(a, length)
        except ValueError:
            error, data = error or 0x1103, b""
        return _fins(2, bytes(header) + struct.pack(">H", error) + data)


class PlcServer:
    def __init__(self, protocol="S7", host="127.0.0.1", port=None, memory=None, on_traffic=None):
        self.protocol = _protocol(protocol)
        self.memory = memory if memory is not None else PlcMemory(self.protocol)
        if self.memory.protocol != self.protocol:
            raise ValueError("PLC server and memory protocol mismatch")
        self.host, self.port = host, int(port if port is not None else {"S7": 102, "MC3E": 6000, "FINS": 9600}[self.protocol])
        self.on_traffic, self._server = on_traffic, None

    @property
    def bound_port(self):
        return self._server.bound_port if self._server else 0

    @property
    def client_count(self):
        return self._server.client_count if self._server else 0

    def start(self):
        if self._server:
            raise RuntimeError("PLC simulator is already running")
        self._server = TcpService(self._serve, self.host, self.port, self.on_traffic).start()
        return self

    def _serve(self, stream, peer, stop):
        session = _Session(self.memory)
        while not stop.is_set():
            request = _frame(stream, self.protocol, time.monotonic() + 300)
            notify(self.on_traffic, "RX", request, peer)
            reply = session.process(request)
            stream.settimeout(2)
            stream.sendall(reply)
            notify(self.on_traffic, "TX", reply, peer)

    def stop(self):
        if self._server:
            self._server.stop()
            self._server = None

    close = stop

    def __enter__(self):
        return self.start()

    def __exit__(self, *_):
        self.stop()
