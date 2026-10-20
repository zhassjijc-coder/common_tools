"""ZLAN UDP management protocol, independently implemented from the public spec.

Reference: https://www.zlmcu.com/download/UDP_port_protocol.pdf
No vendor library, embedded SDK key or device-changing operation runs on import.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
import ipaddress
import json
import select
import socket
import struct
import sys
import threading
import time
from typing import Callable

PARAMETER_LENGTH = 167
PACKET_LENGTH = 170
MANAGEMENT_PORT = 1092
MODES = ("TCP Server", "TCP Client", "UDP", "UDP Multicast")
PARITIES = ("None", "Even", "Odd", "Mark", "Space")
BAUD_RATES = (1200, 2400, 4800, 7200, 9600, 14400, 19200, 28800, 38400, 57600, 76800, 115200, 230400, 460800)
APP_PROTOCOLS = ("透明传输", "Modbus TCP ↔ RTU", "RealCom")
FLOW_CONTROLS = ("None", "CTS / RTS", "DTR / DSR", "XON / XOFF")


def ipv4(value: str, label: str = "IP", *, allow_zero: bool = False, allow_broadcast: bool = False) -> str:
    try:
        result = ipaddress.IPv4Address(value)
    except (ValueError, TypeError) as exc:
        raise ValueError(f"{label}必须是完整的 IPv4 地址，不能省略字段或使用前导零。") from exc
    allowed_special = (allow_zero and int(result) == 0) or (allow_broadcast and int(result) == 0xFFFFFFFF)
    if ((result.is_multicast or result.is_reserved) and not allowed_special) or (not allow_zero and int(result) == 0):
        raise ValueError(f"{label}必须是单播 IPv4 地址。")
    if int(result) >> 24 == 0 and int(result) != 0:
        raise ValueError(f"{label}不能使用保留的 0.0.0.0/8 地址。")
    return str(result)


@dataclass(frozen=True)
class DeviceSettings:
    name: str = ""
    address: str = "192.168.1.200"
    mask: str = "255.255.255.0"
    gateway: str = "192.168.1.1"
    local_port: int = 4196
    destination: str = "192.168.1.100"
    destination_port: int = 4196
    mode: int = 0
    baud: int = 9600
    parity: int = 0
    dhcp: bool = False
    data_bits: int = 8
    stop_bits: int = 1
    flow_control: int = 0
    app_protocol: int = 0

    def validate(self) -> None:
        if not isinstance(self.name, str) or len(self.name) > 9 or any(not 32 <= ord(ch) <= 126 for ch in self.name):
            raise ValueError("设备名最多 9 个 ASCII 可见字符。")
        address = ipv4(self.address, "设备 IP", allow_zero=self.dhcp)
        try:
            network = ipaddress.IPv4Network(f"{address}/{self.mask}", strict=False)
        except ValueError as exc:
            raise ValueError("子网掩码必须为连续的 1 和 0。") from exc
        if str(network.netmask) != self.mask or network.prefixlen == 0:
            raise ValueError("子网掩码必须为完整有效的 IPv4 掩码。")
        if not self.dhcp and network.prefixlen < 31 and ipaddress.IPv4Address(address) in (network.network_address, network.broadcast_address):
            raise ValueError("静态 IP 不能是子网的网络地址或广播地址。")
        gateway = ipv4(self.gateway, "网关", allow_zero=True)
        if gateway != "0.0.0.0" and ipaddress.IPv4Address(gateway) not in network:
            raise ValueError("网关需位于同一子网，或设置为 0.0.0.0。")
        if any(type(value) is not int or not 1 <= value <= 65535 for value in (self.local_port, self.destination_port)):
            raise ValueError("网络端口范围为 1–65535。")
        if self.mode not in range(4) or self.parity not in range(5) or self.app_protocol not in range(3):
            raise ValueError("工作模式、校验或转化协议不受支持。")
        if self.baud not in BAUD_RATES or self.data_bits not in (5, 6, 7, 8) or self.stop_bits not in (1, 2) or self.flow_control not in range(4):
            raise ValueError("串口参数不受该管理协议支持。")
        if not isinstance(self.destination, str) or not 1 <= len(self.destination) <= 29 or any(not 33 <= ord(ch) <= 126 for ch in self.destination):
            raise ValueError("目标地址需为 IPv4 或不超过 29 位 ASCII 字符的域名。")
        if all(ch.isdigit() or ch == "." for ch in self.destination):
            try:
                destination = ipaddress.IPv4Address(self.destination)
            except ValueError as exc:
                raise ValueError("目标 IPv4 地址格式不正确。") from exc
            if destination.is_multicast and self.mode != 3:
                raise ValueError("组播目的地址需要 UDP Multicast 模式。")
        else:
            labels = self.destination.rstrip(".").split(".")
            if any(not label or len(label) > 63 or label.startswith("-") or label.endswith("-") or not all(ch.isalnum() or ch == "-" for ch in label) for label in labels):
                raise ValueError("目标域名格式不正确。")
        if type(self.dhcp) is not bool:
            raise ValueError("DHCP 参数必须为布尔值。")


@dataclass(frozen=True)
class DeviceSnapshot:
    identity: str
    settings: DeviceSettings
    parameters: bytes
    peer: tuple[str, int]
    seen: float

    @property
    def writable(self) -> bool:
        return len(self.parameters) == PARAMETER_LENGTH

    @property
    def supports_dns(self) -> bool:
        return len(self.parameters) > 104 and bool(self.parameters[104] & 2)

    @property
    def supports_dhcp(self) -> bool:
        return len(self.parameters) > 104 and bool(self.parameters[104] & 32)

    @property
    def password_required(self) -> bool:
        return len(self.parameters) > 110 and bool(self.parameters[110] & 4)


def query_packet(broadcast: bool = False) -> bytes:
    return b"ZL" + bytes((0 if broadcast else 4,)) + bytes(PARAMETER_LENGTH)


def parse_response(packet: bytes, peer: tuple[str, int]) -> DeviceSnapshot:
    if not 118 <= len(packet) <= 4096 or packet[:3] != b"ZL\x01":
        raise ValueError("不是受支持的卓岚参数应答。")
    p = bytes(packet[3:])
    if p[31:37] in (bytes(6), bytes((255,)) * 6):
        raise ValueError("设备应答缺少有效 DevID。")
    identity = ":".join(f"{value:02X}" for value in p[31:37])
    if p[100]:
        identity += f" / P{p[100]}"
    address = lambda offset: str(ipaddress.IPv4Address(p[offset:offset + 4]))
    text = lambda offset, length: p[offset:offset + length].split(b"\0", 1)[0].decode("ascii", "replace").rstrip()
    destination = text(66, 30) if p[104] & 2 else ""
    destination = destination or address(12)
    if p[20] == 3:
        destination = address(105)
    settings = DeviceSettings(text(38, 10), address(0), address(4), address(8), struct.unpack_from("!H", p, 16)[0],
        destination, struct.unpack_from("!H", p, 18)[0], p[20], BAUD_RATES[p[37]] if p[37] < len(BAUD_RATES) else 0,
        p[48], bool(p[56]), 8 - p[59], 2 if p[114] & 1 else 1, p[57], p[60])
    return DeviceSnapshot(identity, settings, p, peer, time.time())


def write_packet(original: DeviceSnapshot, settings: DeviceSettings, password: str = "") -> bytes:
    settings.validate()
    if not original.writable:
        raise ValueError("当前设备参数长度与已验证的 167 字节格式不同，仅支持读取。")
    parsed = parse_response(b"ZL\x01" + original.parameters, original.peer)
    if parsed.identity != original.identity or parsed.settings != original.settings:
        raise ValueError("设备快照与原始参数不一致，拒绝写入。")
    if settings.dhcp and not original.settings.dhcp and not original.supports_dhcp:
        raise ValueError("设备未声明支持 DHCP。")
    try:
        destination = ipaddress.IPv4Address(settings.destination)
    except ValueError:
        destination = None
    if destination is None and not original.supports_dns:
        raise ValueError("设备不支持 DNS，请使用 IPv4 目标地址。")
    if settings.mode == 3 and (settings.mode != original.settings.mode or settings.destination != original.settings.destination):
        if not original.parameters[112] & 2 or destination is None or not destination.is_multicast:
            raise ValueError("组播模式需要设备声明支持，且目标为 224.0.0.0–239.255.255.255。")
    if settings.app_protocol != original.settings.app_protocol:
        if (settings.app_protocol == 1 and not original.parameters[104] & 8) or (settings.app_protocol == 2 and not original.parameters[104] & 4):
            raise ValueError("设备未声明支持所选转化协议。")
    p = bytearray(original.parameters)
    def set_ip(offset: int, value: str) -> None:
        p[offset:offset + 4] = ipaddress.IPv4Address(value).packed
    def set_text(offset: int, size: int, value: str) -> None:
        raw = value.encode("ascii")
        p[offset:offset + size] = raw + bytes(size - len(raw))
    set_ip(0, settings.address); set_ip(4, settings.mask); set_ip(8, settings.gateway)
    if settings.destination != original.settings.destination or settings.mode != original.settings.mode:
        if settings.mode == 3:
            set_ip(105, settings.destination)
        else:
            if destination is not None:
                set_ip(12, settings.destination)
            if original.supports_dns:
                set_text(66, 30, settings.destination)
    struct.pack_into("!HH", p, 16, settings.local_port, settings.destination_port)
    p[20], p[37] = settings.mode, BAUD_RATES.index(settings.baud)
    if settings.name != original.settings.name:
        set_text(38, 10, settings.name)
    p[48], p[57], p[59], p[60] = settings.parity, settings.flow_control, 8 - settings.data_bits, settings.app_protocol
    if settings.dhcp != original.settings.dhcp:
        p[56] = int(settings.dhcp)
    p[114] = (p[114] & 0xFE) | (settings.stop_bits == 2)
    if original.password_required:
        if not 1 <= len(password) <= 9 or any(not 32 <= ord(ch) <= 126 for ch in password):
            raise ValueError("请输入 1–9 位 ASCII 字符的设备配置密码。")
        set_text(21, 10, password)
    return b"ZL\x02" + bytes(p)


def redact_packet(packet: bytes) -> bytes:
    result = bytearray(packet)
    if len(result) >= 34:
        result[24:34] = bytes(10)
    if len(result) > 118:
        result[118:] = bytes(len(result) - 118)
    return bytes(result)


def matches(readback: DeviceSnapshot, expected: DeviceSnapshot, requested: DeviceSettings) -> bool:
    read_settings, wanted = asdict(readback.settings), asdict(requested)
    if requested.dhcp:
        for key in ("address", "mask", "gateway"):
            read_settings.pop(key); wanted.pop(key)
    return readback.identity == expected.identity and read_settings == wanted


def export_profile(settings: DeviceSettings) -> str:
    settings.validate()
    return json.dumps({"format_version": 1, "settings": asdict(settings)}, ensure_ascii=False, indent=2)


def import_profile(text: str) -> DeviceSettings:
    if len(text.encode("utf-8")) > 65536:
        raise ValueError("参数模板文件过大。")
    value = json.loads(text)
    if not isinstance(value, dict) or value.get("format_version") != 1 or not isinstance(value.get("settings"), dict):
        raise ValueError("参数模板格式不受支持。")
    allowed = DeviceSettings.__dataclass_fields__
    if any(key not in allowed for key in value["settings"]):
        raise ValueError("参数模板包含不受支持的字段。")
    result = DeviceSettings(**value["settings"])
    result.validate()
    return result


class OperationCancelled(Exception):
    pass


class ZlanClient:
    def __init__(self, port: int = MANAGEMENT_PORT, on_traffic: Callable[[str, bytes, str], None] | None = None):
        if not 1 <= port <= 65535:
            raise ValueError("管理端口范围为 1–65535。")
        self.port = port
        self.on_traffic = on_traffic

    @staticmethod
    def _cancel(cancel: threading.Event | None) -> None:
        if cancel is not None and cancel.is_set():
            raise OperationCancelled("操作已取消。")

    def _log(self, direction: str, packet: bytes, peer: tuple[str, int]) -> None:
        if self.on_traffic:
            self.on_traffic(direction, redact_packet(packet), f"{peer[0]}:{peer[1]} · 认证及扩展字段已隐藏")

    @staticmethod
    def _socket(bind: str, broadcast: bool) -> socket.socket:
        ipv4(bind, "本机网卡", allow_zero=True)
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.bind(("0.0.0.0" if broadcast else bind, 0))
            sock.setblocking(False)
            return sock
        except Exception:
            sock.close()
            raise

    @staticmethod
    def _send(sock: socket.socket, packet: bytes, peer: tuple[str, int], bind: str, broadcast: bool) -> None:
        if broadcast and bind != "0.0.0.0" and sys.platform.startswith("linux") and hasattr(sock, "sendmsg"):
            # Linux IP_PKTINFO chooses the outgoing source/interface while the
            # socket stays bound to ANY to receive cross-subnet broadcast replies.
            pktinfo = struct.pack("=I4s4s", 0, socket.inet_aton(bind), bytes(4))
            sock.sendmsg([packet], [(socket.IPPROTO_IP, getattr(socket, "IP_PKTINFO", 8), pktinfo)], 0, peer)
        else:
            sock.sendto(packet, peer)

    def discover(self, bind: str = "0.0.0.0", target: str = "255.255.255.255", *, broadcast: bool = True,
                 timeout: float = 2.0, cancel: threading.Event | None = None) -> list[DeviceSnapshot]:
        self._cancel(cancel)
        if not 0.1 <= timeout <= 30:
            raise ValueError("超时范围为 0.1–30 秒。")
        peer = (ipv4(target, "查询 IP", allow_broadcast=broadcast), self.port)
        packet = query_packet(broadcast)
        result: dict[str, DeviceSnapshot] = {}
        end = time.monotonic() + timeout
        with self._socket(bind, broadcast) as sock:
            self._send(sock, packet, peer, bind, broadcast); self._log("TX", packet, peer)
            while time.monotonic() < end and len(result) < 256:
                self._cancel(cancel)
                if not select.select([sock], [], [], min(0.1, max(0, end - time.monotonic())))[0]:
                    continue
                try:
                    raw, source = sock.recvfrom(4097)
                except (BlockingIOError, ConnectionResetError, ConnectionRefusedError):
                    continue
                if source[1] != self.port or (not broadcast and source[0] != peer[0]):
                    continue
                try:
                    device = parse_response(raw, source)
                except ValueError:
                    continue
                result[device.identity] = device; self._log("RX", raw, source)
        self._cancel(cancel)
        return sorted(result.values(), key=lambda device: (ipaddress.IPv4Address(device.settings.address), device.identity))

    def read(self, expected: DeviceSnapshot, *, bind: str = "0.0.0.0", timeout: float = 2.0,
             cancel: threading.Event | None = None) -> DeviceSnapshot:
        for device in self.discover(bind, expected.peer[0], broadcast=False, timeout=timeout, cancel=cancel):
            if device.identity == expected.identity:
                return device
        raise TimeoutError("未收到目标 DevID / 通道的设备应答，请重新搜索并检查网络。")

    def apply(self, expected: DeviceSnapshot, settings: DeviceSettings, *, password: str = "", bind: str = "0.0.0.0",
              timeout: float = 2.0, verify_timeout: float = 6.0, cancel: threading.Event | None = None) -> DeviceSnapshot:
        settings.validate()
        current = self.read(expected, bind=bind, timeout=timeout, cancel=cancel)
        if current.settings != expected.settings:
            raise ValueError("设备参数已被其他操作修改，请重新读取后核对。")
        packet = write_packet(current, settings, password)
        self._cancel(cancel)
        with self._socket(bind, False) as sock:
            self._send(sock, packet, (current.peer[0], self.port), bind, False); self._log("TX", packet, current.peer)
        # 0x02 has no documented acknowledgement and restarts the device. Only
        # a subsequent same-identity parameter response can confirm success.
        end = time.monotonic() + max(0.5, verify_timeout)
        candidates = tuple(dict.fromkeys((settings.address, current.peer[0])))
        while time.monotonic() < end:
            if cancel is not None:
                if cancel.wait(0.25):
                    self._cancel(cancel)
            else:
                time.sleep(0.25)
            for address in candidates:
                if address == "0.0.0.0":
                    continue
                try:
                    devices = self.discover(bind, address, broadcast=False, timeout=min(timeout, 0.6), cancel=cancel)
                except OSError:
                    continue
                for device in devices:
                    if matches(device, current, settings):
                        return device
            if settings.dhcp:
                for device in self.discover(bind, "255.255.255.255", broadcast=True, timeout=min(timeout, 0.6), cancel=cancel):
                    if matches(device, current, settings):
                        return device
        raise TimeoutError("配置已发送，但尚未回读同一设备并确认参数。请重新搜索核对，勿连续重复写入。")
