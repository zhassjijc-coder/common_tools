"""Shared presentation widgets; protocol values remain in the underlying models."""
from __future__ import annotations

import html
from PySide6.QtCore import QEvent, QPointF, QRectF, QSize, Qt, QTimer
from PySide6.QtGui import QColor, QPainter, QPainterPath, QPalette, QPen
from PySide6.QtWidgets import (QAbstractItemView, QComboBox, QFrame, QHeaderView,
    QLabel, QListView, QStyle, QStyledItemDelegate, QStyleOptionViewItem,
    QTableWidget, QTableWidgetItem, QToolTip, QVBoxLayout, QWidget)


TONE_ROLE = int(Qt.ItemDataRole.UserRole) + 100


def _dark(palette):
    return palette.color(QPalette.ColorRole.Base).lightness() < 125


def _tone(palette, tone):
    colors = {
        "info": ("#ecf1ff", "#355bd8", "#283e66", "#b6cdff"),
        "success": ("#eaf7f2", "#167459", "#1d443e", "#95ddc1"),
        "warning": ("#fff4e3", "#9e641c", "#4d3c24", "#f4cd8e"),
        "error": ("#fff0f2", "#b74457", "#4e2c3b", "#ffb2c1"),
        "neutral": ("#eff3f8", "#65758c", "#334058", "#c1cce0"),
    }
    light_bg, light_ink, dark_bg, dark_ink = colors.get(tone, colors["neutral"])
    return (QColor(dark_bg), QColor(dark_ink)) if _dark(palette) else (QColor(light_bg), QColor(light_ink))


class _TooltipDelegate(QStyledItemDelegate):
    def helpEvent(self, event, view, option, index):
        text = index.data(Qt.ItemDataRole.ToolTipRole) or index.data(Qt.ItemDataRole.DisplayRole)
        if event.type() == QEvent.Type.ToolTip and text:
            QToolTip.showText(event.globalPos(), "<qt>"+html.escape(str(text)).replace("\n", "<br>")+"</qt>", view)
            return True
        return super().helpEvent(event, view, option, index)


class _SelectDelegate(_TooltipDelegate):
    def __init__(self, owner):
        super().__init__(owner)
        self.owner = owner

    def sizeHint(self, option, index):
        size = super().sizeHint(option, index)
        return QSize(size.width()+45, max(36, size.height()+12))

    def paint(self, painter, option, index):
        opt = QStyleOptionViewItem(option)
        self.initStyleOption(opt, index)
        current = index.row() == self.owner.currentIndex()
        active = bool(opt.state & (QStyle.StateFlag.State_Selected | QStyle.StateFlag.State_MouseOver))
        dark = _dark(opt.palette)
        background = QColor("#2b3e61" if dark else "#edf2ff") if current else QColor("#29364b" if dark else "#f5f7fc")
        painter.save()
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        if current or active:
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(background)
            painter.drawRoundedRect(QRectF(opt.rect.adjusted(3, 1, -3, -1)), 6, 6)
        enabled = bool(opt.state & QStyle.StateFlag.State_Enabled)
        group = QPalette.ColorGroup.Active if enabled else QPalette.ColorGroup.Disabled
        ink = QColor("#b6cdff" if dark else "#355bd8") if current and enabled else opt.palette.color(group, QPalette.ColorRole.Text)
        painter.setPen(ink)
        painter.setFont(opt.font)
        rect = opt.rect.adjusted(13, 0, -34, 0)
        text = opt.fontMetrics.elidedText(opt.text, Qt.TextElideMode.ElideRight, rect.width())
        painter.drawText(rect, Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter, text)
        if current:
            center = opt.rect.center()
            x, y = opt.rect.right()-19, center.y()
            path = QPainterPath(QPointF(x-4, y))
            path.lineTo(x-1, y+3)
            path.lineTo(x+5, y-4)
            painter.setPen(QPen(ink, 1.7, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
            painter.drawPath(path)
        painter.restore()


class SelectBox(QComboBox):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        view = QListView(self)
        view.setObjectName("selectPopup")
        view.setMouseTracking(True)
        view.setUniformItemSizes(True)
        view.setSpacing(2)
        view.setFrameShape(QFrame.Shape.NoFrame)
        view.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        view.setVerticalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        view.setTextElideMode(Qt.TextElideMode.ElideRight)
        self.setView(view)
        self.setItemDelegate(_SelectDelegate(self))
        self.setMaxVisibleItems(10)
        self.setInsertPolicy(QComboBox.InsertPolicy.NoInsert)
        self.currentTextChanged.connect(self._update_tooltip)

    def _update_tooltip(self, text):
        detail = self.currentData(Qt.ItemDataRole.ToolTipRole)
        value = text if not detail or detail == text else text+"\n"+str(detail)
        self.setToolTip("<qt>"+html.escape(value).replace("\n", "<br>")+"</qt>" if value else "")

    def showPopup(self):
        super().showPopup()
        # Start at the committed value even after a previous Escape cancellation.
        if self.currentIndex() >= 0:
            index = self.model().index(self.currentIndex(), self.modelColumn(), self.rootModelIndex())
            self.view().setCurrentIndex(index)
            self.view().scrollTo(index)

    def wheelEvent(self, event):
        if not self.view().isVisible() and not self.hasFocus():
            event.ignore()
            return
        super().wheelEvent(event)


class _CellDelegate(_TooltipDelegate):
    def paint(self, painter, option, index):
        tone = index.data(TONE_ROLE)
        if not tone:
            return super().paint(painter, option, index)
        opt = QStyleOptionViewItem(option)
        self.initStyleOption(opt, index)
        text, opt.text = opt.text, ""
        style = opt.widget.style() if opt.widget else self.parent().style()
        style.drawControl(QStyle.ControlElement.CE_ItemViewItem, opt, painter, opt.widget)
        available = opt.rect.adjusted(9, 5, -9, -5)
        if available.width() < 12:
            return
        painter.save()
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setFont(opt.font)
        width = min(available.width(), opt.fontMetrics.horizontalAdvance(text)+20)
        height = min(26, available.height())
        rect = QRectF(available.center().x()-width/2, available.center().y()-height/2, width, height)
        background, ink = _tone(opt.palette, tone)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(background)
        painter.drawRoundedRect(rect, 6, 6)
        painter.setPen(ink)
        shown = opt.fontMetrics.elidedText(text, Qt.TextElideMode.ElideRight, max(0, round(width)-14))
        painter.drawText(rect, Qt.AlignmentFlag.AlignCenter, shown)
        painter.restore()


class DataTable(QTableWidget):
    def __init__(self, headers, *, column_weights=None, min_height=220, empty_text="暂无数据", empty_hint="连接设备或执行读取后，结果会显示在这里。", compact=False):
        headers = list(headers)
        super().__init__(0, len(headers))
        self._column_specs = [None]*len(headers)
        self._column_weights = list(column_weights or [1]*len(headers))
        self._minimum_stretch = 90
        self._sizing_columns = False
        self._layout_pending = False
        self._empty_pending = False
        self._empty_title = empty_text
        self._empty_hint = empty_hint
        self.setHorizontalHeaderLabels(headers)
        self.setAlternatingRowColors(True)
        self.setShowGrid(False)
        self.setWordWrap(False)
        self.setTextElideMode(Qt.TextElideMode.ElideRight)
        self.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.setVerticalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        self.setHorizontalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        self.setMinimumHeight(min_height)
        self.setMinimumWidth(0)
        self.setItemDelegate(_CellDelegate(self))
        self.verticalHeader().hide()
        self.verticalHeader().setDefaultSectionSize(38 if compact else 44)
        self.verticalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Fixed)
        header = self.horizontalHeader()
        header.setDefaultAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)
        header.setFixedHeight(42)
        header.setMinimumSectionSize(44)
        header.setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        header.setStretchLastSection(False)
        header.setHighlightSections(False)
        header.sectionResized.connect(self._column_resized)
        self._empty = QWidget(self.viewport())
        self._empty.setObjectName("tableEmptyState")
        self._empty.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        layout = QVBoxLayout(self._empty)
        layout.setContentsMargins(18, 16, 18, 16)
        layout.setSpacing(7)
        layout.addStretch()
        self._empty_heading = QLabel(empty_text)
        self._empty_heading.setObjectName("emptyTitle")
        self._empty_heading.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(self._empty_heading)
        self._empty_description = QLabel(empty_hint)
        self._empty_description.setObjectName("emptyHint")
        self._empty_description.setWordWrap(True)
        self._empty_description.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(self._empty_description)
        layout.addStretch()
        self.viewport().installEventFilter(self)
        model = self.model()
        model.rowsInserted.connect(self._schedule_empty)
        model.rowsRemoved.connect(self._schedule_empty)
        model.modelReset.connect(self._schedule_empty)
        self._schedule_columns()
        self._schedule_empty()

    def set_empty_text(self, title, hint=""):
        self._empty_title, self._empty_hint = title, hint
        self.update_empty_state()

    def _schedule_empty(self, *_):
        if not self._empty_pending:
            self._empty_pending = True
            QTimer.singleShot(0, self.update_empty_state)

    def update_empty_state(self):
        self._empty_pending = False
        empty = not any(not self.isRowHidden(index) for index in range(self.rowCount()))
        self._empty_heading.setText("没有匹配的记录" if self.rowCount() else self._empty_title)
        self._empty_description.setText("调整筛选条件后重试。" if self.rowCount() else self._empty_hint)
        self._empty.setGeometry(self.viewport().rect())
        self._empty.setVisible(empty)
        self._empty.raise_()

    def setRowHidden(self, row, hide):
        super().setRowHidden(row, hide)
        self._schedule_empty()

    def eventFilter(self, watched, event):
        if watched is self.viewport() and event.type() == QEvent.Type.Resize:
            self._empty.setGeometry(self.viewport().rect())
            self._schedule_columns()
        return super().eventFilter(watched, event)

    def _column_resized(self, index, old, new):
        if not self._sizing_columns and 0 <= index < len(self._column_specs):
            # Hiding/showing a section emits zero-width changes; only retain
            # actual interactive widths, not those internal layout changes.
            if new > 0 and old > 0 and not self.isColumnHidden(index):
                self._column_specs[index] = max(44, new)
            self._schedule_columns()

    def _schedule_columns(self):
        if not self._layout_pending:
            self._layout_pending = True
            QTimer.singleShot(0, self._apply_columns)

    def _apply_columns(self):
        self._layout_pending = False
        if self._sizing_columns:
            return
        visible = [index for index in range(len(self._column_specs)) if not self.isColumnHidden(index)]
        flexible = [index for index in visible if self._column_specs[index] is None]
        fixed = sum(self._column_specs[index] or 0 for index in visible)
        available = max(self._minimum_stretch*len(flexible), self.viewport().width()-fixed)
        weights = sum(self._column_weights[index] for index in flexible) or 1
        self._sizing_columns = True
        try:
            remaining = available
            weight_left = weights
            flexible_left = len(flexible)
            for index in visible:
                width = self._column_specs[index]
                if width is None:
                    flexible_left -= 1
                    width = max(self._minimum_stretch, round(remaining*self._column_weights[index]/weight_left))
                    width = min(width, remaining-self._minimum_stretch*flexible_left)
                    remaining -= width
                    weight_left -= self._column_weights[index]
                self.setColumnWidth(index, width)
        finally:
            self._sizing_columns = False


def configure_columns(grid, widths, *, stretch_last=True, minimum_stretch=180):
    widths = list(widths)
    if len(widths) != grid.columnCount() or any(width is not None and (not isinstance(width, int) or width < 44) for width in widths):
        raise ValueError("Column widths must match the table, using positive pixels or None")
    if stretch_last and widths and all(width is not None for width in widths):
        widths[-1] = None
    grid._column_specs = widths
    grid._minimum_stretch = minimum_stretch
    grid._schedule_columns()


def set_cell(grid, row, column, text, *, align=None, tone=None, tooltip=None):
    item = QTableWidgetItem(str(text))
    item.setToolTip(str(tooltip if tooltip is not None else text))
    if align is not None:
        item.setTextAlignment(align)
    if tone:
        item.setData(TONE_ROLE, tone)
    grid.setItem(row, column, item)
    return item
