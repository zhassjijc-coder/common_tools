using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkLab.Core;

namespace LinkLab.App;

internal static class ExtendedSmoke
{
    private sealed class NoopHotkeys : ICaptureHotkeys { public void Enable(Action capture, Action pin) { } public void Dispose() { } }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("UI smoke: " + message); }
    private static string FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); string p = ((IPEndPoint)l.LocalEndpoint).Port.ToString(); l.Stop(); return p; }
    internal static async Task Run(ShellViewModel vm, Window window, string output)
    {
        var report = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            await vm.Plc.Close(); vm.Plc.ProtocolIndex = i; vm.Plc.ServerPort = FreePort(); await vm.Plc.Loopback();
            Check(vm.Plc.ClientRows.Count == 8 && vm.Plc.ClientRows[0].Value == "100", "PLC demo read");
            vm.Plc.Values = "11 22 33 44 55 66 77 88"; await vm.Plc.Execute(true); await vm.Plc.Execute(false);
            Check(vm.Plc.ClientRows[7].Value == "88", "PLC write/read"); vm.Plc.Refresh(); vm.Plc.MemoryRows[0].Value = "999"; vm.Plc.Apply(); await vm.Plc.Execute(false);
            Check(vm.Plc.ClientRows[0].Value == "999", "PLC memory edit");
            vm.Plc.Interval = "100"; vm.Plc.PollCommand.Execute(null); await Task.Delay(250); vm.Plc.PollCommand.Execute(null);
            for (int n = 0; n < 60 && !vm.Plc.CanConfigure; n++) await Task.Delay(25);
            Check(vm.Plc.CanConfigure, "PLC poll stop"); vm.Plc.Interval = "1000";
            vm.Navigate("plc"); await App.Capture(window, Path.Combine(output, $"plc-{i}-client.png"));
            vm.Plc.Mode = 1; await App.Capture(window, Path.Combine(output, $"plc-{i}-server.png")); vm.Plc.Mode = 0;
        }
        await vm.Plc.Close(); vm.Plc.ProtocolIndex = 0; vm.Plc.ServerPort = FreePort(); await vm.Plc.Loopback(); report.Add("PLC: all 3 protocols, VM read/write, memory editing, polling and client/server views passed.");
        var secretProbe = new PasswordBox(); BindingOperations.SetBinding(secretProbe, PasswordBinding.ValueProperty, new Binding(nameof(MqttVm.Password)) { Source = vm.Mqtt, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); secretProbe.Password = "test-only-secret";
        Check(vm.Mqtt.Password == "test-only-secret", "PasswordBox binding"); var prefs = new Preferences(); prefs.Transfer("mqtt", vm.Mqtt, true); Check(!prefs.Values.ContainsKey("mqtt.Password") && !prefs.Values.ContainsKey("mqtt.CertificatePassword"), "MQTT template excludes secrets"); vm.Mqtt.Password = "";
        string? mqttPort = Environment.GetEnvironmentVariable("LINKLAB_SMOKE_MQTT_PORT");
        if (!string.IsNullOrEmpty(mqttPort))
        {
            vm.Mqtt.Port = mqttPort; await vm.Mqtt.Connect(); await vm.Mqtt.Subscribe(); await vm.Mqtt.Publish();
            for (int n = 0; n < 100; n++) { await Task.Delay(20); vm.Mqtt.Flush(); if (vm.Mqtt.Messages.Any(m => m.Direction == "RX")) break; }
            Check(vm.Mqtt.Messages.Any(m => m.Direction == "RX" && m.Text.Contains("demo-01")), "MQTT VM publish/subscription");
            vm.Mqtt.Selected = vm.Mqtt.Messages.First(m => m.Direction == "RX"); vm.Mqtt.Display = "JSON"; vm.Mqtt.Direction = "TX"; Check(vm.Mqtt.MessageView.Cast<MqttRecord>().All(m => m.Direction == "TX"), "MQTT filter"); vm.Mqtt.Direction = "全部";
            var disconnect = vm.Mqtt.Disconnect(); Check(vm.Mqtt.Active && ReferenceEquals(disconnect, vm.Mqtt.Disconnect()), "MQTT concurrent disconnect is joined");
            bool transitionRejected = false; try { await vm.Mqtt.Connect(); } catch (InvalidOperationException) { transitionRejected = true; }
            Check(transitionRejected, "MQTT reconnect waits for cleanup"); await disconnect; Check(vm.Mqtt.CanConfigure, "MQTT disconnect transition completed"); await vm.Mqtt.Connect();
            report.Add("MQTT: live local Broker connect, subscribe, publish, receive, filter and rapid disconnect/reconnect passed; password masking/binding and template exclusions passed.");
        }
        else report.Add("MQTT VM live broker check skipped (LINKLAB_SMOKE_MQTT_PORT not set).");
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 450));
            dc.DrawText(new FormattedText("MQTT Debug 12345", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 52, Brushes.Black, 1), new Point(48, 55));
            dc.DrawText(new FormattedText("设备联调工具 通信调试", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 46, Brushes.Black, 1), new Point(48, 145));
            dc.DrawText(new FormattedText("Temperature: 23.5 C", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 36, Brushes.Black, 1), new Point(48, 235));
        }
        var sample = new RenderTargetBitmap(1000, 450, 96, 96, PixelFormats.Pbgra32); sample.Render(drawing); sample.Freeze(); vm.Quick.SetImage(sample);
        await vm.Quick.Recognize(sample, false); Check(vm.Quick.OcrText.Contains("12345") && vm.Quick.OcrText.Contains("设备"), "offline Chinese/English OCR: " + vm.Quick.OcrText); File.WriteAllText(Path.Combine(output, "ocr-result.txt"), vm.Quick.OcrText);
        Check(DesktopCapture.PixelRect(new Rect(10, 20, 30, 40), 500, 225, sample) == new Int32Rect(20, 40, 60, 80), "DPI crop mapping");
        var desktop = DesktopCapture.Capture(); Check(desktop.Width > 0 && desktop.Image.PixelWidth == desktop.Width, "native desktop capture"); // Pixels are never saved by this check.
        var frame = new DesktopFrame(sample, 0, 0, 1000, 450);
        Check(CaptureEditing.Move(new Rect(20, 30, 300, 200), new Vector(-100, 500), new Size(1000, 450)) == new Rect(0, 250, 300, 200), "selection move stays on desktop");
        Check(CaptureEditing.Box(new Point(80, 80), new Point(10, 35), new Size(300, 200), true) == new Rect(35, 35, 45, 45), "square annotation supports reverse dragging");
        Check(CaptureEditing.Resize(new Rect(20, 30, 300, 200), new Vector(-80, -90), CaptureEdge.Left | CaptureEdge.Top, new Size(1000, 450)) == new Rect(0, 0, 320, 230), "selection corner resizing respects screen bounds");
        var overlay = new CaptureOverlay(frame, false) { Left = -16000, Top = 0, ShowActivated = false, Topmost = false }; overlay.Show(); await Task.Delay(100); overlay.SelectForVerification(new Rect(30, 30, 820, 300));
        overlay.MoveForVerification(new Vector(10, 8)); Check(overlay.Selection == new Rect(40, 38, 820, 300), "overlay selection moves");
        overlay.ResizeForVerification(new Vector(-5, -4), CaptureEdge.Left | CaptureEdge.Top); Check(overlay.Selection == new Rect(35, 34, 825, 304), "overlay selection corner resizes");
        var originalCrop = overlay.SelectedImage(false);
        overlay.BoxForVerification(new Rect(10, 10, 250, 90), Colors.Red); var redBox = DesktopCapture.Png(overlay.SelectedImage());
        overlay.ColorForVerification(Colors.Blue); Check(!DesktopCapture.Png(overlay.SelectedImage()).SequenceEqual(redBox), "existing rectangle can change color");
        overlay.UndoForVerification(); Check(DesktopCapture.Png(overlay.SelectedImage()).SequenceEqual(redBox), "annotation color undo restores exact pixels");
        overlay.TextForVerification(new Point(20, 170), "温度标记 23.5°C", Colors.Blue); Check(overlay.AnnotationCount == 2, "rectangle and Chinese text annotation");
        var originalLabel = DesktopCapture.Png(overlay.SelectedImage()); overlay.EditTextForVerification(1, "温度标记 25°C");
        Check(overlay.AnnotationCount == 2 && !DesktopCapture.Png(overlay.SelectedImage()).SequenceEqual(originalLabel), "existing text editing replaces the annotation");
        overlay.UndoForVerification(); Check(DesktopCapture.Png(overlay.SelectedImage()).SequenceEqual(originalLabel), "text edit undo restores exact content");
        string wrappedText = new('M', 80); overlay.TextForVerification(new Point(20, 40), wrappedText, Colors.Green); var wrappedImage = DesktopCapture.Png(overlay.SelectedImage());
        overlay.EditTextForVerification(2, wrappedText); Check(DesktopCapture.Png(overlay.SelectedImage()).SequenceEqual(wrappedImage), "multiline annotation keeps wrapping when entering, committing and reopening editor");
        overlay.UndoForVerification(); overlay.UndoForVerification(); Check(overlay.AnnotationCount == 2, "wrapped text undo leaves previous annotations");
        var annotatedCrop = overlay.SelectedImage(); Check(!DesktopCapture.Png(annotatedCrop).SequenceEqual(DesktopCapture.Png(originalCrop)), "export contains annotations");
        overlay.ZoomForVerification(2); Check(overlay.PreviewZoom == 2 && overlay.SelectedImage().PixelWidth == 825 && DesktopCapture.Png(overlay.SelectedImage()).SequenceEqual(DesktopCapture.Png(annotatedCrop)), "preview zoom preserves output resolution and pixels");
        await App.Capture(overlay, Path.Combine(output, "capture-zoom.png")); overlay.ZoomForVerification(1);
        await App.Capture(overlay, Path.Combine(output, "capture-selection.png"));
        vm.Quick.SetImage(annotatedCrop, originalCrop); await vm.Quick.RecognizeLatest(false);
        Check(vm.Quick.OcrText.Contains("12345") && !vm.Quick.OcrText.Contains("温度标记"), "recent capture OCR uses original before annotations");
        bool completed = false; overlay.Completed += (image, action) => completed = image.PixelWidth == 825 && image.PixelHeight == 304 && action == CaptureAction.Ocr && overlay.OriginalImage is not null && DesktopCapture.Png(overlay.OriginalImage).SequenceEqual(DesktopCapture.Png(originalCrop)); overlay.Finish(CaptureAction.Ocr); Check(completed, "capture completion keeps original OCR source and marked export");
        // 228 DIPs maps to whole physical pixels at standard Windows 25% DPI increments.
        // The synthetic source is exactly 2x this logical viewport; 225 DIPs rounded at 150%.
        var dpiDrawing = new DrawingVisual();
        using (var dc = dpiDrawing.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 456)); dc.DrawImage(sample, new Rect(0, 0, 1000, 450)); }
        var dpiSample = new RenderTargetBitmap(1000, 456, 96, 96, PixelFormats.Pbgra32); dpiSample.Render(dpiDrawing); dpiSample.Freeze();
        var dpiOverlay = new CaptureOverlay(new DesktopFrame(dpiSample, 0, 0, 1000, 456), false) { Width = 500, Height = 228, Left = -16000, Top = 0, ShowActivated = false, Topmost = false };
        dpiOverlay.Show(); await Task.Delay(80); dpiOverlay.SelectForVerification(new Rect(10.25, 8.5, 100.5, 60.25));
        var dpiCrop = dpiOverlay.SelectedImage(false); var dpiScale = VisualTreeHelper.GetDpi(dpiOverlay); var dpiContent = (FrameworkElement)dpiOverlay.Content;
        string dpiDetails = $"Window Width/Height={dpiOverlay.Width:R},{dpiOverlay.Height:R}; Actual={dpiOverlay.ActualWidth:R},{dpiOverlay.ActualHeight:R}; viewport={dpiContent.ActualWidth:R},{dpiContent.ActualHeight:R}; scene={dpiOverlay.DisplaySize.Width:R},{dpiOverlay.DisplaySize.Height:R}; DPI={dpiScale.DpiScaleX:R},{dpiScale.DpiScaleY:R}; source={dpiSample.PixelWidth},{dpiSample.PixelHeight}; selected={dpiOverlay.Selection}; crop={dpiCrop.PixelWidth},{dpiCrop.PixelHeight}";
        File.WriteAllText(Path.Combine(output, "capture-dpi-details.txt"), dpiDetails);
        Check(dpiCrop.PixelWidth == 202 && dpiCrop.PixelHeight == 121, "fractional selection maps from half-size logical canvas to full source pixels: " + dpiDetails);
        dpiOverlay.BoxForVerification(new Rect(5, 5, 20, 20), Colors.Magenta); var dpiMarked = dpiOverlay.SelectedImage(); var edgePixel = new byte[4];
        dpiMarked.CopyPixels(new Int32Rect(10, 20, 1, 1), edgePixel, 4, 0);
        Check(edgePixel[0] > 200 && edgePixel[1] < 80 && edgePixel[2] > 200, "annotation export uses physical pixel scale and fractional crop origin");
        dpiOverlay.ZoomForVerification(.5); Check(DesktopCapture.Png(dpiOverlay.SelectedImage()).SequenceEqual(DesktopCapture.Png(dpiMarked)), "zooming out preserves high-DPI marked pixels");
        dpiOverlay.Close();
        var pin = vm.Quick.PinImage(annotatedCrop, false, originalCrop); pin.WindowStartupLocation = WindowStartupLocation.Manual; pin.Left = -16000; pin.Top = 0; pin.Topmost = false; pin.ShowActivated = false; pin.Show();
        pin.ScaleForVerification(2); Check(pin.ImageScale == 2, "pin zoom enlarges"); pin.ScaleForVerification(.5); Check(pin.ImageScale == .5, "pin zoom shrinks"); pin.ScaleForVerification(1); Check(pin.ImageScale == 1, "pin original scale reset");
        await App.Capture(pin, Path.Combine(output, "pinned-image.png")); pin.Close();
        var interrupted = new QuickVm(new Hub(), new NoopHotkeys()) { Enabled = true }; interrupted.CaptureCommand.Execute(null); interrupted.Enabled = false; interrupted.Enabled = true;
        for (int n = 0; n < 60 && !interrupted.CaptureCommand.CanExecute(null); n++) await Task.Delay(20);
        Check(interrupted.CanCapture && !Application.Current.Windows.OfType<CaptureOverlay>().Any(), "disable/re-enable during capture delay cancels the old capture without opening an overlay"); interrupted.Close();
        var textWindow = new OcrTextWindow(vm.Quick.OcrText, _ => { }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, Topmost = false, ShowActivated = false }; textWindow.Show(); await App.Capture(textWindow, Path.Combine(output, "ocr-window.png")); textWindow.Close();
        using var hotkeys = new CaptureHotkeys(); int hits = 0;
        try
        {
            hotkeys.Enable(() => hits++, () => hits += 10); hotkeys.TriggerForVerification(101); hotkeys.TriggerForVerification(103); Check(hits == 11, "hotkey message dispatch");
            using var competitor = new CaptureHotkeys(); bool rejected = false; try { competitor.Enable(() => { }, () => { }); } catch (InvalidOperationException) { rejected = true; } Check(rejected, "hotkey conflict"); hotkeys.Dispose(); competitor.Enable(() => { }, () => { }); report.Add("Hotkeys: F1/F3 registration, WM_HOTKEY dispatch, conflict detection and release/re-register passed.");
        }
        catch (InvalidOperationException ex)
        {
            report.Add("Default F1/F3 could not be registered: " + ex.Message);
            hotkeys.Dispose(); using var alternative = new CaptureHotkeys(); int alternateHits = 0;
            alternative.Enable(() => alternateHits++, () => alternateHits += 10, 0x86, 0x87); alternative.TriggerForVerification(101); alternative.TriggerForVerification(103); Check(alternateHits == 11, "alternate hotkey dispatch"); alternative.Dispose(); alternative.Enable(() => { }, () => { }, 0x86, 0x87);
            report.Add("Hotkey engine dispatch and release/re-register verified with temporary F23/F24; existing F1/F3 ownership was preserved.");
        }
        report.Add("Capture: native virtual-desktop capture, DPI crop mapping, bounded selection movement/corner resize, reverse square drawing, Chinese text/rectangle annotation, existing annotation recoloring/undo, zoom-independent marked export, original-source OCR and pin zoom/reset passed. Clipboard was not modified by tests.");
        File.WriteAllLines(Path.Combine(output, "extended-result.txt"), report);
    }
}
