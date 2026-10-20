"""Modbus RTU/TCP master and bounded, shared-memory slave simulator."""
from __future__ import annotations

from array import array
import socket
import struct
import threading
import time

from .codec import crc16
from .transport import TcpService, notify, read_exact


class ModbusError(IOError):
    def __init__(self, code, function=0):
        self.code, self.function = code, function
        super().__init__(f"Modbus exception {code:02X} for function {function:02X}")


class Memory:
    def __init__(self, size=65536):
        if not 1 <= size <= 65536:
            raise ValueError("Memory size must be 1..65536")
        self.size = size
        self._lock = threading.RLock()
        self._areas = {"coils": bytearray(size), "discrete": bytearray(size),
                       "holding": array("H", [0]) * size, "input": array("H", [0]) * size}

    def _validate(self, area, address, count):
        if area not in self._areas:
            raise ValueError("Area must be coils, discrete, holding or input")
        if not isinstance(address, int) or not isinstance(count, int) or address < 0 or count < 1 or address + count > self.size:
            raise ValueError("Address range exceeds memory")

    def read(self, area, address, count=1):
        self._validate(area, address, count)
        with self._lock:
            return list(self._areas[area][address:address + count])

    def write(self, area, address, values):
        values = list(values)
        self._validate(area, address, len(values))
        maximum = 1 if area in ("coils", "discrete") else 65535
        if any(not isinstance(value, int) or not 0 <= value <= maximum for value in values):
            raise ValueError("Memory value is outside its data-area range")
        with self._lock:
            for n, value in enumerate(values):
                self._areas[area][address + n] = value


def _range(address, count, limit):
    if not isinstance(address, int) or not isinstance(count, int) or not 0 <= address <= 65535 or not 1 <= count <= limit or address + count > 65536:
        raise ValueError(f"Invalid address/count (maximum quantity {limit})")


def build_pdu(function, address, count=1, values=None):
    if function in (1, 2, 3, 4):
        _range(address, count, 2000 if function < 3 else 125)
        if values is not None:
            raise ValueError("Read requests cannot contain write values")
        return struct.pack(">BHH", function, address, count)
    if function not in (5, 6, 15, 16):
        raise ValueError("Supported functions: 01/02/03/04/05/06/15/16")
    if isinstance(values, (int, bool)):
        values = [values]
    values = list(values or [])
    _range(address, len(values), 1 if function in (5, 6) else 1968 if function == 15 else 123)
    maximum = 1 if function in (5, 15) else 65535
    if any(not isinstance(value, int) or not 0 <= value <= maximum for value in values):
        raise ValueError("Invalid write value")
    if function in (5, 6):
        value = (0xFF00 if values[0] else 0) if function == 5 else values[0]
        return struct.pack(">BHH", function, address, value)
    if function == 15:
        packed = bytearray((len(values) + 7) // 8)
        for n, value in enumerate(values):
            packed[n // 8] |= value << (n % 8)
        data = bytes(packed)
    else:
        data = struct.pack(">" + "H" * len(values), *values)
    return struct.pack(">BHHB", function, address, len(values), len(data)) + data


def build_rtu(unit, pdu):
    if not 0 <= unit <= 247 or not 1 <= len(pdu) <= 253:
        raise ValueError("Invalid RTU unit or PDU length")
    frame = bytes((unit,)) + bytes(pdu)
    return frame + struct.pack("<H", crc16(frame))


def build_tcp(transaction, unit, pdu):
    if not 0 <= transaction <= 65535 or not 0 <= unit <= 255 or not 1 <= len(pdu) <= 253:
        raise ValueError("Invalid MBAP header or PDU length")
    return struct.pack(">HHHB", transaction, 0, len(pdu) + 1, unit) + bytes(pdu)


def _tcp_frame(stream, deadline):
    header = read_exact(stream, 7, deadline)
    transaction, protocol, length, unit = struct.unpack(">HHHB", header)
    if protocol != 0 or not 2 <= length <= 254:
        raise ValueError("Invalid Modbus MBAP protocol/length")
    return header + read_exact(stream, length - 1, deadline), transaction, unit


def _serial_stream(port, baudrate, timeout, **options):
    import serial
    return serial.serial_for_url(port, baudrate=baudrate, timeout=min(timeout, .1), write_timeout=timeout, **options)


class ModbusClient:
    def __init__(self, host="127.0.0.1", port=502, unit=1, timeout=2, serial_port=None,
                 baudrate=9600, on_traffic=None, parity="N", stopbits=1, bytesize=8):
        if serial_port is not None and not str(serial_port).strip():
            raise ValueError("RTU requires a serial port")
        if not .05 <= timeout <= 60:
            raise ValueError("Timeout must be .05..60 seconds")
        if not 0 <= unit <= (247 if serial_port else 255):
            raise ValueError("Invalid unit")
        self.host, self.port, self.unit, self.timeout = host, int(port), int(unit), float(timeout)
        self.serial_port, self.baudrate, self.on_traffic = serial_port, int(baudrate), on_traffic
        self._options = dict(parity=parity, stopbits=stopbits, bytesize=bytesize)
        self._lock = threading.RLock()
        self._stream = None
        self._sequence = 0
        self._generation = 0
        self._state_lock = threading.Lock()
        self._closed = False

    def connect(self):
        with self._lock:
            if self._stream is None:
                with self._state_lock:
                    if self._closed:
                        raise OSError("Modbus client is closed; prepare a new connection")
                    generation = self._generation
                stream = _serial_stream(self.serial_port, self.baudrate, self.timeout, **self._options) if self.serial_port else socket.create_connection((self.host, self.port), self.timeout)
                with self._state_lock:
                    if generation != self._generation:
                        stream.close()
                        raise OSError("Connection was cancelled while opening")
                    self._stream = stream
        return self

    def close(self):
        with self._state_lock:
            self._closed = True
            self._generation += 1
            stream, self._stream = self._stream, None
        if stream:
            try:
                if isinstance(stream, socket.socket):
                    stream.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            stream.close()

    stop = close

    def __enter__(self):
        return self.connect()

    def __exit__(self, *_):
        self.close()

    def _exchange(self, pdu):
        with self._lock:
            try:
                self.connect()
                deadline = time.monotonic() + self.timeout
                self._sequence = (self._sequence + 1) & 65535
                frame = build_rtu(self.unit, pdu) if self.serial_port else build_tcp(self._sequence, self.unit, pdu)
                peer = self.serial_port or f"{self.host}:{self.port}"
                stream = self._stream
                if self.serial_port:
                    stream.reset_input_buffer()
                    if stream.write(frame) != len(frame):
                        raise TimeoutError("Incomplete RTU write")
                else:
                    stream.settimeout(self.timeout)
                    stream.sendall(frame)
                notify(self.on_traffic, "TX", frame, peer)
                if self.serial_port:
                    if self.unit == 0:
                        return pdu[:5]  # Broadcast writes have no response.
                    prefix = read_exact(stream, 2, deadline)
                    if prefix[1] & 0x80:
                        response = prefix + read_exact(stream, 3, deadline)
                    elif prefix[1] in (1, 2, 3, 4):
                        size = read_exact(stream, 1, deadline)
                        if size[0] > 250:
                            raise ValueError("RTU response is too large")
                        response = prefix + size + read_exact(stream, size[0] + 2, deadline)
                    elif prefix[1] in (5, 6, 15, 16):
                        response = prefix + read_exact(stream, 6, deadline)
                    else:
                        raise ValueError("Unexpected RTU function")
                    if response[0] != self.unit or crc16(response[:-2]) != struct.unpack("<H", response[-2:])[0]:
                        raise ValueError("RTU unit or CRC mismatch")
                    result = response[1:-2]
                else:
                    response, sequence, unit = _tcp_frame(stream, deadline)
                    if sequence != self._sequence or unit != self.unit:
                        raise ValueError("Modbus transaction or unit mismatch")
                    result = response[7:]
                notify(self.on_traffic, "RX", response, peer)
                if result[0] == pdu[0] | 0x80:
                    if len(result) != 2:
                        raise ValueError("Malformed exception response")
                    raise ModbusError(result[1], pdu[0])
                if result[0] != pdu[0]:
                    raise ValueError("Modbus function mismatch")
                return result
            except ModbusError:
                raise
            except Exception:
                self.close()
                raise

    def read(self, function, address, count=1):
        if function not in (1, 2, 3, 4) or self.serial_port and self.unit == 0:
            raise ValueError("Read needs FC01..04 and a nonbroadcast RTU unit")
        result = self._exchange(build_pdu(function, address, count))
        expected = (count + 7) // 8 if function < 3 else count * 2
        if len(result) != expected + 2 or result[1] != expected:
            self.close()
            raise ValueError("Modbus response byte count mismatch")
        if function < 3:
            return [(result[2 + n // 8] >> (n % 8)) & 1 for n in range(count)]
        return list(struct.unpack(">" + "H" * count, result[2:]))

    def write(self, function, address, values):
        pdu = build_pdu(function, address, values=values)
        result = self._exchange(pdu)
        if len(result) != 5 or result != pdu[:5]:
            self.close()
            raise ValueError("Modbus write acknowledgement mismatch")


def process_pdu(memory, pdu):
    if not pdu:
        raise ValueError("Empty PDU")
    function = pdu[0]
    try:
        if function not in (1, 2, 3, 4, 5, 6, 15, 16):
            raise ModbusError(1, function)
        if len(pdu) < 5:
            raise ModbusError(3, function)
        address, count = struct.unpack(">HH", pdu[1:5])
        if function in (1, 2, 3, 4):
            if len(pdu) != 5 or not 1 <= count <= (2000 if function < 3 else 125):
                raise ModbusError(3, function)
            if address + count > memory.size:
                raise ModbusError(2, function)
            values = memory.read({1: "coils", 2: "discrete", 3: "holding", 4: "input"}[function], address, count)
            if function < 3:
                data = bytearray((count + 7) // 8)
                for n, value in enumerate(values):
                    data[n // 8] |= value << (n % 8)
            else:
                data = struct.pack(">" + "H" * count, *values)
            return bytes((function, len(data))) + data
        if function in (5, 6):
            if len(pdu) != 5 or function == 5 and count not in (0, 0xFF00):
                raise ModbusError(3, function)
            if address >= memory.size:
                raise ModbusError(2, function)
            memory.write("coils" if function == 5 else "holding", address, [int(count == 0xFF00) if function == 5 else count])
        else:
            limit = 1968 if function == 15 else 123
            expected = (count + 7) // 8 if function == 15 else count * 2
            if not 1 <= count <= limit or len(pdu) != 6 + expected or pdu[5] != expected:
                raise ModbusError(3, function)
            if address + count > memory.size:
                raise ModbusError(2, function)
            values = [(pdu[6 + n // 8] >> (n % 8)) & 1 for n in range(count)] if function == 15 else struct.unpack(">" + "H" * count, pdu[6:])
            memory.write("coils" if function == 15 else "holding", address, values)
        return pdu[:5]
    except ModbusError as exc:
        return bytes((function | 0x80, exc.code))


class ModbusServer:
    def __init__(self, memory=None, host="127.0.0.1", port=502, unit=1, serial_port=None,
                 baudrate=9600, on_traffic=None, parity="N", stopbits=1, bytesize=8):
        if serial_port is not None and not str(serial_port).strip():
            raise ValueError("RTU requires a serial port")
        self.memory = memory if memory is not None else Memory()
        self.host, self.port, self.unit = host, int(port), int(unit)
        if not 1 <= self.unit <= (247 if serial_port else 255):
            raise ValueError("Server unit must be a valid nonbroadcast address")
        self.serial_port, self.baudrate, self.on_traffic = serial_port, int(baudrate), on_traffic
        self._options = dict(parity=parity, stopbits=stopbits, bytesize=bytesize)
        self._server = None
        self._stream = None
        self._thread = None
        self._stop = threading.Event()

    @property
    def bound_port(self):
        return self._server.bound_port if self._server else 0

    @property
    def client_count(self):
        return self._server.client_count if self._server else 0

    def start(self):
        if self._server or self._stream:
            raise RuntimeError("Server is already started")
        self._stop.clear()
        if self.serial_port:
            self._stream = _serial_stream(self.serial_port, self.baudrate, 1, **self._options)
            self._thread = threading.Thread(target=self._serve_serial, name="LinkLab RTU slave", daemon=True)
            self._thread.start()
        else:
            self._server = TcpService(self._serve_tcp, self.host, self.port, self.on_traffic).start()
        return self

    def _serve_tcp(self, stream, peer, stop):
        while not stop.is_set():
            frame, transaction, unit = _tcp_frame(stream, time.monotonic() + 300)
            notify(self.on_traffic, "RX", frame, peer)
            response = process_pdu(self.memory, frame[7:]) if unit == self.unit else bytes((frame[7] | 0x80, 11))
            reply = build_tcp(transaction, unit, response)
            stream.settimeout(2)
            stream.sendall(reply)
            notify(self.on_traffic, "TX", reply, peer)

    def _serve_serial(self):
        stream = self._stream
        while not self._stop.is_set():
            try:
                first = stream.read(1)
                if not first:
                    continue
                deadline = time.monotonic() + 2
                function = read_exact(stream, 1, deadline)
                frame = first + function
                if function[0] in (1, 2, 3, 4, 5, 6):
                    frame += read_exact(stream, 6, deadline)
                elif function[0] in (15, 16):
                    header = read_exact(stream, 5, deadline)
                    if header[4] > 246:
                        raise ValueError("RTU byte count is too large")
                    frame += header + read_exact(stream, header[4] + 2, deadline)
                else:
                    stream.reset_input_buffer()
                    continue
                notify(self.on_traffic, "RX", frame, self.serial_port)
                if crc16(frame[:-2]) != struct.unpack("<H", frame[-2:])[0] or first[0] not in (0, self.unit):
                    continue
                if first[0] == 0 and function[0] in (1, 2, 3, 4):
                    continue
                reply = process_pdu(self.memory, frame[1:-2])
                if first[0] != 0:
                    response = build_rtu(self.unit, reply)
                    stream.write(response)
                    notify(self.on_traffic, "TX", response, self.serial_port)
            except (OSError, EOFError, ValueError, TimeoutError) as exc:
                if not self._stop.is_set():
                    notify(self.on_traffic, "ERR", b"", f"{self.serial_port}: {exc}")
                    try:
                        stream.reset_input_buffer()
                    except OSError:
                        break
            except (AttributeError, TypeError) as exc:
                # pyserial URL/OS backends can clear their internal handle while
                # a read is waking during close. This is an expected stop race.
                if not self._stop.is_set():
                    notify(self.on_traffic, "ERR", b"", f"{self.serial_port}: {exc}")
                break

    def stop(self):
        self._stop.set()
        if self._server:
            self._server.stop()
            self._server = None
        stream, self._stream = self._stream, None
        if stream:
            stream.close()
        if self._thread and self._thread is not threading.current_thread():
            self._thread.join(3)

    close = stop

    def __enter__(self):
        return self.start()

    def __exit__(self, *_):
        self.stop()
