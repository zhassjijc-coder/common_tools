"""System settings and the local screenshot/OCR workspace."""
from __future__ import annotations

from PySide6.QtCore import Qt, QTimer, Signal, QSignalBlocker
from PySide6.QtGui import QPixmap
from PySide6.QtWidgets import QApplication, QCheckBox, QFrame, QGridLayout, QHBoxLayout, QLabel, QPlainTextEdit, QPushButton, QScrollArea, QVBoxLayout, QWidget

from .widgets import SelectBox

from ..capture.editor import CaptureController
from ..capture.hotkeys import GlobalHotkeys
from ..capture.ocr import availability
from ..services.settings import AutostartService, SettingsStore


class SettingsPage(QWidget):
    request_hide = Signal()

    def __init__(self, hub, parent=None, *, test_mode=False):
        super().__init__(parent)
        self.hub = hub
        self.store = SettingsStore(test_mode=test_mode)
        self.autostart = AutostartService(test_mode=test_mode)
        self.controller = CaptureController(hub, self.store, self, test_mode=test_mode)
        self.capture_controller = self.controller
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        outer.addWidget(scroll)
        content = QWidget()
        content.setObjectName("canvas")
        layout = QVBoxLayout(content)
        layout.setSpacing(16)
        scroll.setWidget(content)
        header = QHBoxLayout()
        title = QLabel("偏好与截图")
        title.setStyleSheet("font-size:18px;font-weight:600;")
        header.addWidget(title)
        header.addStretch()
        tray = QPushButton("隐藏到托盘")
        tray.clicked.connect(lambda checked=False: self.request_hide.emit())
        header.addWidget(tray)
        layout.addLayout(header)
        layout.addWidget(QLabel("启动与后台、F1 区域截图、F3 悬浮贴图和本机文字识别"))
        startup = self._card("启动与后台", layout)
        self.autostart_checkbox = QCheckBox("登录 Windows / Ubuntu 后自启动（后台运行）")
        try:
            self.autostart_checkbox.setChecked(self.autostart.enabled)
        except OSError as exc:
            hub.set_notice("读取启动项失败：" + str(exc))
        self.autostart_checkbox.toggled.connect(self._toggle_autostart)
        startup.addWidget(self.autostart_checkbox)
        self._hint(startup, "默认关闭。Ubuntu 使用当前用户 XDG autostart；Windows 使用当前用户 Run 启动项。后台启动仅显示托盘图标。")
        self.default_capture_checkbox = QCheckBox("启动时默认开启截图全局热键")
        self.default_capture_checkbox.setChecked(bool(self.store.get("capture_on_startup", False)))
        self.default_capture_checkbox.toggled.connect(self._toggle_default)
        startup.addWidget(self.default_capture_checkbox)
        self._hint(startup, "独立于当前开关；关闭全局热键后，页面截图 / 贴图按钮仍可使用。Wayland 全局热键不受支持。")
        self.enabled_checkbox = QCheckBox("当前启用 F1 截图 / F3 贴图全局热键")
        self.enabled_checkbox.toggled.connect(self.controller.set_enabled)
        self.controller.enabled_changed.connect(self._enabled_changed)
        startup.addWidget(self.enabled_checkbox)
        limitation = GlobalHotkeys.limitation()
        if limitation:
            self._hint(startup, limitation)

        tools = self._card("截图与标注", layout)
        row = QHBoxLayout()
        for label, action in (("开始截图 F1", self.controller.capture), ("贴图 F3", self.controller.pin_clipboard), ("打开图片", self.controller.open_image), ("关闭所有浮窗", self._close_pins)):
            button = QPushButton(label)
            button.clicked.connect(lambda checked=False, action=action: action())
            row.addWidget(button)
        row.addStretch()
        tools.addLayout(row)
        self._hint(tools, "F1：拖选后，V 移动选区 / 标注，拖边角调整尺寸。T 文字、R 矩形、S 正方形，均支持换色；双击文字编辑，Ctrl+Z 撤销。")
        self._hint(tools, "滚轮 / ± 缩放画布，空格拖动或中键平移；100% 重置。Enter 复制，Ctrl+S 保存，Esc 取消。文字输入时 Enter 确认，Shift+Enter 换行。")
        self._hint(tools, "F3：拖动浮图移动；滚轮 / ± 缩放；100% 为当前屏幕的物理像素比例；Ctrl+滚轮调透明度。缩放不会改变保存分辨率。")
        self.status = QLabel("设置立即生效，所有截图和识别都在本机运行。")
        self.status.setWordWrap(True)
        self.status.setObjectName("statusBanner")
        self.status.setProperty("statusTone", "info")
        tools.addWidget(self.status)
        hub.notice.connect(self.status.setText)

        previews = QGridLayout()
        layout.addLayout(previews)
        picture_card = QFrame()
        picture_card.setObjectName("capturePictureCard")
        picture_layout = QVBoxLayout(picture_card)
        picture_layout.addWidget(QLabel("最近截图（保留标注）"))
        self.image_preview = QLabel("还没有截图")
        self.image_preview.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.image_preview.setMinimumSize(250, 230)
        self.image_preview.setObjectName("capturePreview")
        picture_layout.addWidget(self.image_preview)
        picture_actions = QHBoxLayout()
        for label, action in (("复制图片", self.controller.copy_image), ("保存 PNG", self.controller.save_image)):
            button = QPushButton(label)
            button.clicked.connect(lambda checked=False, action=action: action())
            picture_actions.addWidget(button)
        picture_layout.addLayout(picture_actions)
        self._hint(picture_layout, "图片仅在本次运行中保留，主动保存才写入文件。")
        previews.addWidget(picture_card, 0, 0)
        text_card = QFrame()
        text_card.setObjectName("captureTextCard")
        text_layout = QVBoxLayout(text_card)
        text_layout.addWidget(QLabel("文字识别（优先使用标注前原图）"))
        language = SelectBox()
        language.addItem("简体中文 + English", "chi_sim+eng")
        language.addItem("English", "eng")
        language.addItem("简体中文", "chi_sim")
        language.setCurrentIndex(max(0, language.findData(self.store.get("ocr_language", "chi_sim+eng"))))
        language.currentIndexChanged.connect(lambda _: self._save("ocr_language", language.currentData()))
        text_layout.addWidget(language)
        self.ocr_text = QPlainTextEdit()
        self.ocr_text.setPlaceholderText("识别结果可编辑后复制")
        self.ocr_text.setMinimumHeight(180)
        text_layout.addWidget(self.ocr_text)
        ocr_buttons = QHBoxLayout()
        recognize = QPushButton("识别文字")
        recognize.clicked.connect(lambda: self.controller.recognize())
        ocr_buttons.addWidget(recognize)
        copy_text = QPushButton("复制文字")
        copy_text.clicked.connect(lambda: QApplication.clipboard().setText(self.ocr_text.toPlainText()))
        ocr_buttons.addWidget(copy_text)
        text_layout.addLayout(ocr_buttons)
        self._hint(text_layout, availability())
        previews.addWidget(text_card, 0, 1)
        previews.setColumnStretch(0, 1)
        previews.setColumnStretch(1, 1)
        layout.addStretch()
        self.controller.image_changed.connect(self._show_image)
        self.controller.ocr_ready.connect(self.ocr_text.setPlainText)
        if self.default_capture_checkbox.isChecked() and not test_mode:
            QTimer.singleShot(0, lambda: self.controller.set_enabled(True))

    def _card(self, title, parent):
        card = QFrame()
        card.setObjectName("settingsCard")
        layout = QVBoxLayout(card)
        layout.setContentsMargins(20, 18, 20, 18)
        heading = QLabel(title)
        heading.setObjectName("cardTitle")
        layout.addWidget(heading)
        parent.addWidget(card)
        return layout

    @staticmethod
    def _hint(layout, text):
        hint = QLabel(text)
        hint.setWordWrap(True)
        hint.setObjectName("muted")
        hint.setProperty("hint", True)
        layout.addWidget(hint)

    def _save(self, key, value):
        try:
            self.store.set(key, value)
            return True
        except OSError as exc:
            self.hub.set_notice("保存设置失败：" + str(exc))
            return False

    def _toggle_default(self, enabled):
        if not self._save("capture_on_startup", enabled):
            blocker = QSignalBlocker(self.default_capture_checkbox)
            self.default_capture_checkbox.setChecked(not enabled)
            del blocker

    def _toggle_autostart(self, enabled):
        try:
            self.autostart.set_enabled(enabled)
            self.hub.set_notice("当前用户后台自启动已开启" if enabled else "当前用户自启动已关闭")
        except (OSError, RuntimeError, ValueError) as exc:
            blocker = QSignalBlocker(self.autostart_checkbox)
            self.autostart_checkbox.setChecked(not enabled)
            del blocker
            self.hub.set_notice("设置自启动失败：" + str(exc))

    def _enabled_changed(self, enabled):
        blocker = QSignalBlocker(self.enabled_checkbox)
        self.enabled_checkbox.setChecked(enabled)
        del blocker

    def _show_image(self, image):
        self.image_preview.setPixmap(QPixmap.fromImage(image).scaled(self.image_preview.size(), Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation))

    def _close_pins(self):
        for window in self.controller._floating[:]:
            window.close()

    def close_resources(self):
        self.controller.close_resources()
