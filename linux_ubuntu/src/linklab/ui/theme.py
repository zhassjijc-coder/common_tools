from PySide6.QtGui import QFont, QFontDatabase
from pathlib import Path
import os
import sys


def configure_font(app):
    if not QFontDatabase.families() and sys.platform == "win32":
        # Qt's offscreen Windows plugin has no native font database. Load existing
        # system fonts for verification; no font files are copied or redistributed.
        fonts = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
        for filename in ("msyh.ttc", "segoeui.ttf", "consola.ttf"):
            if (fonts / filename).exists():
                QFontDatabase.addApplicationFont(str(fonts / filename))
    installed = set(QFontDatabase.families())
    family = next((name for name in ("Microsoft YaHei UI", "Noto Sans CJK SC", "Noto Sans", "DejaVu Sans") if name in installed), "Sans Serif")
    font = QFont(family, 10)
    font.setStyleStrategy(QFont.StyleStrategy.PreferAntialias)
    app.setFont(font)


def stylesheet(dark=False):
    assets = Path(__file__).resolve().parents[1] / "assets"
    down = (assets / "chevron-down.svg").as_posix()
    up = (assets / "chevron-up.svg").as_posix()
    bg, surface, field = ("#141b2d", "#202b42", "#172238") if dark else ("#f5f7fc", "#ffffff", "#f9faff")
    ink, muted, border = ("#e5eaf4", "#a8b7d1", "#34435c") if dark else ("#20334e", "#718198", "#e0e7f1")
    hover = "#263650" if dark else "#f2f5ff"
    selected = "#2b3e61" if dark else "#edf2ff"
    selected_ink = "#bfd1ff" if dark else "#355bd8"
    disabled_bg, disabled_ink = ("#1b263a", "#6d7f9a") if dark else ("#f3f5f9", "#a2aebf")
    header_bg = "#243148" if dark else "#f7f9fc"
    alternate = "#1d2940" if dark else "#fbfcfe"
    row_line = "#2d3b53" if dark else "#edf1f7"
    mono = next((name for name in ("Cascadia Code", "Consolas", "DejaVu Sans Mono") if name in QFontDatabase.families()), "monospace")
    return f"""
    QWidget {{ color:{ink}; font-size:14px; }}
    QMainWindow, QWidget#canvas {{ background:{bg}; }}
    QWidget#header, QFrame#sidebar, QFrame#card, QFrame#hero {{ background:{surface}; }}
    QFrame#card, QFrame#hero {{ border:1px solid {border}; border-radius:16px; }}
    QFrame#settingsCard, QFrame#capturePictureCard, QFrame#captureTextCard {{ background:{surface}; border:1px solid {border}; border-radius:14px; }}
    QLabel#capturePreview {{ background:{field}; color:{muted}; border-radius:8px; }}
    QLabel {{ background:transparent; }}
    QLabel#title {{ font-size:30px; font-weight:700; }}
    QLabel#brand {{ font-size:22px; font-weight:700; color:#3e5af4; }}
    QLabel#cardTitle {{ font-size:18px; font-weight:600; }}
    QLabel#muted {{ color:{muted}; }}
    QLabel[hint="true"] {{ font-size:12px; }}
    QLabel[fieldLabel="true"] {{ font-size:13px; }}
    QPushButton {{ background:{surface}; border:1px solid {border}; border-radius:9px; padding:10px 15px; min-height:20px; }}
    QPushButton:hover {{ background:{hover}; border-color:#a2b1ee; }}
    QPushButton:pressed {{ background:{selected}; color:{selected_ink}; }}
    QPushButton:focus {{ border-color:#7187ed; }}
    QPushButton[primary="true"] {{ background:#405cf5; border-color:#405cf5; color:white; font-weight:600; }}
    QPushButton[primary="true"]:hover {{ background:#304be2; }}
    QPushButton:disabled {{ color:{disabled_ink}; background:{disabled_bg}; border-color:{border}; }}
    QPushButton[nav="true"] {{ text-align:left; border:0; padding:14px 15px; background:transparent; }}
    QPushButton[nav="true"]:checked {{ background:{selected}; color:{selected_ink}; font-weight:600; }}
    QPushButton[tile="true"] {{ text-align:left; background:{surface}; border-radius:16px; padding:22px; font-size:17px; min-height:135px; }}
    QScrollArea {{ border:0; background:transparent; }}
    QLineEdit, QSpinBox, QDoubleSpinBox, QComboBox, QPlainTextEdit, QTextEdit {{ background:{field}; border:1px solid {border}; border-radius:9px; padding:8px 12px; selection-background-color:{selected}; selection-color:{selected_ink}; }}
    QLineEdit, QSpinBox, QDoubleSpinBox, QComboBox {{ min-height:22px; }}
    QLineEdit:hover, QSpinBox:hover, QDoubleSpinBox:hover, QComboBox:hover {{ border-color:#b8c5dc; }}
    QLineEdit:focus, QSpinBox:focus, QDoubleSpinBox:focus, QComboBox:focus, QPlainTextEdit:focus, QTextEdit:focus {{ border-color:#6c83ee; background:{surface}; }}
    QLineEdit:disabled, QSpinBox:disabled, QDoubleSpinBox:disabled, QComboBox:disabled, QPlainTextEdit:disabled, QTextEdit:disabled {{ background:{disabled_bg}; color:{disabled_ink}; border-color:{border}; }}
    QLineEdit:read-only, QPlainTextEdit:read-only, QTextEdit:read-only {{ background:{surface}; color:{muted}; }}
    QPlainTextEdit[code="true"] {{ font-family:"{mono}"; font-size:13px; padding:12px; color:{ink}; }}
    QComboBox {{ padding-right:8px; }}
    QComboBox:on {{ border-color:#6c83ee; background:{surface}; }}
    QComboBox::drop-down {{ subcontrol-origin:padding; subcontrol-position:top right; width:31px; border:0; margin-right:4px; }}
    QComboBox::drop-down:hover {{ background:{hover}; border-radius:6px; }}
    QComboBox::down-arrow {{ image:url("{down}"); width:14px; height:14px; }}
    QSpinBox::up-button, QDoubleSpinBox::up-button {{ subcontrol-origin:border; subcontrol-position:top right; width:27px; border:0; border-top-right-radius:9px; margin:1px 1px 0 0; background:transparent; }}
    QSpinBox::down-button, QDoubleSpinBox::down-button {{ subcontrol-origin:border; subcontrol-position:bottom right; width:27px; border:0; border-bottom-right-radius:9px; margin:0 1px 1px 0; background:transparent; }}
    QSpinBox::up-button:hover, QSpinBox::down-button:hover, QDoubleSpinBox::up-button:hover, QDoubleSpinBox::down-button:hover {{ background:{hover}; }}
    QSpinBox::up-arrow, QDoubleSpinBox::up-arrow {{ image:url("{up}"); width:12px; height:12px; }}
    QSpinBox::down-arrow, QDoubleSpinBox::down-arrow {{ image:url("{down}"); width:12px; height:12px; }}
    QComboBox QAbstractItemView {{ background:{surface}; color:{ink}; border:1px solid {border}; border-radius:9px; padding:5px; selection-background-color:{selected}; selection-color:{selected_ink}; outline:0; }}
    QTableWidget {{ background:{surface}; alternate-background-color:{alternate}; border:1px solid {border}; border-radius:11px; selection-background-color:{selected}; selection-color:{ink}; outline:0; font-size:13px; }}
    QTableWidget:focus {{ border-color:#b0bfeb; }}
    QTableWidget::item {{ padding:0 12px; border:0; border-bottom:1px solid {row_line}; }}
    QTableWidget::item:hover {{ background:{hover}; }}
    QTableWidget::item:selected {{ background:{selected}; color:{ink}; }}
    QHeaderView {{ background:{header_bg}; }}
    QHeaderView::section {{ background:{header_bg}; color:{muted}; font-size:12px; font-weight:600; padding:0 12px; border:0; border-bottom:1px solid {border}; }}
    QHeaderView::section:first {{ border-top-left-radius:10px; }}
    QHeaderView::section:last {{ border-top-right-radius:10px; }}
    QWidget#tableEmptyState {{ background:transparent; }}
    QLabel#emptyTitle {{ color:{muted}; font-size:14px; font-weight:600; }}
    QLabel#emptyHint {{ color:{muted}; font-size:12px; }}
    QLabel#selectionSummary {{ background:{header_bg}; color:{ink}; border:1px solid {border}; border-radius:8px; padding:10px 12px; font-size:13px; }}
    QLabel#statusBanner {{ border:0; border-radius:8px; padding:10px 12px; font-size:13px; background:{header_bg}; color:{muted}; }}
    QLabel#statusBanner[statusTone="info"] {{ background:{'#283e66' if dark else '#edf2ff'}; color:{selected_ink}; }}
    QLabel#statusBanner[statusTone="success"] {{ background:{'#1d443e' if dark else '#eaf7f2'}; color:{'#95ddc1' if dark else '#167459'}; }}
    QLabel#statusBanner[statusTone="warning"] {{ background:{'#4d3c24' if dark else '#fff4e3'}; color:{'#f4cd8e' if dark else '#9e641c'}; }}
    QLabel#statusBanner[statusTone="error"] {{ background:{'#4e2c3b' if dark else '#fff0f2'}; color:{'#ffb2c1' if dark else '#b74457'}; }}
    QTabWidget::pane {{ border:1px solid {border}; border-radius:8px; }}
    QTabBar::tab {{ padding:10px 20px; background:{field}; border-bottom:2px solid transparent; }}
    QTabBar::tab:selected {{ color:{selected_ink}; border-bottom-color:#7187ed; background:{surface}; }}
    QCheckBox {{ spacing:8px; padding:5px; }}
    QCheckBox::indicator {{ width:17px; height:17px; }}
    QScrollBar:vertical {{ width:9px; background:transparent; margin:3px; }}
    QScrollBar::handle:vertical {{ background:{'#51627e' if dark else '#c8d2e2'}; border-radius:3px; min-height:36px; }}
    QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{ height:0; }}
    QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {{ background:transparent; }}
    QScrollBar:horizontal {{ height:10px; background:transparent; margin:2px; }}
    QScrollBar::handle:horizontal {{ background:{'#51627e' if dark else '#c8d2e2'}; border-radius:3px; min-width:36px; }}
    QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {{ width:0; }}
    QScrollBar::add-page:horizontal, QScrollBar::sub-page:horizontal {{ background:transparent; }}
    QStatusBar {{ background:{surface}; border-top:1px solid {border}; color:{muted}; }}
    QToolTip {{ background:{surface}; color:{ink}; border:1px solid {border}; padding:6px; }}
    """
