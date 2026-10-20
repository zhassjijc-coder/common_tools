"""Global shortcuts; Wayland's compositor is intentionally not bypassed."""
from __future__ import annotations

import os
import sys
import threading
from PySide6.QtCore import QObject, Signal


class GlobalHotkeys(QObject):
    capture_requested = Signal()
    pin_requested = Signal()

    def __init__(self, parent=None, *, test_mode: bool = False, capture_key: int = 0x70, pin_key: int = 0x72):
        super().__init__(parent)
        self.test_mode = test_mode
        self.capture_key, self.pin_key = capture_key, pin_key
        self._thread = None
        self._stop = threading.Event()
        self._ready = threading.Event()
        self._error = None
        self._thread_id = 0

    @staticmethod
    def limitation() -> str:
        if sys.platform.startswith("linux") and (os.environ.get("XDG_SESSION_TYPE", "").lower() == "wayland" or os.environ.get("WAYLAND_DISPLAY")):
            return "Wayland 会话限制全局按键监听；F1/F3 全局热键不可用。可使用页面按钮；若系统禁止屏幕读取，请使用系统截图后通过剪贴板贴图或打开图片。"
        if sys.platform.startswith("linux") and not os.environ.get("DISPLAY"):
            return "没有可用的 X11 显示会话，无法注册全局 F1/F3。页面按钮仍可使用。"
        return ""

    def enable(self) -> None:
        if self._thread is not None or self.test_mode:
            return
        limitation = self.limitation()
        if limitation:
            raise RuntimeError(limitation)
        self._stop.clear()
        self._ready.clear()
        self._error = None
        target = self._windows if sys.platform == "win32" else self._x11 if sys.platform.startswith("linux") else None
        if target is None:
            raise RuntimeError("当前平台不支持全局热键，页面按钮仍可使用")
        self._thread = threading.Thread(target=target, name="LinkLab-Hotkeys", daemon=True)
        self._thread.start()
        if not self._ready.wait(4):
            self.close()
            raise RuntimeError("全局热键注册超时，页面按钮仍可使用")
        if self._error:
            error = self._error
            self.close()
            raise RuntimeError(error)

    def _windows(self):
        import ctypes
        from ctypes import wintypes
        user = ctypes.WinDLL("user32", use_last_error=True)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetCurrentThreadId.restype = wintypes.DWORD
        user.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
            ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, ctypes.c_void_p]
        user.CreateWindowExW.restype = wintypes.HWND
        user.RegisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT]
        user.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]
        user.DestroyWindow.argtypes = [wintypes.HWND]
        user.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
        user.GetMessageW.restype = ctypes.c_int
        hwnd, registered = None, []
        try:
            self._thread_id = kernel.GetCurrentThreadId()
            hwnd = user.CreateWindowExW(0, "STATIC", "LinkLabQt hotkeys", 0, 0, 0, 0, 0, wintypes.HWND(-3), None, None, None)
            if not hwnd:
                raise ctypes.WinError(ctypes.get_last_error())
            for identifier, key, label in ((101, self.capture_key, "F1"), (103, self.pin_key, "F3")):
                if not user.RegisterHotKey(hwnd, identifier, 0x4000, key):
                    code = ctypes.get_last_error()
                    raise RuntimeError(f"{label} 已被其他程序占用，未接管热键。关闭占用的截图工具后重试。" if code == 1409 else f"{label} 注册失败（Windows {code}）")
                registered.append(identifier)
            self._ready.set()
            message = wintypes.MSG()
            while not self._stop.is_set():
                result = user.GetMessageW(ctypes.byref(message), None, 0, 0)
                if result <= 0:
                    break
                if message.message == 0x0312 and not self._stop.is_set():
                    (self.capture_requested if message.wParam == 101 else self.pin_requested).emit()
        except Exception as exc:
            self._error = str(exc)
        finally:
            for identifier in registered:
                user.UnregisterHotKey(hwnd, identifier)
            if hwnd:
                user.DestroyWindow(hwnd)
            self._ready.set()
            self._thread_id = 0

    def _x11(self):
        connection, grabs = None, []
        try:
            from Xlib import X, XK, display, error
            from collections import deque
            connection = display.Display()
            root = connection.screen().root
            errors = []
            codes = [connection.keysym_to_keycode(XK.string_to_keysym("F1")), connection.keysym_to_keycode(XK.string_to_keysym("F3"))]
            for code in codes:
                for modifiers in (0, X.LockMask, X.Mod2Mask, X.LockMask | X.Mod2Mask):
                    root.grab_key(code, modifiers, True, X.GrabModeAsync, X.GrabModeAsync, onerror=lambda issue, request: errors.append(issue))
                    grabs.append((code, modifiers))
            connection.sync()
            if errors:
                raise RuntimeError("F1 / F3 已被其他程序或桌面快捷键占用，未接管热键；页面按钮仍可使用。")
            self._ready.set()
            pressed, pending = set(), deque()
            while not self._stop.wait(.015):
                while pending or connection.pending_events():
                    event = pending.popleft() if pending else connection.next_event()
                    if event.type == X.KeyRelease:
                        if connection.pending_events():
                            following = connection.next_event()
                            if following.type == X.KeyPress and following.detail == event.detail and following.time == event.time:
                                continue  # X11's synthetic repeat release/press pair.
                            pending.append(following)
                        pressed.discard(event.detail)
                    elif event.type == X.KeyPress and event.detail not in pressed:
                        pressed.add(event.detail)
                        if event.detail in codes:
                            (self.capture_requested if event.detail == codes[0] else self.pin_requested).emit()
        except Exception as exc:
            self._error = "X11 全局热键不可用：" + str(exc)
        finally:
            if connection is not None:
                try:
                    root = connection.screen().root
                    for code, modifiers in grabs:
                        root.ungrab_key(code, modifiers)
                    connection.sync()
                    connection.close()
                except Exception:
                    pass
            self._ready.set()

    def close(self) -> None:
        self._stop.set()
        if sys.platform == "win32" and self._thread_id:
            import ctypes
            ctypes.windll.user32.PostThreadMessageW(self._thread_id, 0x0012, 0, 0)
        if self._thread is not None and self._thread is not threading.current_thread():
            self._thread.join(timeout=2)
        self._thread = None
