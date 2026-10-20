"""Threaded MQTT 3.1.1/5 client powered by Eclipse Paho 2.x.

Only publish payloads and protocol outcomes are reported to callers. CONNECT
credentials, TLS keys and their passwords never enter traffic records/templates.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass, field
import json
from pathlib import Path
import ssl
import threading
import time
import uuid
from typing import Callable
from urllib.parse import urlsplit

import paho.mqtt.client as paho
from paho.mqtt.packettypes import PacketTypes
from paho.mqtt.properties import Properties

MAX_PAYLOAD = 1024 * 1024
MAX_PREVIEW = 65536


def validate_topic(topic: str, *, subscription: bool = False) -> None:
    if not topic or "\0" in topic or len(topic.encode("utf-8")) > 65535:
        raise ValueError("MQTT 主题不能为空，不能包含 NUL，UTF-8 长度不能超过 65535 字节。")
    if not subscription:
        if "+" in topic or "#" in topic:
            raise ValueError("发布和遗嘱主题不能包含 + / # 通配符。")
        return
    levels = topic.split("/")
    for index, level in enumerate(levels):
        if ("+" in level and level != "+") or ("#" in level and (level != "#" or index != len(levels) - 1)):
            raise ValueError("订阅主题中 + 必须占完整一级，# 必须占最后一级。")


def payload_bytes(value: str, format_name: str) -> bytes:
    if format_name == "HEX":
        cleaned = value.replace("0x", "").replace("0X", "")
        for separator in (",", "-", ":", "\n", "\r", "\t"):
            cleaned = cleaned.replace(separator, " ")
        try:
            result = bytes.fromhex(cleaned)
        except ValueError as exc:
            raise ValueError("HEX 负载格式错误，请输入完整字节。") from exc
    else:
        if format_name == "JSON":
            json.loads(value)
        elif format_name != "Text":
            raise ValueError("负载格式不受支持。")
        result = value.encode("utf-8")
    if len(result) > MAX_PAYLOAD:
        raise ValueError("单条调试负载最多 1 MiB。")
    return result


@dataclass(frozen=True)
class MqttSettings:
    host: str = "127.0.0.1"
    port: int = 1883
    client_id: str = field(default_factory=lambda: "LinkLab-" + uuid.uuid4().hex[:8])
    transport: str = "TCP"
    protocol: str = "5.0"
    username: str = ""
    password: str = ""
    keepalive: int = 60
    clean_session: bool = True
    session_expiry: int = 3600
    timeout: float = 5.0
    auto_reconnect: bool = True
    ca_file: str = ""
    cert_file: str = ""
    key_file: str = ""
    key_password: str = ""
    will_topic: str = ""
    will_payload: str = "offline"
    will_qos: int = 0
    will_retain: bool = False

    def endpoint(self) -> tuple[str, int, str]:
        if self.transport in ("WS", "WSS"):
            url = urlsplit(self.host)
            expected = "wss" if self.transport == "WSS" else "ws"
            if url.scheme != expected or not url.hostname or url.username or url.password or url.fragment:
                raise ValueError(f"WebSocket 地址需为完整 {expected}:// URL，认证请使用用户名/密码字段。")
            port = url.port or (443 if expected == "wss" else 80)
            if url.port == 0:
                raise ValueError("WebSocket 端口范围为 1–65535。")
            path = url.path or "/mqtt"
            if url.query:
                path += "?" + url.query
            return url.hostname, port, path
        if not self.host or "://" in self.host or any(ch.isspace() for ch in self.host) or "/" in self.host:
            raise ValueError("TCP / TLS 主机需为 IP 或主机名。")
        return self.host, self.port, ""

    def validate(self) -> None:
        if self.transport not in ("TCP", "TLS", "WS", "WSS") or self.protocol not in ("3.1.1", "5.0"):
            raise ValueError("MQTT 协议或传输不受支持。")
        self.endpoint()
        if type(self.port) is not int or not 1 <= self.port <= 65535:
            raise ValueError("MQTT 端口范围为 1–65535。")
        if not self.client_id or "\0" in self.client_id or len(self.client_id.encode("utf-8")) > 65535:
            raise ValueError("Client ID 不能为空或包含 NUL。")
        if not 5 <= self.keepalive <= 65535 or not 0 <= self.session_expiry <= 604800 or not 0.2 <= self.timeout <= 60:
            raise ValueError("Keep Alive、会话过期或超时范围不正确。")
        if self.will_qos not in (0, 1, 2):
            raise ValueError("遗嘱 QoS 范围为 0–2。")
        if self.will_topic:
            validate_topic(self.will_topic)
            if len(self.will_payload.encode("utf-8")) > 65535:
                raise ValueError("遗嘱负载最多 65535 字节。")
        if bool(self.cert_file) != bool(self.key_file):
            raise ValueError("双向 TLS 需要同时填写客户端 PEM 证书和私钥。")
        for value in (self.ca_file, self.cert_file, self.key_file):
            if value and not Path(value).is_file():
                raise ValueError("证书或私钥文件不存在。")
        if self.transport not in ("TLS", "WSS") and any((self.ca_file, self.cert_file, self.key_file)):
            raise ValueError("证书配置需要 TLS 或 WSS 传输。")

    def safe_dict(self) -> dict:
        values = asdict(self)
        values.pop("password"); values.pop("key_password")
        return values

    @classmethod
    def from_dict(cls, value: dict) -> "MqttSettings":
        if not isinstance(value, dict):
            raise ValueError("MQTT 配置必须是对象。")
        allowed = cls.__dataclass_fields__
        if any(key not in allowed for key in value):
            raise ValueError("MQTT 配置包含未知字段。")
        safe = {key: val for key, val in value.items() if key not in ("password", "key_password")}
        settings = cls(**safe)
        settings.validate()
        return settings


@dataclass(frozen=True)
class MqttMessage:
    timestamp: float
    direction: str
    topic: str
    payload: bytes
    qos: int
    retain: bool
    original_length: int
    mid: int = 0
    status: str = ""


def configured_client(settings: MqttSettings, factory=paho.Client):
    """Build and validate a client without opening a socket."""
    settings.validate()
    kwargs = dict(callback_api_version=paho.CallbackAPIVersion.VERSION2,
        client_id=settings.client_id, protocol=paho.MQTTv5 if settings.protocol == "5.0" else paho.MQTTv311,
        transport="websockets" if settings.transport in ("WS", "WSS") else "tcp",
        reconnect_on_failure=settings.auto_reconnect)
    if settings.protocol == "3.1.1":
        kwargs["clean_session"] = settings.clean_session
    client = factory(**kwargs)
    client.connect_timeout = settings.timeout
    client.max_queued_messages_set(200)
    client.max_inflight_messages_set(20)
    client.reconnect_delay_set(min_delay=1, max_delay=30)
    if settings.username:
        client.username_pw_set(settings.username, settings.password)
    if settings.will_topic:
        client.will_set(settings.will_topic, settings.will_payload.encode("utf-8"), settings.will_qos, settings.will_retain)
    if settings.transport in ("WS", "WSS"):
        client.ws_set_options(path=settings.endpoint()[2])
    if settings.transport in ("TLS", "WSS"):
        context = ssl.create_default_context(cafile=settings.ca_file or None)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        if settings.cert_file:
            context.load_cert_chain(settings.cert_file, settings.key_file, settings.key_password or None)
        client.tls_set_context(context)  # hostname and certificate validation stay enabled.
    return client


class MqttService:
    def __init__(self, *, on_state: Callable[[str, bool], None] | None = None,
                 on_message: Callable[[MqttMessage], None] | None = None,
                 on_ack: Callable[[int, str], None] | None = None):
        self.on_state = on_state
        self.on_message = on_message
        self.on_ack = on_ack
        self._lock = threading.RLock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._client = None
        self._connected = False
        self._running = False
        self._settings: MqttSettings | None = None
        self._subscriptions: dict[str, int] = {}
        self._pending_subscriptions: dict[int, tuple[str, int]] = {}
        self._pending_unsubscribe: dict[int, str] = {}
        self._outgoing_qos: dict[int, int] = {}

    @property
    def connected(self) -> bool:
        return self._connected

    @property
    def active(self) -> bool:
        return self._running

    @property
    def subscriptions(self) -> dict[str, int]:
        with self._lock:
            return dict(self._subscriptions)

    def _state(self, text: str) -> None:
        if self.on_state:
            try:
                self.on_state(text, self.connected)
            except RuntimeError:
                pass  # The Qt receiver may already have been destroyed at shutdown.

    def connect(self, settings: MqttSettings) -> None:
        if self.active or (self._thread is not None and self._thread.is_alive()):
            raise RuntimeError("连接正在运行或清理中，请先断开。")
        client = configured_client(settings)
        self._settings = settings
        self._client = client
        self._stop.clear(); self._connected = False; self._running = True
        self._outgoing_qos.clear()
        client.on_connect = self._on_connect
        client.on_disconnect = self._on_disconnect
        client.on_message = self._on_message
        client.on_publish = self._on_publish
        client.on_subscribe = self._on_subscribe
        client.on_unsubscribe = self._on_unsubscribe
        client.on_connect_fail = lambda *_: self._state("连接失败，等待自动重连…")
        self._thread = threading.Thread(target=self._worker, args=(client, settings), name="LinkLab-MQTT", daemon=True)
        self._state("正在连接 Broker…")
        self._thread.start()

    def _worker(self, client, settings: MqttSettings) -> None:
        delay = 1.0
        try:
            while not self._stop.is_set():
                try:
                    properties = None
                    clean_start = paho.MQTT_CLEAN_START_FIRST_ONLY if settings.clean_session else False
                    if settings.protocol == "5.0":
                        properties = Properties(PacketTypes.CONNECT)
                        properties.SessionExpiryInterval = settings.session_expiry
                    host, port, _ = settings.endpoint()
                    options = {"clean_start": clean_start, "properties": properties} if settings.protocol == "5.0" else {}
                    client.connect(host, port, settings.keepalive, **options)
                    if self._stop.is_set():
                        client.disconnect(); break
                    client.loop_forever(timeout=0.2, retry_first_connection=False)
                    break
                except (OSError, ValueError) as exc:
                    if self._stop.is_set():
                        break
                    self._state(f"连接失败：{exc}" + (" · 稍后重试" if settings.auto_reconnect else ""))
                    if not settings.auto_reconnect or self._stop.wait(delay):
                        break
                    delay = min(30.0, delay * 2)
        finally:
            self._connected = False; self._running = False
            try:
                client.disconnect()
            except (OSError, ValueError):
                pass
            self._state("已断开连接。")

    def _on_connect(self, client, userdata, flags, reason_code, properties) -> None:
        if self._stop.is_set():
            client.disconnect(); return
        if reason_code.is_failure:
            self._connected = False; self._state(f"Broker 拒绝连接：{reason_code}")
            if self._settings is not None and not self._settings.auto_reconnect:
                client.disconnect()
            return
        self._connected = True
        with self._lock:
            subscriptions = dict(self._subscriptions)
            self._pending_subscriptions.clear(); self._pending_unsubscribe.clear()
            for topic, qos in subscriptions.items():
                rc, mid = client.subscribe(topic, qos)
                if rc == paho.MQTT_ERR_SUCCESS:
                    self._pending_subscriptions[mid] = (topic, qos)
        self._state("已连接 Broker · 订阅将在收到 SUBACK 后确认。")

    def _on_disconnect(self, client, userdata, flags, reason_code, properties) -> None:
        self._connected = False
        reconnecting = not self._stop.is_set() and self._settings is not None and self._settings.auto_reconnect
        self._state("连接已断开，等待重连…" if reconnecting else "已断开连接。")

    def _on_message(self, client, userdata, message) -> None:
        if self.on_message:
            try:
                self.on_message(MqttMessage(time.time(), "RX", message.topic, bytes(message.payload[:MAX_PREVIEW]), message.qos,
                    bool(message.retain), len(message.payload), message.mid, "已接收"))
            except RuntimeError:
                pass

    def _on_publish(self, client, userdata, mid, reason_code, properties) -> None:
        with self._lock:
            qos = self._outgoing_qos.pop(mid, 0)
            if self.on_ack:
                try:
                    self.on_ack(mid, f"失败：{reason_code}" if reason_code.is_failure else ("Broker 已确认" if qos else "已发送（QoS 0 无应答）"))
                except RuntimeError:
                    pass

    def _on_subscribe(self, client, userdata, mid, reasons, properties) -> None:
        with self._lock:
            subscription = self._pending_subscriptions.pop(mid, None)
            if subscription is None:
                return
            if any(reason.is_failure for reason in reasons):
                self._state(f"Broker 拒绝订阅 {subscription[0]}"); return
            self._subscriptions[subscription[0]] = subscription[1]
        self._state(f"订阅已确认：{subscription[0]}")

    def _on_unsubscribe(self, client, userdata, mid, reasons, properties) -> None:
        with self._lock:
            topic = self._pending_unsubscribe.pop(mid, None)
            if topic is None:
                return
            if any(reason.is_failure for reason in reasons):
                self._state(f"Broker 拒绝取消订阅 {topic}"); return
            self._subscriptions.pop(topic, None)
        self._state(f"已取消订阅：{topic}")

    def subscribe(self, topic: str, qos: int = 0) -> None:
        validate_topic(topic, subscription=True)
        if qos not in (0, 1, 2):
            raise ValueError("QoS 范围为 0–2。")
        if not self.connected or self._client is None:
            raise RuntimeError("请先连接 Broker。")
        with self._lock:
            rc, mid = self._client.subscribe(topic, qos)
            if rc != paho.MQTT_ERR_SUCCESS:
                raise RuntimeError(paho.error_string(rc))
            self._pending_subscriptions[mid] = (topic, qos)
        self._state(f"订阅请求已发送：{topic}")

    def unsubscribe(self, topic: str) -> None:
        validate_topic(topic, subscription=True)
        if not self.connected or self._client is None:
            raise RuntimeError("请先连接 Broker。")
        with self._lock:
            rc, mid = self._client.unsubscribe(topic)
            if rc != paho.MQTT_ERR_SUCCESS:
                raise RuntimeError(paho.error_string(rc))
            self._pending_unsubscribe[mid] = topic

    def set_subscriptions(self, subscriptions: dict[str, int]) -> None:
        if self.active:
            raise RuntimeError("请断开后载入订阅模板。")
        for topic, qos in subscriptions.items():
            validate_topic(topic, subscription=True)
            if qos not in (0, 1, 2):
                raise ValueError("订阅 QoS 范围为 0–2。")
        with self._lock:
            self._subscriptions = dict(subscriptions)

    def publish(self, topic: str, payload: bytes, qos: int = 0, retain: bool = False) -> int:
        validate_topic(topic)
        if qos not in (0, 1, 2) or len(payload) > MAX_PAYLOAD:
            raise ValueError("QoS 必须为 0–2，单条调试负载最多 1 MiB。")
        if not self.connected or self._client is None:
            raise RuntimeError("请先连接 Broker。")
        with self._lock:
            info = self._client.publish(topic, payload, qos, retain)
            if info.rc != paho.MQTT_ERR_SUCCESS:
                raise RuntimeError(paho.error_string(info.rc))
            self._outgoing_qos[info.mid] = qos
            if self.on_message:
                self.on_message(MqttMessage(time.time(), "TX", topic, bytes(payload[:MAX_PREVIEW]), qos, retain, len(payload), info.mid, "已入队"))
            return info.mid

    def disconnect(self, wait: float = 0.0) -> None:
        self._stop.set()
        if self._client is not None:
            try:
                self._client.disconnect()
            except (OSError, ValueError):
                pass
        thread = self._thread
        if wait and thread is not None and thread is not threading.current_thread():
            thread.join(wait)
        self._connected = False
        self._state("正在断开…" if self.active else "已断开连接。")

    def close(self) -> None:
        self.disconnect(wait=0.5)
