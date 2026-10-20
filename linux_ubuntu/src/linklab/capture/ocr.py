"""Local-only OCR using Tesseract, with a genuine Windows DLL fallback."""
from __future__ import annotations

import ctypes
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import time
from PySide6.QtCore import QBuffer, QIODevice
from PySide6.QtGui import QImage


def png_bytes(image: QImage) -> bytes:
    buffer = QBuffer()
    buffer.open(QIODevice.OpenModeFlag.WriteOnly)
    if not image.save(buffer, "PNG"):
        raise RuntimeError("无法编码 OCR 图片")
    return bytes(buffer.data())


def _windows_engine() -> tuple[Path, Path] | None:
    if sys.platform != "win32":
        return None
    workspace = Path(__file__).resolve().parents[4]
    candidates = [workspace / "windows/src/LinkLab.App/bin/Release/net10.0-windows", workspace / "windows/artifacts/LinkLab-1.0-win-x64"]
    for base in candidates:
        library = base / "x64/tesseract50.dll"
        if library.exists() and (base / "tessdata/eng.traineddata").exists():
            return library, base / "tessdata"
    return None


def availability() -> str:
    executable = shutil.which("tesseract")
    if executable:
        return "本机 Tesseract · 支持的识别语言以本机安装的语言包为准"
    if _windows_engine():
        return "使用本机设备联调工具 Windows 包内的 Tesseract DLL / tessdata"
    return "未找到本机 Tesseract。Ubuntu 请安装 tesseract-ocr、tesseract-ocr-eng、tesseract-ocr-chi-sim；Windows 请安装 Tesseract 或保留现有 Windows 包的 x64 DLL 与 tessdata。"


def _recognize_dll(image: QImage, language: str, library: Path, data: Path, cancel: threading.Event | None = None) -> str:
    # Do not modify PATH globally; only this library's dependency directory is added.
    directory = os.add_dll_directory(str(library.parent))
    try:
        engine = ctypes.CDLL(str(library))
        engine.TessBaseAPICreate.restype = ctypes.c_void_p
        engine.TessBaseAPIInit3.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
        engine.TessBaseAPIInit3.restype = ctypes.c_int
        engine.TessBaseAPISetImage.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ubyte), ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int]
        engine.TessBaseAPIGetUTF8Text.argtypes = [ctypes.c_void_p]
        engine.TessBaseAPIGetUTF8Text.restype = ctypes.c_void_p
        engine.TessDeleteText.argtypes = [ctypes.c_void_p]
        engine.TessBaseAPIDelete.argtypes = [ctypes.c_void_p]
        engine.TessBaseAPIRecognize.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        engine.TessBaseAPIRecognize.restype = ctypes.c_int
        engine.TessMonitorCreate.restype = ctypes.c_void_p
        engine.TessMonitorDelete.argtypes = [ctypes.c_void_p]
        engine.TessMonitorSetDeadlineMSecs.argtypes = [ctypes.c_void_p, ctypes.c_int]
        cancel_type = ctypes.CFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_int)
        engine.TessMonitorSetCancelFunc.argtypes = [ctypes.c_void_p, cancel_type]
        handle = engine.TessBaseAPICreate()
        if not handle:
            raise RuntimeError("Tesseract 引擎创建失败")
        try:
            for part in language.split("+"):
                if not (data / (part + ".traineddata")).is_file():
                    raise RuntimeError(f"缺少 OCR 语言包：{part}")
            if engine.TessBaseAPIInit3(handle, os.fsencode(data), language.encode("ascii")) != 0:
                raise RuntimeError("Tesseract 初始化失败，请检查语言数据和原生依赖")
            rgb = image.convertToFormat(QImage.Format.Format_RGB888)
            raw = bytes(rgb.constBits())
            pixels = (ctypes.c_ubyte * len(raw)).from_buffer_copy(raw)
            engine.TessBaseAPISetImage(handle, pixels, rgb.width(), rgb.height(), 3, rgb.bytesPerLine())
            monitor = engine.TessMonitorCreate()
            callback = cancel_type(lambda context, progress: bool(cancel and cancel.is_set()))
            try:
                engine.TessMonitorSetDeadlineMSecs(monitor, 45_000)
                engine.TessMonitorSetCancelFunc(monitor, callback)
                if engine.TessBaseAPIRecognize(handle, monitor) != 0:
                    raise RuntimeError("识别已取消" if cancel and cancel.is_set() else "OCR 识别失败或超过 45 秒")
            finally:
                engine.TessMonitorDelete(monitor)
            text = engine.TessBaseAPIGetUTF8Text(handle)
            if not text:
                return ""
            try:
                return ctypes.string_at(text).decode("utf-8", errors="replace").strip()
            finally:
                engine.TessDeleteText(text)
        finally:
            engine.TessBaseAPIDelete(handle)
    finally:
        directory.close()


def recognize(image: QImage, language: str = "chi_sim+eng", *, cancel: threading.Event | None = None) -> str:
    if image.isNull():
        raise ValueError("没有可识别的图片")
    if image.width() * image.height() > 16_000_000:
        raise ValueError("识别区域超过 1600 万像素，请缩小选区")
    if language not in {"eng", "chi_sim", "chi_sim+eng"}:
        raise ValueError("不支持的 OCR 语言")
    if cancel and cancel.is_set():
        raise RuntimeError("识别已取消")
    if shutil.which("tesseract"):
        executable = shutil.which("tesseract")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        listing = subprocess.run([executable, "--list-langs"], capture_output=True, text=True, errors="replace", timeout=5, creationflags=flags)
        installed = set(listing.stdout.splitlines()[1:])
        missing = set(language.split("+")) - installed
        if missing:
            raise RuntimeError("缺少 OCR 语言包：" + "、".join(sorted(missing)))
        process = subprocess.Popen([executable, "stdin", "stdout", "-l", language], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=flags)
        deadline = time.monotonic() + 45
        payload = png_bytes(image)
        try:
            while True:
                if cancel and cancel.is_set():
                    raise RuntimeError("识别已取消")
                if time.monotonic() > deadline:
                    raise RuntimeError("OCR 超过 45 秒，请缩小识别区域")
                try:
                    output, error = process.communicate(input=payload, timeout=.2)
                    if process.returncode:
                        raise RuntimeError("本机 OCR 失败：" + error.decode("utf-8", errors="replace")[:500])
                    return output.decode("utf-8", errors="replace").strip()
                except subprocess.TimeoutExpired:
                    payload = None
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()
    fallback = _windows_engine()
    if fallback:
        return _recognize_dll(image, language, *fallback, cancel=cancel)
    raise RuntimeError(availability())
