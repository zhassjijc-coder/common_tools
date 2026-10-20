"""Byte encodings and checksums; no Qt or device dependencies."""
from __future__ import annotations

import re
import struct
import zlib
from functools import reduce
from operator import xor


def encode(text: str, mode: str = "HEX") -> bytes:
    mode = mode.upper().replace("-", "")
    if mode == "HEX":
        cleaned = re.sub(r"0[xX]", "", text)
        if re.search(r"[^0-9a-fA-F\s,:;_-]", cleaned):
            raise ValueError("HEX contains an invalid character")
        cleaned = re.sub(r"[\s,:;_-]", "", cleaned)
        if len(cleaned) % 2:
            raise ValueError("HEX needs two digits per byte")
        return bytes.fromhex(cleaned)
    if mode not in ("ASCII", "UTF8"):
        raise ValueError("Encoding must be HEX, ASCII or UTF-8")
    return text.encode("ascii" if mode == "ASCII" else "utf-8")


def decode(data: bytes, mode: str = "HEX") -> str:
    mode = mode.upper().replace("-", "")
    if mode == "HEX":
        return bytes(data).hex(" ").upper()
    if mode not in ("ASCII", "UTF8"):
        raise ValueError("Encoding must be HEX, ASCII or UTF-8")
    return bytes(data).decode("ascii" if mode == "ASCII" else "utf-8", errors="replace")


def crc16(data: bytes) -> int:
    result = 0xFFFF
    for byte in data:
        result ^= byte
        for _ in range(8):
            result = (result >> 1) ^ (0xA001 if result & 1 else 0)
    return result


def checksum(data: bytes, kind: str = "CRC16") -> bytes:
    kind = kind.upper().replace("-", "").replace("_", "").replace(" ", "")
    if kind in ("NONE", "无", ""):
        return b""
    if kind in ("CRC16", "CRC16MODBUS", "MODBUSCRC16"):
        return struct.pack("<H", crc16(data))
    if kind == "CRC32":
        return struct.pack("<I", zlib.crc32(data))
    if kind in ("SUM", "SUM8"):
        return bytes((sum(data) & 255,))
    if kind in ("XOR", "XOR8"):
        return bytes((reduce(xor, data, 0),))
    if kind == "LRC":
        return bytes((-sum(data) & 255,))
    raise ValueError("Unknown checksum")


_TYPES = {"Bool": "?", "UInt8": "B", "Int8": "b", "UInt16": "H", "Int16": "h",
          "UInt32": "I", "Int32": "i", "Float32": "f", "Float": "f", "UInt64": "Q",
          "Int64": "q", "Float64": "d", "Double": "d"}


def type_size(data_type: str) -> int:
    try:
        return struct.calcsize(">" + _TYPES[data_type])
    except KeyError as exc:
        raise ValueError(f"Unsupported type: {data_type}") from exc


def reorder(data: bytes, byte_order: str = "ABCD") -> bytes:
    """Self-inverse register ordering, matching the Windows implementation."""
    order = byte_order.upper()
    if order not in ("ABCD", "BADC", "CDAB", "DCBA"):
        raise ValueError("Byte order must be ABCD, BADC, CDAB or DCBA")
    result = bytearray(data)
    if len(result) <= 1:
        return bytes(result)
    if len(result) % 2:
        raise ValueError("Register data needs an even byte length")
    if order in ("BADC", "DCBA"):
        for n in range(0, len(result), 2):
            result[n], result[n + 1] = result[n + 1], result[n]
    if order in ("CDAB", "DCBA") and len(result) > 2:
        words = [result[n:n + 2] for n in range(0, len(result), 2)]
        result = bytearray().join(reversed(words))
    return bytes(result)


def _number(value, data_type):
    if data_type == "Bool":
        if isinstance(value, str):
            if value.lower().strip() in ("true", "on", "1"):
                return True
            if value.lower().strip() in ("false", "off", "0"):
                return False
            raise ValueError("Bool must be 0/1, true/false or on/off")
        if value not in (0, 1, False, True):
            raise ValueError("Bool must be 0 or 1")
        return bool(value)
    if _TYPES.get(data_type) in ("f", "d"):
        return float(value)
    if isinstance(value, float) and not value.is_integer():
        raise ValueError("Integer data types do not accept fractional values")
    return int(value, 16 if value.lower().startswith(("0x", "-0x")) else 10) if isinstance(value, str) else int(value)


def pack_values(values, data_type: str = "UInt16", byte_order: str = "ABCD") -> bytes:
    type_size(data_type)
    if isinstance(values, (str, int, float, bool)):
        values = [values]
    try:
        return b"".join(reorder(struct.pack(">" + _TYPES[data_type], _number(value, data_type)), byte_order) for value in values)
    except (struct.error, OverflowError) as exc:
        raise ValueError(f"Value is outside {data_type} range") from exc


def unpack_values(data: bytes, data_type: str = "UInt16", byte_order: str = "ABCD") -> list:
    size = type_size(data_type)
    if len(data) % size:
        raise ValueError("Data length does not match type size")
    return [struct.unpack(">" + _TYPES[data_type], reorder(data[n:n + size], byte_order))[0] for n in range(0, len(data), size)]
