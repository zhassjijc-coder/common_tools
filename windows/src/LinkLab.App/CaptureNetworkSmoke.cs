using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkLab.App;

// Synthetic images and fake devices only: no hotkeys, desktop capture or LAN writes.
internal static class CaptureNetworkSmoke
{
    private static void Check(bool value, string message) { if (!value) throw new IOException("Capture/network smoke: " + message); }
    internal static async Task Run(string output)
    {
        var settingsBefore = File.Exists(Preferences.FilePath) ? File.ReadAllBytes(Preferences.FilePath) : null;
        var shell = new ShellViewModel(false, SystemSettingsSmoke.CreateRegistration());
        var window = new MainWindow(shell, false) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            await NetworkConfigSmoke.Run(shell, window, output);
            foreach (var (width, height) in new[] { (200, 100), (80, 800), (3000, 100), (1200, 1600) })
            {
                var pixels = new byte[width * height * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i + 1] = 110; pixels[i + 2] = 40; pixels[i + 3] = 255; }
                var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
                BitmapSource? copied = null, recognized = null;
                var pin = new PinnedImageWindow(source, image => copied = image, image => recognized = image) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, Topmost = false, ShowActivated = false };
                pin.Show();
                try
                {
                    foreach (double scale in new[] { .25, .5, 1d, 2d, 8d })
                    {
                        pin.ScaleForVerification(scale); pin.UpdateLayout();
                        var picture = App.Descendants<Image>(pin).Single();
                        Check(Math.Abs(picture.Width / picture.Height - (double)width / height) < .001, "image aspect ratio changed");
                        Check(pin.ResizeMode == ResizeMode.NoResize && !App.Descendants<ScrollViewer>(pin).Any(), "pin is resizable or contains a scrolling viewport");
                        Check(picture.ActualWidth + 1 >= picture.Width && picture.ActualHeight + 1 >= picture.Height, "image is clipped by its viewport");
                    }
                    double before = pin.ImageScale;
                    pin.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), Environment.TickCount, Key.OemMinus) { RoutedEvent = Keyboard.KeyDownEvent });
                    Check(pin.ImageScale == before, "keyboard unexpectedly resized the image");
                    pin.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    Check(pin.ImageScale < before, "wheel did not zoom the image");
                    var buttons = App.Descendants<Button>(pin).ToArray();
                    Check(buttons.Length == 2 && Equals(buttons[0].Content, "复制") && Equals(buttons[1].Content, "识别"), "toolbar contains unexpected actions");
                    foreach (var button in buttons) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(ReferenceEquals(copied, source) && ReferenceEquals(recognized, source), "copy/OCR used a resized image");
                    if (width == 200) await App.Capture(pin, Path.Combine(output, "pinned-proportional.png"));
                }
                finally { pin.Close(); }
            }
            File.WriteAllText(Path.Combine(output, "capture-network-result.txt"), "PASS: proportional wheel-only pin zoom; no resize frame or scrollbars; wide/tall/large image bounds; original image copy/OCR; selected-device reboot UI and fake backend. No desktop pixels were captured, no hotkeys were registered, no physical device commands were sent.\n");
        }
        finally
        {
            await shell.Close(false); window.Close();
            var settingsAfter = File.Exists(Preferences.FilePath) ? File.ReadAllBytes(Preferences.FilePath) : null;
            Check(settingsBefore is null ? settingsAfter is null : settingsAfter is not null && settingsBefore.SequenceEqual(settingsAfter), "verification changed user preferences");
        }
    }
}
