from __future__ import annotations

import argparse
import getpass
import hashlib
import os
from pathlib import Path
import sys
from PySide6.QtCore import QTimer
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import QApplication
from . import APP_TITLE, __version__
from .ui.theme import configure_font, stylesheet


def main(argv=None):
    parser = argparse.ArgumentParser(description=f"{APP_TITLE} · Python Qt {__version__}")
    parser.add_argument("--background", action="store_true", help="start in tray when available")
    parser.add_argument("--smoke-test", metavar="OUTPUT", help="run isolated Qt UI verification and exit")
    args = parser.parse_args(argv)
    app = QApplication.instance() or QApplication(sys.argv[:1])
    app.setApplicationName("LinkLabQt")
    app.setApplicationDisplayName(APP_TITLE)
    app.setApplicationVersion(__version__)
    app.setOrganizationName("LinkLab")
    configure_font(app)
    app.setStyle("Fusion")
    app.setStyleSheet(stylesheet())
    if args.smoke_test:
        from .smoke import run
        return run(app, Path(args.smoke_test))
    # Scope single-instance activation to the current user and graphical session.
    identity = f"{getpass.getuser()}:{os.environ.get('DISPLAY', '')}:{os.environ.get('WAYLAND_DISPLAY', '')}:{os.environ.get('SESSIONNAME', '')}"
    name = "linklab-qt-"+hashlib.sha256(identity.encode()).hexdigest()[:24]
    probe = QLocalSocket()
    probe.connectToServer(name)
    if probe.waitForConnected(300):
        if not args.background:
            probe.write(b"show")
            probe.waitForBytesWritten(300)
        probe.disconnectFromServer()
        return 0
    server = QLocalServer(app)
    server.setSocketOptions(QLocalServer.SocketOption.UserAccessOption)
    QLocalServer.removeServer(name)
    if not server.listen(name):
        raise RuntimeError("无法建立本地单实例连接："+server.errorString())
    from .ui.main_window import MainWindow
    window = MainWindow()
    def activate():
        while server.hasPendingConnections():
            client = server.nextPendingConnection()
            def read(client=client):
                if b"show" in client.readAll().data():
                    window.show_main()
            client.readyRead.connect(read)
            client.disconnected.connect(client.deleteLater)
            read()
    server.newConnection.connect(activate)
    if not args.background or not window.tray:
        window.show()
    result = app.exec()
    server.close()
    return result


if __name__ == "__main__":
    raise SystemExit(main())
