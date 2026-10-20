using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;
using Rect = System.Windows.Rect;

namespace LinkLab.App;

public sealed record DesktopFrame(BitmapSource Image, int Left, int Top, int Width, int Height);
public static class DesktopCapture
{
    public static DesktopFrame Capture()
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        if (w <= 0 || h <= 0 || (long)w * h > 100_000_000) throw new InvalidOperationException("屏幕尺寸无效或超过 1 亿像素。");
        nint screen = GetDC(0), dc = 0, bitmap = 0, previous = 0;
        if (screen == 0) throw new Win32Exception();
        try
        {
            dc = CreateCompatibleDC(screen); if (dc == 0) throw new Win32Exception();
            bitmap = CreateCompatibleBitmap(screen, w, h); if (bitmap == 0) throw new Win32Exception();
            previous = SelectObject(dc, bitmap);
            if (!BitBlt(dc, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000)) throw new Win32Exception();
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze();
            return new(image, x, y, w, h);
        }
        finally { if (previous != 0) SelectObject(dc, previous); if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc); ReleaseDC(0, screen); }
    }
    public static Int32Rect PixelRect(Rect selection, double displayWidth, double displayHeight, BitmapSource bitmap)
    {
        if (displayWidth <= 0 || displayHeight <= 0 || selection.IsEmpty) throw new ArgumentException("请先选择截图区域。");
        int left = Math.Clamp((int)Math.Floor(selection.Left * bitmap.PixelWidth / displayWidth), 0, bitmap.PixelWidth - 1);
        int top = Math.Clamp((int)Math.Floor(selection.Top * bitmap.PixelHeight / displayHeight), 0, bitmap.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(selection.Right * bitmap.PixelWidth / displayWidth), left + 1, bitmap.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom * bitmap.PixelHeight / displayHeight), top + 1, bitmap.PixelHeight);
        return new(left, top, right - left, bottom - top);
    }
    public static BitmapSource Crop(BitmapSource image, Int32Rect rect) { var crop = new CroppedBitmap(image, rect); crop.Freeze(); return crop; }
    public static byte[] Png(BitmapSource image) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray(); }
    internal static uint ClipboardSequence => GetClipboardSequenceNumber();
    public static void Position(Window window, DesktopFrame frame) { nint handle = new WindowInteropHelper(window).Handle; if (!SetWindowPos(handle, new nint(-1), frame.Left, frame.Top, frame.Width, frame.Height, 0x0040)) throw new Win32Exception(); }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}

public interface ICaptureHotkeys : IDisposable
{
    void Enable(Action capture, Action pin);
}

public sealed class CaptureHotkeys : ICaptureHotkeys
{
    private HwndSource? _source;
    void ICaptureHotkeys.Enable(Action capture, Action pin) => Enable(capture, pin);
    public void Enable(Action capture, Action pin, uint captureKey = 0x70, uint pinKey = 0x72)
    {
        if (_source is not null) return;
        var source = new HwndSource(new HwndSourceParameters("LinkLab capture hotkeys") { ParentWindow = new nint(-3), WindowStyle = 0 });
        bool f1 = RegisterHotKey(source.Handle, 101, 0x4000, captureKey); int firstError = f1 ? 0 : Marshal.GetLastWin32Error();
        bool f3 = RegisterHotKey(source.Handle, 103, 0x4000, pinKey); int secondError = f3 ? 0 : Marshal.GetLastWin32Error();
        if (!f1 || !f3)
        { if (f1) UnregisterHotKey(source.Handle, 101); if (f3) UnregisterHotKey(source.Handle, 103); source.Dispose(); int error = !f1 ? firstError : secondError; throw new InvalidOperationException(error == 1409 ? $"{(!f1 ? "F1" : "F3")} 已被其他程序占用。请退出占用热键的截图程序后再开启。" : $"热键注册失败（Windows {error}）：{new Win32Exception(error).Message}"); }
        source.AddHook((nint hwnd, int msg, nint wparam, nint lparam, ref bool handled) =>
        { if (msg == 0x0312) { handled = true; if (wparam == 101) capture(); else if (wparam == 103) pin(); } return 0; });
        _source = source;
    }
    public void Dispose() { if (_source is null) return; UnregisterHotKey(_source.Handle, 101); UnregisterHotKey(_source.Handle, 103); _source.Dispose(); _source = null; }
    internal void TriggerForVerification(int id) { if (_source is null) throw new InvalidOperationException(); SendMessage(_source.Handle, 0x0312, id, 0); }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}

public static class LocalOcr
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<string> Recognize(BitmapSource image, int language, CancellationToken ct = default)
    {
        if ((long)image.PixelWidth * image.PixelHeight > 16_000_000) throw new ArgumentException("识别区域超过 1600 万像素，请缩小选区以保证识别速度。");
        byte[] png = DesktopCapture.Png(image); await Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                string data = Path.Combine(AppContext.BaseDirectory, "tessdata");
                try
                {
                    using var engine = new TesseractEngine(data, language switch { 1 => "eng", 2 => "chi_sim", _ => "chi_sim+eng" }, EngineMode.LstmOnly);
                    using var pix = Pix.LoadFromMemory(png);
                    using var page = engine.Process(pix, PageSegMode.Auto); string result = page.GetText().Trim(); ct.ThrowIfCancellationRequested(); return result;
                }
                catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
                { throw new InvalidOperationException("OCR 引擎加载失败，请确认 x64 原生 DLL 与 tessdata 完整，并安装 Microsoft Visual C++ 2015–2022 x64 运行库。", ex); }
            }, ct);
        }
        finally { Gate.Release(); }
    }
}
