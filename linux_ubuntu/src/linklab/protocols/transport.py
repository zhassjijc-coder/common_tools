"""Bounded synchronous framing and stoppable serial/socket transports."""
from __future__ import annotations

import socket
import threading
import time


def notify(callback, *args):
    if callback:
        try:
            callback(*args)
        except Exception:
            pass  # UI callbacks must not kill a device worker.


def read_exact(stream, count: int, deadline: float | None = None) -> bytes:
    if count < 0 or count > 1_048_576:
        raise ValueError("Invalid frame length")
    result = bytearray()
    while len(result) < count:
        remaining = None if deadline is None else deadline - time.monotonic()
        if remaining is not None and remaining <= 0:
            raise TimeoutError("Timed out waiting for a complete frame")
        if isinstance(stream, socket.socket):
            if remaining is not None:
                stream.settimeout(remaining)
            chunk = stream.recv(count - len(result))
            if not chunk:
                raise EOFError("Peer closed the connection")
        else:
            chunk = stream.read(count - len(result))
            if not chunk and getattr(stream, "is_open", True) is False:
                raise EOFError("Serial port closed")
        result.extend(chunk)
    return bytes(result)


class TcpService:
    """A bounded multi-client listener. stop closes accepted sockets before joining."""
    def __init__(self, handler, host="127.0.0.1", port=0, on_traffic=None):
        self.handler, self.host, self.port = handler, host, int(port)
        self.on_traffic = on_traffic
        self.bound_port = 0
        self._stop = threading.Event()
        self._lock = threading.RLock()
        self._clients: set[socket.socket] = set()
        self._workers: set[threading.Thread] = set()
        self._listener = None
        self._thread = None

    @property
    def client_count(self):
        with self._lock:
            return len(self._clients)

    def start(self):
        if self._listener:
            raise RuntimeError("Server is already running")
        self._stop.clear()
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind((self.host, self.port))
            listener.listen(16)
            listener.settimeout(.2)
        except Exception:
            listener.close()
            raise
        self._listener = listener
        self.bound_port = listener.getsockname()[1]
        self._thread = threading.Thread(target=self._accept, name="LinkLab accept", daemon=True)
        self._thread.start()
        return self

    def _accept(self):
        listener = self._listener
        while not self._stop.is_set():
            try:
                client, address = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            client.settimeout(1)
            with self._lock:
                if self._stop.is_set() or len(self._clients) >= 64:
                    client.close()
                    continue
                self._clients.add(client)
                worker = threading.Thread(target=self._serve, args=(client, address), name="LinkLab peer", daemon=True)
                self._workers.add(worker)
                worker.start()

    def _serve(self, client, address):
        peer = f"{address[0]}:{address[1]}"
        try:
            self.handler(client, peer, self._stop)
        except (OSError, EOFError, ValueError, TimeoutError) as exc:
            if not self._stop.is_set():
                notify(self.on_traffic, "ERR", b"", f"{peer}: {exc}")
        finally:
            client.close()
            with self._lock:
                self._clients.discard(client)
                self._workers.discard(threading.current_thread())

    def stop(self):
        self._stop.set()
        listener, self._listener = self._listener, None
        if listener:
            listener.close()
        with self._lock:
            clients, workers = list(self._clients), list(self._workers)
        for client in clients:
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            client.close()
        if self._thread and self._thread is not threading.current_thread():
            self._thread.join(2)
        for worker in workers:
            if worker is not threading.current_thread():
                worker.join(2)
        self.bound_port = 0

    close = stop


class SerialTransport:
    def __init__(self, port, baudrate=9600, parity="N", stopbits=1, bytesize=8,
                 on_data=None, on_state=None, **kwargs):
        self.port, self.baudrate, self.parity = port, int(baudrate), parity
        self.stopbits, self.bytesize = stopbits, int(bytesize)
        self.on_data, self.on_state = on_data, on_state
        self._stream = None
        self._stop = threading.Event()
        self._thread = None
        self._write_lock = threading.Lock()

    def start(self):
        if self._stream:
            raise RuntimeError("Serial port is already open")
        import serial
        stream = serial.serial_for_url(self.port, baudrate=self.baudrate, parity=self.parity,
                                       stopbits=self.stopbits, bytesize=self.bytesize, timeout=.1, write_timeout=2)
        self._stream = stream
        self._stop.clear()
        self._thread = threading.Thread(target=self._receive, name="LinkLab serial", daemon=True)
        self._thread.start()
        notify(self.on_state, f"Connected: {self.port}")
        return self

    def _receive(self):
        stream = self._stream
        try:
            while not self._stop.is_set():
                data = stream.read(max(1, min(stream.in_waiting, 65536)))
                if data:
                    notify(self.on_data, data, self.port)
        except (OSError, ValueError, AttributeError, TypeError) as exc:
            if not self._stop.is_set():
                notify(self.on_state, f"Error: {exc}")

    def send(self, data: bytes, peer=None):
        if not self._stream or self._stop.is_set():
            raise RuntimeError("Serial port is closed")
        with self._write_lock:
            written = self._stream.write(data)
            if written != len(data):
                raise TimeoutError("Incomplete serial write")

    def stop(self):
        self._stop.set()
        stream, self._stream = self._stream, None
        if stream:
            stream.close()
        if self._thread and self._thread is not threading.current_thread():
            self._thread.join(2)
        notify(self.on_state, "Disconnected")

    close = stop


class NetworkTransport:
    def __init__(self, mode="TCP Client", host="127.0.0.1", port=9000,
                 local_host="0.0.0.0", local_port=0, on_data=None, on_state=None, timeout=2, **kwargs):
        if mode not in ("TCP Client", "TCP Server", "UDP"):
            raise ValueError("Mode must be TCP Client, TCP Server or UDP")
        self.mode, self.host, self.port = mode, host, int(port)
        self.local_host, self.local_port = local_host, int(local_port)
        self.on_data, self.on_state, self.timeout = on_data, on_state, float(timeout)
        self.bound_port = 0
        self._stop = threading.Event()
        self._socket = None
        self._server = None
        self._thread = None
        self._lock = threading.RLock()
        self._peers = {}

    @property
    def peers(self):
        with self._lock:
            return list(self._peers)

    def start(self):
        if self._socket or self._server:
            raise RuntimeError("Transport is already started")
        self._stop.clear()
        if self.mode == "TCP Server":
            self._server = TcpService(self._serve, self.local_host, self.local_port).start()
            self.bound_port = self._server.bound_port
        else:
            stream = socket.socket(socket.AF_INET, socket.SOCK_DGRAM if self.mode == "UDP" else socket.SOCK_STREAM)
            try:
                stream.settimeout(self.timeout)
                if self.mode == "UDP":
                    stream.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                    stream.bind((self.local_host, self.local_port))
                else:
                    if self.local_port or self.local_host not in ("", "0.0.0.0"):
                        stream.bind((self.local_host, self.local_port))
                    stream.connect((self.host, self.port))
                stream.settimeout(.2)
            except Exception:
                stream.close()
                raise
            self._socket = stream
            self.bound_port = stream.getsockname()[1]
            self._thread = threading.Thread(target=self._receive, name="LinkLab network", daemon=True)
            self._thread.start()
        notify(self.on_state, f"Started {self.mode}: {self.bound_port}")
        return self

    def _serve(self, stream, peer, stop):
        with self._lock:
            self._peers[peer] = stream
        notify(self.on_state, f"Connected: {peer}")
        try:
            while not stop.is_set():
                try:
                    data = stream.recv(65536)
                except socket.timeout:
                    continue
                if not data:
                    break
                notify(self.on_data, data, peer)
        finally:
            with self._lock:
                self._peers.pop(peer, None)
            notify(self.on_state, f"Disconnected: {peer}")

    def _receive(self):
        stream = self._socket
        while not self._stop.is_set():
            try:
                if self.mode == "UDP":
                    data, address = stream.recvfrom(65507)
                    peer = f"{address[0]}:{address[1]}"
                else:
                    data = stream.recv(65536)
                    if not data:
                        break
                    peer = f"{self.host}:{self.port}"
                notify(self.on_data, data, peer)
            except socket.timeout:
                continue
            except OSError as exc:
                if not self._stop.is_set():
                    notify(self.on_state, f"Error: {exc}")
                break
        if not self._stop.is_set():
            notify(self.on_state, "Peer disconnected")

    def send(self, data: bytes, peer=None):
        if len(data) > 1_048_576:
            raise ValueError("Send payload exceeds 1 MiB")
        if self.mode == "TCP Server":
            with self._lock:
                targets = [self._peers[peer]] if peer in self._peers else list(self._peers.values()) if peer is None else []
            if not targets:
                raise RuntimeError("No connected client")
            for stream in targets:
                stream.sendall(data)
        elif not self._socket:
            raise RuntimeError("Transport is stopped")
        elif self.mode == "UDP":
            if len(data) > 65507:
                raise ValueError("UDP payload exceeds 65507 bytes")
            destination = (self.host, self.port) if peer is None else peer
            if isinstance(destination, str):
                ip, port = destination.rsplit(":", 1)
                destination = ip, int(port)
            self._socket.sendto(data, destination)
        else:
            with self._lock:
                self._socket.sendall(data)

    def stop(self):
        self._stop.set()
        if self._server:
            self._server.stop()
            self._server = None
        stream, self._socket = self._socket, None
        if stream:
            try:
                stream.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            stream.close()
        if self._thread and self._thread is not threading.current_thread():
            self._thread.join(2)
        self.bound_port = 0
        notify(self.on_state, "Stopped")

    close = stop
