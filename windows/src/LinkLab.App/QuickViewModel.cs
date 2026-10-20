using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LinkLab.Core;
using Microsoft.Win32;

namespace LinkLab.App;

public sealed class QuickVm : PageVm
{
    public SystemSettingsVm System { get; internal set; } = null!;
    private readonly ICaptureHotkeys _hotkeys;
    private readonly List<Window> _floating = [];
    private readonly CancellationTokenSource _stop = new();
    private CaptureOverlay? _overlay;
    private bool _enabled, _capturing, _recognizing, _closed;
    private BitmapSource? _lastImage, _originalImage, _copiedOcrSource;
    private uint _copiedSequence;
    private int _captureGeneration;
    private string _status = "截图快捷键已关闭 · 开启后使用 F1 截图，F3 贴图", _ocrText = "";
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled || _closed) return;
            try
            {
                if (value) _hotkeys.Enable(() => CaptureCommand.Execute(null), () => PinCommand.Execute(null));
                else { _captureGeneration++; _hotkeys.Dispose(); _overlay?.Close(); CloseFloating(); }
                Set(ref _enabled, value); Raise(nameof(CanCapture));
                Status = value ? "快捷键已开启 · F1 截图 / F3 贴图 · 最小化后仍可使用" : "快捷键已关闭，F1 / F3 已释放";
            }
            catch (Exception ex) { Hub.Error(ex); Status = ex.Message; Raise(nameof(Enabled)); }
        }
    }
    public bool CanCapture => Enabled && !_capturing;
    public bool CanOcr => LastImage is not null && !_recognizing;
    public bool HasImage => LastImage is not null;
    [Saved] public int Language { get; set; }
    public string[] Languages { get; } = ["简体中文 + English", "English", "简体中文"];
    public BitmapSource? LastImage { get => _lastImage; private set { Set(ref _lastImage, value); Raise(nameof(HasImage)); Raise(nameof(CanOcr)); Raise(nameof(ImageSize)); } }
    public string ImageSize => LastImage is null ? "还没有截图" : $"{LastImage.PixelWidth} × {LastImage.PixelHeight} px";
    public string Status { get => _status; set { Set(ref _status, value); Hub.Notice = value; } }
    public string OcrText { get => _ocrText; set => Set(ref _ocrText, value); }
    public AsyncCommand CaptureCommand { get; }
    public Command PinCommand { get; }
    public Command SaveCommand { get; }
    public Command CopyCommand { get; }
    public AsyncCommand OcrCommand { get; }
    public Command CopyTextCommand { get; }
    public Command ClosePinsCommand { get; }
    public Command OpenImageCommand { get; }
    public QuickVm(Hub hub, ICaptureHotkeys? hotkeys = null) : base(hub)
    {
        _hotkeys = hotkeys ?? new CaptureHotkeys();
        CaptureCommand = Async(Capture); PinCommand = Safe(PastePin); SaveCommand = Safe(() => SaveImage(NeedImage())); CopyCommand = Safe(() => CopyImage(NeedImage(), NeedOcrImage()));
        OcrCommand = Async(() => RecognizeLatest(true)); CopyTextCommand = Safe(() => CopyText(OcrText)); ClosePinsCommand = Safe(CloseFloating);
        OpenImageCommand = Safe(OpenImage);
    }
    private Command Safe(Action a) => new(() => { try { a(); } catch (Exception ex) { Status = ex.Message; Hub.Error(ex); } });
    private BitmapSource NeedImage() => LastImage ?? throw new InvalidOperationException("请先截图或打开图片。");
    private BitmapSource NeedOcrImage() => _originalImage ?? NeedImage();
    public void SetImage(BitmapSource image, BitmapSource? original = null)
    {
        if (!image.IsFrozen) image.Freeze(); if (original is not null && !original.IsFrozen) original.Freeze();
        _originalImage = original ?? image; LastImage = image;
    }
    private async Task Capture()
    {
        if (!Enabled) throw new InvalidOperationException("请先开启截图快捷键。");
        if (_capturing) return; _capturing = true; Raise(nameof(CanCapture));
        int generation = _captureGeneration;
        var main = Application.Current.MainWindow; bool restore = main?.IsActive == true;
        try
        {
            if (restore) main!.Hide(); await Task.Delay(150, _stop.Token);
            if (!Enabled || _closed || generation != _captureGeneration)
            {
                _capturing = false; Raise(nameof(CanCapture)); if (restore && !_closed) main!.Show(); return;
            }
            var frame = DesktopCapture.Capture(); var overlay = new CaptureOverlay(frame); _overlay = overlay;
            overlay.Closed += (_, _) =>
            {
                _overlay = null; _capturing = false; Raise(nameof(CanCapture));
                if (restore && !_closed) { main!.ShowActivated = false; main.Show(); }
            };
            overlay.Completed += (image, action) => { SetImage(image, overlay.OriginalImage); DispatchAction(image, action); };
            overlay.Show();
        }
        catch (OperationCanceledException) when (_closed || !Enabled)
        { _capturing = false; _overlay = null; Raise(nameof(CanCapture)); if (restore && !_closed) main!.Show(); }
        catch { _capturing = false; _overlay = null; Raise(nameof(CanCapture)); if (restore && !_closed) main!.Show(); throw; }
    }
    private void DispatchAction(BitmapSource image, CaptureAction action)
    {
        try
        {
            switch (action)
            {
                case CaptureAction.Copy: CopyImage(image, NeedOcrImage()); break;
                case CaptureAction.Save: SaveImage(image); break;
                case CaptureAction.Pin: PinImage(image, ocrSource: NeedOcrImage()); break;
                case CaptureAction.Ocr: _ = RecognizeSafely(NeedOcrImage()); break;
            }
        }
        catch (Exception ex) { Hub.Error(ex); Status = ex.Message; }
    }
    private async Task RecognizeSafely(BitmapSource image) { try { await Recognize(image, true); } catch (Exception ex) { Hub.Error(ex); Status = ex.Message; } }
    public Task RecognizeLatest(bool showWindow) => Recognize(NeedOcrImage(), showWindow);
    public async Task Recognize(BitmapSource image, bool showWindow)
    {
        if (_recognizing) { Status = "正在识别，请稍候…"; return; }
        _recognizing = true; Raise(nameof(CanOcr)); Status = "正在本机识别文字…";
        try
        {
            string text = await LocalOcr.Recognize(image, Language, _stop.Token);
            if (_closed) return; OcrText = text; Status = text.Length == 0 ? "没有识别到文字，可重新选择清晰的文字区域" : $"识别完成 · {text.Length} 个字符 · 可编辑后复制";
            if (showWindow) Track(new OcrTextWindow(text, value => { try { CopyText(value); } catch (Exception ex) { Hub.Error(ex); } }));
        }
        finally { _recognizing = false; Raise(nameof(CanOcr)); }
    }
    private void PastePin()
    {
        if (!Enabled) throw new InvalidOperationException("请先开启截图快捷键。");
        if (_overlay is not null) { _overlay.Finish(CaptureAction.Pin); return; }
        uint sequence = DesktopCapture.ClipboardSequence;
        bool fromClipboard = Clipboard.ContainsImage();
        BitmapSource? image = fromClipboard ? Clipboard.GetImage() : LastImage;
        if (image is null) throw new InvalidOperationException("剪贴板中没有图片，请先按 F1 截图。");
        BitmapSource? original = fromClipboard ? sequence != 0 && sequence == _copiedSequence ? _copiedOcrSource : image : NeedOcrImage();
        SetImage(image, original); PinImage(image, ocrSource: original);
    }
    public PinnedImageWindow PinImage(BitmapSource image, bool show = true, BitmapSource? ocrSource = null)
    {
        if (_floating.OfType<PinnedImageWindow>().Count() >= 12) throw new InvalidOperationException("最多同时显示 12 张贴图，请先关闭部分贴图。");
        var source = ocrSource ?? (ReferenceEquals(image, LastImage) ? NeedOcrImage() : image);
        var window = new PinnedImageWindow(image, value => { try { CopyImage(value, source); } catch (Exception ex) { Hub.Error(ex); } }, value => _ = RecognizeSafely(source));
        Track(window, show); Status = "贴图已创建 · 拖动移动，滚轮 / ± 缩放，100% 重置，Ctrl+滚轮调透明度"; return window;
    }
    private void Track(Window window, bool show = true) { _floating.Add(window); window.Closed += (_, _) => _floating.Remove(window); if (show) window.Show(); }
    private void CloseFloating() { foreach (var window in _floating.ToArray()) window.Close(); _floating.Clear(); }
    private void CopyImage(BitmapSource image, BitmapSource? original = null)
    {
        Clipboard.SetImage(image); _copiedSequence = DesktopCapture.ClipboardSequence; _copiedOcrSource = original ?? image;
        Status = "截图与标注已复制到剪贴板 · 按 F3 可以贴图";
    }
    private void CopyText(string text) { if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("没有可复制的文字。"); Clipboard.SetText(text); Status = "文字已复制到剪贴板"; }
    private void SaveImage(BitmapSource image)
    {
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"LinkLab-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog() == true) { File.WriteAllBytes(dialog.FileName, DesktopCapture.Png(image)); Status = "截图已保存到 " + dialog.FileName; }
    }
    private void OpenImage()
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 50_000_000) throw new IOException("图片文件超过 50 MB。");
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(dialog.FileName); image.EndInit(); image.Freeze();
        if ((long)image.PixelWidth * image.PixelHeight > 50_000_000) throw new IOException("图片超过 5000 万像素。"); SetImage(image); Status = "图片已载入，可以识别文字或贴图";
    }
    public void Close() { _closed = true; _captureGeneration++; _stop.Cancel(); _hotkeys.Dispose(); _overlay?.Close(); CloseFloating(); _enabled = false; }
}
