using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LinkLab.App;

public enum CaptureAction { Copy, Save, Pin, Ocr }

public sealed class CaptureOverlay : Window
{
    private enum Drag { None, New, Move, Resize, Mark, Rectangle, Square, Pan }
    private sealed record State(Rect Selection, CaptureMark[] Marks);
    private readonly DesktopFrame _frame;
    private readonly Grid _viewport = new() { ClipToBounds = true };
    private readonly Canvas _scene = new() { Background = Brushes.Transparent };
    private readonly Canvas _hud = new();
    private readonly Image _desktop;
    private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(135, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _outline = new() { Stroke = Brushes.DodgerBlue, IsHitTestVisible = false };
    private readonly CaptureMarkLayer _markLayer = new() { IsHitTestVisible = false };
    private readonly List<Rectangle> _handles = [];
    private readonly List<CaptureMark> _marks = [];
    private readonly List<State> _history = [];
    private readonly Dictionary<CaptureTool, Button> _toolButtons = [];
    private readonly Border _toolbar;
    private readonly TextBlock _size = new() { Padding = new Thickness(8, 4, 8, 4), FontSize = 12, IsHitTestVisible = false };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, Width = 46, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _fontLabel = new() { VerticalAlignment = VerticalAlignment.Center, Width = 28, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _tips;
    private Rect _selection = Rect.Empty, _originRect;
    private Point _origin, _panOrigin;
    private Vector _pan, _panBefore;
    private Drag _drag;
    private CaptureEdge _edge;
    private CaptureTool _tool;
    private Color _color = Color.FromRgb(238, 66, 83);
    private double _zoom = 1, _fontSize = 20;
    private bool _space;
    private int _selectedMark = -1, _editingMark = -1;
    private TextBox? _textEditor;
    private Point _textPoint;
    private CaptureMark? _pending;
    public event Action<BitmapSource, CaptureAction>? Completed;
    public BitmapSource? OriginalImage { get; private set; }
    internal Rect Selection => _selection;
    internal double PreviewZoom => _zoom;
    internal int AnnotationCount => _marks.Count;
    internal Size DisplaySize => SceneSize;

    public CaptureOverlay(DesktopFrame frame, bool position = true)
    {
        _frame = frame; Title = "LinkLab 区域截图"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; Background = Brushes.Black; Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual; Width = frame.Width; Height = frame.Height;
        SetResourceReference(ForegroundProperty, "Text");
        _desktop = new Image { Source = frame.Image, Stretch = Stretch.Fill, IsHitTestVisible = false };
        _scene.Children.Add(_desktop); _scene.Children.Add(_markLayer); _scene.Children.Add(_shade); _scene.Children.Add(_outline);
        for (int n = 0; n < 8; n++)
        {
            var handle = new Rectangle { Fill = Brushes.White, Stroke = Brushes.DodgerBlue, IsHitTestVisible = false };
            _handles.Add(handle); _scene.Children.Add(handle);
        }
        _size.SetResourceReference(TextBlock.ForegroundProperty, "Text"); _size.SetResourceReference(TextBlock.BackgroundProperty, "Card");
        _hud.Children.Add(_size);
        _tips = new TextBlock
        {
            Text = "拖动选择区域 · 滚轮缩放画布 · 空格拖动 / 中键平移 · Enter 复制 · F3 贴图 · Esc 取消",
            Padding = new Thickness(12, 8, 12, 8), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19, IsHitTestVisible = false
        };
        _tips.SetResourceReference(TextBlock.ForegroundProperty, "Text"); _tips.SetResourceReference(TextBlock.BackgroundProperty, "Card");
        Canvas.SetTop(_tips, 16); Canvas.SetLeft(_tips, 16); _hud.Children.Add(_tips);
        _toolbar = CreateToolbar(); _hud.Children.Add(_toolbar);
        _viewport.Children.Add(_scene); _viewport.Children.Add(_hud); Content = _viewport;
        _scene.MouseDown += MouseDownOnScene; _scene.MouseMove += MouseMoveOnScene; _scene.MouseUp += MouseUpOnScene;
        _scene.MouseRightButtonUp += (_, e) =>
        {
            if (IsTextInput(e.OriginalSource)) return;
            if (_textEditor is not null) CancelText();
            else if (_selectedMark >= 0) { _selectedMark = -1; Update(); }
            else if (_selection.IsEmpty) Close();
            else { SaveState(); _selection = Rect.Empty; _marks.Clear(); Update(); }
            e.Handled = true;
        };
        _scene.MouseWheel += (_, e) => { if (IsTextInput(e.OriginalSource)) return; CommitText(); ZoomAt(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(_viewport)); e.Handled = true; };
        _scene.LostMouseCapture += (_, _) => { if (_drag != Drag.None) { _drag = Drag.None; _pending = null; Update(); } };
        KeyDown += KeyPressed; KeyUp += (_, e) => { if (e.Key == Key.Space) { _space = false; UpdateCursor(Mouse.GetPosition(_scene)); } };
        Deactivated += (_, _) => { _space = false; _drag = Drag.None; _pending = null; _scene.ReleaseMouseCapture(); Update(); };
        SizeChanged += (_, _) => LayoutScene();
        Loaded += (_, _) => { if (position) DesktopCapture.Position(this, frame); if (ShowActivated) Activate(); LayoutScene(); };
    }

    private Border CreateToolbar()
    {
        var content = new StackPanel(); var tools = new WrapPanel(); content.Children.Add(tools);
        foreach (var (label, tool) in new[] { ("移动 V", CaptureTool.Select), ("文字 T", CaptureTool.Text), ("矩形 R", CaptureTool.Rectangle), ("正方形 S", CaptureTool.Square) })
        {
            var button = Button(label, () => SelectTool(tool));
            button.ToolTip = tool == CaptureTool.Select ? "拖动选区内部移动，拖动边角调整大小；点击标注可移动，双击文字可编辑" : "点击或拖动选区内添加标注";
            _toolButtons.Add(tool, button); tools.Children.Add(button);
        }
        foreach (var (name, value) in new[] { ("红色", "#EE4253"), ("橙色", "#ED9A23"), ("蓝色", "#3065E8"), ("绿色", "#14976D"), ("黑色", "#182335"), ("白色", "#FFFFFF") })
        {
            Color color = (Color)ColorConverter.ConvertFromString(value);
            var button = Button("", () => SetColor(color)); button.Width = 25; button.Height = 25; button.Padding = new Thickness(0);
            button.Content = new Border { Background = new SolidColorBrush(color), Width = 15, Height = 15, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.5), CornerRadius = new CornerRadius(3) };
            button.ToolTip = name + " · 修改当前选中标注及后续标注颜色"; tools.Children.Add(button);
        }
        tools.Children.Add(Button("字−", () => ChangeFont(-2))); tools.Children.Add(_fontLabel); tools.Children.Add(Button("字+", () => ChangeFont(2)));
        tools.Children.Add(Button("撤销", Undo)); tools.Children.Add(Button("删除", DeleteMark));
        var actions = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) }; content.Children.Add(actions);
        foreach (var (label, action) in new[] { ("复制 ↵", CaptureAction.Copy), ("保存", CaptureAction.Save), ("贴图 F3", CaptureAction.Pin), ("文字识别", CaptureAction.Ocr) })
            actions.Children.Add(Button(label, () => Finish(action)));
        actions.Children.Add(Button("−", () => ZoomAt(_zoom / 1.2, Center()))); actions.Children.Add(_zoomLabel);
        actions.Children.Add(Button("+", () => ZoomAt(_zoom * 1.2, Center()))); actions.Children.Add(Button("100%", ResetZoom));
        actions.Children.Add(new TextBlock { Text = "Ctrl+Z 撤销 · 选中文字/框后可切色", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        var toolbar = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Child = content, Cursor = Cursors.Arrow };
        toolbar.SetResourceReference(Border.BackgroundProperty, "Card"); toolbar.SetResourceReference(Border.BorderBrushProperty, "Line");
        return toolbar;
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(2), Padding = new Thickness(9, 6, 9, 6), FontSize = 12, Focusable = false };
        button.Click += (_, _) => action(); return button;
    }
    private Size SceneSize => new(Math.Max(2, _scene.Width), Math.Max(2, _scene.Height));
    private Point Center() => new(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2);
    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, SceneSize.Width), Math.Clamp(point.Y, 0, SceneSize.Height));
    private Point Local(Point point) => new(point.X - _selection.Left, point.Y - _selection.Top);
    private Point Screen(Point point) => new(point.X * _zoom + _pan.X, point.Y * _zoom + _pan.Y);
    private void LayoutScene()
    {
        _scene.Width = _desktop.Width = Math.Max(2, _viewport.ActualWidth);
        _scene.Height = _desktop.Height = Math.Max(2, _viewport.ActualHeight);
        _tips.MaxWidth = Math.Max(100, _viewport.ActualWidth - 32); _toolbar.MaxWidth = Math.Max(100, _viewport.ActualWidth - 16);
        LimitPan(); Update();
    }
    private void LimitPan()
    {
        double w = SceneSize.Width * _zoom, h = SceneSize.Height * _zoom;
        _pan = new(w <= _viewport.ActualWidth ? (_viewport.ActualWidth - w) / 2 : Math.Clamp(_pan.X, _viewport.ActualWidth - w, 0),
            h <= _viewport.ActualHeight ? (_viewport.ActualHeight - h) / 2 : Math.Clamp(_pan.Y, _viewport.ActualHeight - h, 0));
    }
    private void ZoomAt(double value, Point anchor)
    {
        CommitText(); double next = Math.Clamp(value, .25, 8);
        var world = new Point((anchor.X - _pan.X) / _zoom, (anchor.Y - _pan.Y) / _zoom);
        _zoom = next; _pan = new(anchor.X - world.X * next, anchor.Y - world.Y * next); LimitPan(); Update();
    }
    private void ResetZoom() { _zoom = 1; _pan = default; Update(); }
    private void SelectTool(CaptureTool tool) { CommitText(); _tool = tool; _selectedMark = -1; Update(); }
    private void SaveState()
    {
        _history.Add(new(_selection, _marks.ToArray())); if (_history.Count > 60) _history.RemoveAt(0);
    }
    private void Undo()
    {
        if (_textEditor is not null) { CancelText(); return; }
        if (_history.Count == 0) return; var state = _history[^1]; _history.RemoveAt(_history.Count - 1);
        _selection = state.Selection; _marks.Clear(); _marks.AddRange(state.Marks); _selectedMark = -1; _pending = null; Update();
    }
    private void SetColor(Color color)
    {
        _color = color;
        if (_textEditor is not null) { _textEditor.Foreground = new SolidColorBrush(color); _textEditor.Background = TextBackground(color); }
        else if (_selectedMark >= 0) { SaveState(); _marks[_selectedMark] = _marks[_selectedMark] with { Color = color }; }
        Update();
    }
    private void ChangeFont(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 10, 72);
        if (_textEditor is not null)
        {
            _textEditor.FontSize = _fontSize; _textEditor.MinHeight = _fontSize + 12;
            _textEditor.MaxHeight = Math.Max(_fontSize + 12, Math.Min(200, _selection.Height - _textPoint.Y));
        }
        else if (_selectedMark >= 0 && _marks[_selectedMark].IsText)
        {
            SaveState(); var mark = _marks[_selectedMark];
            _marks[_selectedMark] = CaptureEditing.Label(mark.Bounds.TopLeft, mark.Text!, mark.Color, _fontSize,
                mark.WrapWidth > 0 ? mark.WrapWidth : Math.Max(2, _selection.Width - mark.Bounds.X));
        }
        Update();
    }
    private void DeleteMark()
    {
        if (_textEditor is not null) { CancelText(); return; }
        if (_selectedMark < 0) return; SaveState(); _marks.RemoveAt(_selectedMark); _selectedMark = -1; Update();
    }
    private void MouseDownOnScene(object sender, MouseButtonEventArgs e)
    {
        // TextBox caret placement and text selection must stay inside the editor.
        if (IsTextInput(e.OriginalSource)) return;
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _space))
        {
            CommitText(); _drag = Drag.Pan; _panOrigin = e.GetPosition(_viewport); _panBefore = _pan; _scene.CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        CommitText(); Point point = Clamp(e.GetPosition(_scene)); _origin = point; _originRect = _selection;
        _selectedMark = -1;
        if (_tool != CaptureTool.Select && !_selection.IsEmpty && _selection.Contains(point))
        {
            if (_tool == CaptureTool.Text) { EditText(Local(point)); e.Handled = true; return; }
            _drag = _tool == CaptureTool.Square || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Drag.Square : Drag.Rectangle;
        }
        else
        {
            _edge = EdgeAt(point);
            if (_edge != CaptureEdge.None) { SaveState(); _drag = Drag.Resize; }
            else if (!_selection.IsEmpty && _selection.Contains(point))
            {
                _selectedMark = MarkAt(Local(point));
                if (_selectedMark >= 0)
                {
                    var mark = _marks[_selectedMark]; _color = mark.Color; if (mark.IsText) _fontSize = mark.FontSize;
                    if (e.ClickCount == 2 && mark.IsText) { EditText(mark.Bounds.TopLeft, _selectedMark); e.Handled = true; return; }
                    SaveState(); _drag = Drag.Mark; _originRect = mark.Bounds;
                }
                else { SaveState(); _drag = Drag.Move; }
            }
            else
            {
                SaveState(); _drag = Drag.New; _marks.Clear(); _selection = new(point, point); _tool = CaptureTool.Select;
            }
        }
        _scene.CaptureMouse(); Update(); e.Handled = true;
    }
    private void MouseMoveOnScene(object sender, MouseEventArgs e)
    {
        Point point = Clamp(e.GetPosition(_scene));
        if (_drag == Drag.None) { UpdateCursor(point); return; }
        Vector delta = point - _origin;
        switch (_drag)
        {
            case Drag.Pan: _pan = _panBefore + (e.GetPosition(_viewport) - _panOrigin); LimitPan(); break;
            case Drag.New: _selection = new(_origin, point); break;
            case Drag.Move: _selection = CaptureEditing.Move(_originRect, delta, SceneSize); break;
            case Drag.Resize:
                var next = CaptureEditing.Resize(_originRect, delta, _edge, SceneSize); Vector shift = _selection.TopLeft - next.TopLeft;
                for (int n = 0; n < _marks.Count; n++) { var rect = _marks[n].Bounds; rect.Offset(shift); _marks[n] = _marks[n] with { Bounds = rect }; }
                _selection = next; break;
            case Drag.Mark:
                if (_selectedMark >= 0) _marks[_selectedMark] = _marks[_selectedMark] with { Bounds = CaptureEditing.Move(_originRect, delta, _selection.Size) };
                break;
            case Drag.Rectangle:
            case Drag.Square:
                _pending = new(CaptureEditing.Box(Local(_origin), Local(point), _selection.Size, _drag == Drag.Square), null, _color); break;
        }
        UpdateCursor(point); Update();
    }
    private void MouseUpOnScene(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None || IsTextInput(e.OriginalSource)) return;
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle) return;
        if (_pending is { Bounds.Width: >= 2, Bounds.Height: >= 2 } && _marks.Count < 500) { SaveState(); _marks.Add(_pending); _selectedMark = _marks.Count - 1; }
        _pending = null; _drag = Drag.None; _scene.ReleaseMouseCapture(); Update();
    }
    private CaptureEdge EdgeAt(Point point)
    {
        if (_selection.IsEmpty || _selection.Width < 2 || _selection.Height < 2) return CaptureEdge.None;
        Rect hit = _selection; double margin = 7 / _zoom; hit.Inflate(margin, margin); if (!hit.Contains(point)) return CaptureEdge.None;
        CaptureEdge edge = CaptureEdge.None;
        if (Math.Abs(point.X - _selection.Left) <= margin) edge |= CaptureEdge.Left;
        else if (Math.Abs(point.X - _selection.Right) <= margin) edge |= CaptureEdge.Right;
        if (Math.Abs(point.Y - _selection.Top) <= margin) edge |= CaptureEdge.Top;
        else if (Math.Abs(point.Y - _selection.Bottom) <= margin) edge |= CaptureEdge.Bottom;
        return edge;
    }
    private int MarkAt(Point point)
    {
        for (int n = _marks.Count - 1; n >= 0; n--)
        {
            var mark = _marks[n]; Rect hit = mark.Bounds; hit.Inflate(6 / _zoom, 6 / _zoom);
            if (!hit.Contains(point)) continue;
            Rect inside = mark.Bounds; inside.Inflate(-6 / _zoom, -6 / _zoom);
            if (mark.IsText || inside.IsEmpty || !inside.Contains(point)) return n;
        }
        return -1;
    }
    private void UpdateCursor(Point point)
    {
        if (_space || _drag == Drag.Pan) { _scene.Cursor = Cursors.Hand; return; }
        var edge = EdgeAt(point);
        _scene.Cursor = edge switch
        {
            CaptureEdge.Left or CaptureEdge.Right => Cursors.SizeWE,
            CaptureEdge.Top or CaptureEdge.Bottom => Cursors.SizeNS,
            _ when edge == (CaptureEdge.Left | CaptureEdge.Top) || edge == (CaptureEdge.Right | CaptureEdge.Bottom) => Cursors.SizeNWSE,
            _ when edge == (CaptureEdge.Left | CaptureEdge.Bottom) || edge == (CaptureEdge.Right | CaptureEdge.Top) => Cursors.SizeNESW,
            _ => _tool == CaptureTool.Text ? Cursors.IBeam : _tool == CaptureTool.Select && !_selection.IsEmpty && _selection.Contains(point) ? Cursors.SizeAll : Cursors.Cross
        };
    }
    private void EditText(Point local, int index = -1)
    {
        if (_marks.Count >= 500 && index < 0) return;
        _textPoint = local; _editingMark = index; var existing = index < 0 ? null : _marks[index];
        if (existing is not null) { _color = existing.Color; _fontSize = existing.FontSize; }
        double wrapWidth = existing is { WrapWidth: > 0 } ? existing.WrapWidth : Math.Max(2, Math.Min(300, _selection.Width - local.X));
        _textEditor = new TextBox
        {
            Text = existing?.Text ?? "", FontFamily = new FontFamily("Microsoft YaHei"), FontSize = _fontSize,
            Foreground = new SolidColorBrush(_color), Background = TextBackground(_color), Padding = new Thickness(0), BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(1), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 2000,
            Width = wrapWidth + 2 + SystemParameters.VerticalScrollBarWidth, MinHeight = _fontSize + 12,
            MaxHeight = Math.Max(_fontSize + 12, Math.Min(200, _selection.Height - local.Y)), VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Top,
            ToolTip = "Enter 完成文字；Shift+Enter 换行；Esc 取消本次编辑"
        };
        Canvas.SetLeft(_textEditor, _selection.X + local.X); Canvas.SetTop(_textEditor, _selection.Y + local.Y);
        _scene.Children.Add(_textEditor);
        _textEditor.ApplyTemplate(); _textEditor.UpdateLayout();
        if (_textEditor.Template?.FindName("PART_ContentHost", _textEditor) is ScrollViewer { ViewportWidth: > 0 } contentHost)
        {
            // Scrollbar width is theme dependent. Keep the text viewport stable when reopening a label.
            _textEditor.Width = Math.Max(2, _textEditor.Width + wrapWidth - contentHost.ViewportWidth); _textEditor.UpdateLayout();
        }
        _textEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CommitText(); e.Handled = true; }
        };
        if (ShowActivated) _textEditor.Focus(); _textEditor.CaretIndex = _textEditor.Text.Length; Update();
    }
    private static Brush TextBackground(Color color) => color.R * .299 + color.G * .587 + color.B * .114 > 180 ? new SolidColorBrush(Color.FromRgb(39, 47, 62)) : Brushes.White;
    private bool IsTextInput(object source) => _textEditor is not null && source is DependencyObject target &&
        (ReferenceEquals(target, _textEditor) || target is Visual && _textEditor.IsAncestorOf(target));
    private void CommitText()
    {
        if (_textEditor is null) return;
        string text = _textEditor.Text; int index = _editingMark;
        _textEditor.UpdateLayout();
        var host = _textEditor.Template?.FindName("PART_ContentHost", _textEditor) as ScrollViewer;
        double wrapWidth = host is { ViewportWidth: > 0 } ? host.ViewportWidth : _textEditor.Width - 2 - SystemParameters.VerticalScrollBarWidth;
        _scene.Children.Remove(_textEditor); _textEditor = null; _editingMark = -1;
        if (!string.IsNullOrWhiteSpace(text))
        {
            SaveState(); var mark = CaptureEditing.Label(_textPoint, text, _color, _fontSize, Math.Max(2, wrapWidth));
            if (index < 0) { _marks.Add(mark); _selectedMark = _marks.Count - 1; } else { _marks[index] = mark; _selectedMark = index; }
        }
        else if (index >= 0) { SaveState(); _marks.RemoveAt(index); _selectedMark = -1; }
        if (ShowActivated) Focus(); Update();
    }
    private void CancelText()
    {
        if (_textEditor is not null) _scene.Children.Remove(_textEditor); _textEditor = null; _editingMark = -1; if (ShowActivated) Focus(); Update();
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (_textEditor is not null) return;
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter) Finish(CaptureAction.Copy);
        else if (e.Key == Key.F3) Finish(CaptureAction.Pin);
        else if (e.Key == Key.Space) { _space = true; _scene.Cursor = Cursors.Hand; }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Undo();
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Finish(CaptureAction.Copy);
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Finish(CaptureAction.Save);
        else if (e.Key == Key.Delete) DeleteMark();
        else if (e.Key is Key.Add or Key.OemPlus) ZoomAt(_zoom * 1.2, Center());
        else if (e.Key is Key.Subtract or Key.OemMinus) ZoomAt(_zoom / 1.2, Center());
        else if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ResetZoom();
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.V or Key.T or Key.R or Key.S)
        {
            SelectTool(e.Key switch { Key.T => CaptureTool.Text, Key.R => CaptureTool.Rectangle, Key.S => CaptureTool.Square, _ => CaptureTool.Select });
        }
        else return;
        e.Handled = true;
    }

    private void Update()
    {
        if (double.IsNaN(_scene.Width) || double.IsNaN(_scene.Height)) return;
        _scene.RenderTransform = new TransformGroup { Children = { new ScaleTransform(_zoom, _zoom), new TranslateTransform(_pan.X, _pan.Y) } };
        bool valid = !_selection.IsEmpty && _selection.Width >= 2 && _selection.Height >= 2;
        _tips.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, SceneSize.Width, SceneSize.Height)));
        _outline.Visibility = _size.Visibility = _markLayer.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        _toolbar.Visibility = valid && _drag == Drag.None ? Visibility.Visible : Visibility.Collapsed;
        _zoomLabel.Text = $"{_zoom:P0}"; _fontLabel.Text = _fontSize.ToString("0");
        foreach (var pair in _toolButtons) pair.Value.FontWeight = pair.Key == _tool ? FontWeights.Bold : FontWeights.Normal;
        foreach (var handle in _handles) handle.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        if (valid)
        {
            geometry.Children.Add(new RectangleGeometry(_selection));
            Canvas.SetLeft(_outline, _selection.X); Canvas.SetTop(_outline, _selection.Y); _outline.Width = _selection.Width; _outline.Height = _selection.Height; _outline.StrokeThickness = 1.5 / _zoom;
            _markLayer.Width = _selection.Width; _markLayer.Height = _selection.Height; Canvas.SetLeft(_markLayer, _selection.X); Canvas.SetTop(_markLayer, _selection.Y);
            _markLayer.Marks = _marks; _markLayer.Pending = _pending; _markLayer.Selected = _selectedMark; _markLayer.Editing = _editingMark; _markLayer.Zoom = _zoom; _markLayer.InvalidateVisual();
            Point[] points = [_selection.TopLeft, new(_selection.Left + _selection.Width / 2, _selection.Top), _selection.TopRight,
                new(_selection.Right, _selection.Top + _selection.Height / 2), _selection.BottomRight,
                new(_selection.Left + _selection.Width / 2, _selection.Bottom), _selection.BottomLeft, new(_selection.Left, _selection.Top + _selection.Height / 2)];
            for (int n = 0; n < _handles.Count; n++)
            {
                var handle = _handles[n]; handle.Width = handle.Height = 7 / _zoom; handle.StrokeThickness = 1 / _zoom;
                Canvas.SetLeft(handle, points[n].X - handle.Width / 2); Canvas.SetTop(handle, points[n].Y - handle.Height / 2);
            }
            var pixels = DesktopCapture.PixelRect(_selection, SceneSize.Width, SceneSize.Height, _frame.Image);
            _size.Text = $"{pixels.Width} × {pixels.Height} px · 预览 {_zoom:P0} · 标注 {_marks.Count}";
            Point top = Screen(_selection.TopLeft), bottom = Screen(_selection.BottomRight);
            Canvas.SetLeft(_size, Math.Clamp(top.X, 4, Math.Max(4, _viewport.ActualWidth - 300))); Canvas.SetTop(_size, Math.Clamp(top.Y - 30, 4, Math.Max(4, _viewport.ActualHeight - 30)));
            _toolbar.Measure(new Size(Math.Max(100, _viewport.ActualWidth - 16), double.PositiveInfinity));
            double width = _toolbar.DesiredSize.Width, height = _toolbar.DesiredSize.Height;
            Canvas.SetLeft(_toolbar, Math.Clamp(bottom.X - width, 4, Math.Max(4, _viewport.ActualWidth - width - 4)));
            Canvas.SetTop(_toolbar, Math.Clamp(bottom.Y + 9, 4, Math.Max(4, _viewport.ActualHeight - height - 4)));
        }
        _shade.Data = geometry;
    }

    public void SelectForVerification(Rect rect) { _selection = rect; Update(); }
    internal void MoveForVerification(Vector delta) { SaveState(); _selection = CaptureEditing.Move(_selection, delta, SceneSize); Update(); }
    internal void ResizeForVerification(Vector delta, CaptureEdge edge)
    {
        SaveState(); var next = CaptureEditing.Resize(_selection, delta, edge, SceneSize); Vector shift = _selection.TopLeft - next.TopLeft;
        for (int n = 0; n < _marks.Count; n++) { var rect = _marks[n].Bounds; rect.Offset(shift); _marks[n] = _marks[n] with { Bounds = rect }; }
        _selection = next; Update();
    }
    internal void TextForVerification(Point point, string text, Color color)
    {
        EditText(point); _textEditor!.Text = text; SetColor(color); CommitText();
    }
    internal void EditTextForVerification(int index, string text)
    {
        EditText(_marks[index].Bounds.TopLeft, index); _textEditor!.Text = text; CommitText();
    }
    internal void BoxForVerification(Rect rect, Color color) { SaveState(); _marks.Add(new(rect, null, color)); _selectedMark = _marks.Count - 1; Update(); }
    internal void ColorForVerification(Color color) => SetColor(color);
    internal void UndoForVerification() => Undo();
    internal void ZoomForVerification(double zoom) => ZoomAt(zoom, Center());
    public BitmapSource SelectedImage(bool includeAnnotations = true)
    {
        CommitText(); var rect = DesktopCapture.PixelRect(_selection, SceneSize.Width, SceneSize.Height, _frame.Image); var raw = DesktopCapture.Crop(_frame.Image, rect);
        var scale = new Vector(_frame.Image.PixelWidth / SceneSize.Width, _frame.Image.PixelHeight / SceneSize.Height);
        var offset = new Vector(_selection.Left * scale.X - rect.X, _selection.Top * scale.Y - rect.Y);
        return includeAnnotations ? CaptureEditing.Composite(raw, scale, offset, _marks) : raw;
    }
    public void Finish(CaptureAction action)
    {
        if (_selection.IsEmpty || _selection.Width < 2 || _selection.Height < 2) return;
        CommitText(); OriginalImage = SelectedImage(false); var image = SelectedImage(); Close(); Completed?.Invoke(image, action);
    }
}
