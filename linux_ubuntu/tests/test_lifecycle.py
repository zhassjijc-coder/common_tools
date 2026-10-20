"""Verify process exit for a tray-style main window that has never been shown."""
import os
from pathlib import Path
import subprocess
import sys
import unittest


class LifecycleTests(unittest.TestCase):
    def test_never_shown_background_window_exits_and_preferences_exclude_passwords(self):
        script = r'''
from pathlib import Path
import json, tempfile
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication
from linklab.ui.main_window import MainWindow
from linklab.ui.theme import configure_font
app=QApplication([])
configure_font(app)
with tempfile.TemporaryDirectory() as directory:
    window=MainWindow(test_mode=True)
    window.state_path=Path(directory)/'ui-state.json'
    window.pages['mqtt'].password.setText('do-not-save-connection-password')
    window.pages['mqtt'].key_password.setText('do-not-save-key-password')
    # Only the root's normal-exit path is enabled; services stay in isolated test mode.
    window.test_mode=False
    QTimer.singleShot(10,window.close)
    QTimer.singleShot(10,window.close)
    QTimer.singleShot(3000,lambda: app.exit(7))
    result=app.exec()
    assert result==0 and window._closed, (result,window._closed)
    text=window.state_path.read_text(encoding='utf-8')
    assert 'do-not-save' not in text
    value=json.loads(text)
    assert value['mqtt.host']=='127.0.0.1'
    assert value['master.parity']=='N'
print('BACKGROUND EXIT AND SECRET-FREE PREFERENCES PASS')
'''
        environment = os.environ.copy()
        environment["QT_QPA_PLATFORM"] = "offscreen"
        result = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True,
                                encoding="utf-8", errors="replace", env=environment, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertIn("BACKGROUND EXIT AND SECRET-FREE PREFERENCES PASS", result.stdout)


if __name__ == "__main__":
    unittest.main()
