"""Small local preferences and per-user opt-in startup registration."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
from .. import APP_NAME, APP_SUBTITLE


class SettingsStore:
    def __init__(self, path: str | Path | None = None, *, test_mode: bool = False):
        self.test_mode = test_mode
        base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local")) if sys.platform == "win32" else Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
        self.path = Path(path) if path else base / "LinkLabQt" / "settings.json"
        self.values: dict = {}
        if not test_mode:
            try:
                value = json.loads(self.path.read_text(encoding="utf-8"))
                if isinstance(value, dict):
                    self.values = value
            except (OSError, ValueError):
                pass

    def get(self, name: str, default=None):
        return self.values.get(name, default)

    def set(self, name: str, value) -> None:
        candidate = {**self.values, name: value}
        if not self.test_mode:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.path.parent / ("settings-" + uuid.uuid4().hex + ".tmp")
            try:
                with temporary.open("x", encoding="utf-8") as stream:
                    json.dump(candidate, stream, ensure_ascii=False, indent=2)
                os.replace(temporary, self.path)
            finally:
                temporary.unlink(missing_ok=True)
        self.values = candidate


class AutostartService:
    """Only writes LinkLab's own current-user entry when explicitly requested."""
    VALUE_NAME = "LinkLabPython"

    def __init__(self, *, test_mode: bool = False, desktop_path: str | Path | None = None, platform: str | None = None):
        self.test_mode = test_mode
        self.platform = platform or sys.platform
        self._memory = False
        config = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
        self.desktop_path = Path(desktop_path) if desktop_path else config / "autostart" / "linklab.desktop"

    @property
    def enabled(self) -> bool:
        if self.test_mode:
            return self._memory
        if self.platform == "win32":
            import winreg
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
                    return bool(winreg.QueryValueEx(key, self.VALUE_NAME)[0])
            except FileNotFoundError:
                return False
        if self.platform.startswith("linux"):
            try:
                text = self.desktop_path.read_text(encoding="utf-8")
                return "X-GNOME-Autostart-enabled=false" not in text and "Hidden=true" not in text
            except FileNotFoundError:
                return False
        return False

    @staticmethod
    def _argument(value: str) -> str:
        if "\n" in value or "\r" in value:
            raise ValueError("启动命令包含无效换行")
        value = value.replace("%", "%%")
        for old, new in (("\\", "\\\\"), ('"', '\\"'), ("`", "\\`"), ("$", "\\$")):
            value = value.replace(old, new)
        return '"' + value + '"'

    def command(self) -> list[str]:
        source = Path(__file__).resolve().parents[2]
        executable = Path(sys.executable)
        if self.platform == "win32" and executable.with_name("pythonw.exe").exists():
            executable = executable.with_name("pythonw.exe")
        bootstrap = f"import runpy,sys;sys.path.insert(0,{str(source)!r});runpy.run_module('linklab',run_name='__main__')"
        return [str(executable), "-c", bootstrap, "--background"]

    def set_enabled(self, enabled: bool) -> None:
        if self.test_mode:
            self._memory = bool(enabled)
            return
        if self.platform == "win32":
            import winreg
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
                if enabled:
                    winreg.SetValueEx(key, self.VALUE_NAME, 0, winreg.REG_SZ, subprocess.list2cmdline(self.command()))
                else:
                    try:
                        winreg.DeleteValue(key, self.VALUE_NAME)
                    except FileNotFoundError:
                        pass
        elif self.platform.startswith("linux"):
            if not enabled:
                self.desktop_path.unlink(missing_ok=True)
                return
            self.desktop_path.parent.mkdir(parents=True, exist_ok=True)
            command = " ".join(self._argument(item) for item in self.command())
            content = f"[Desktop Entry]\nType=Application\nName={APP_NAME}\nComment={APP_SUBTITLE}\nExec=" + command + "\nTerminal=false\nStartupNotify=false\nX-GNOME-Autostart-enabled=true\n"
            self.desktop_path.write_text(content, encoding="utf-8")
        else:
            raise RuntimeError("当前平台暂未实现后台自启动")
