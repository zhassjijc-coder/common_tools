using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LinkLab.Core;

namespace LinkLab.App;

internal static class NavigationScrollSmoke
{
    internal static async Task Run(ShellViewModel vm, MainWindow window, string output)
    {
        double width = window.Width, height = window.Height;
        window.Width = 1080; window.Height = 720;
        var list = (ListBox)window.FindName("ModuleNavigationList");
        var settings = (Button)window.FindName("SystemSettingsButton");
        await Layout(window);
        double bottom = settings.TranslatePoint(new Point(), window).Y;
        var menuScroll = App.Descendants<ScrollViewer>(list).First();
        menuScroll.ScrollToEnd(); await Layout(window);
        Check(Math.Abs(settings.TranslatePoint(new Point(), window).Y - bottom) < 0.1 && settings.IsVisible, "Settings moved with sidebar scrolling");
        Check(bottom > list.TranslatePoint(new Point(0, list.ActualHeight), window).Y, "Settings not below module list");
        settings.Command.Execute(settings.CommandParameter); await Layout(window);
        Check(vm.IsSettingsSelected && list.SelectedItem is null, "Settings and module selection disagree");
        if (!vm.VirtualSerialVisible)
        {
            vm.Navigate("virtualserial"); await Layout(window);
            Check(vm.SelectedNav.Key == "home" && ReferenceEquals(vm.CurrentPage, vm.Home), "Hidden module navigation did not fall back to home");
            Check(!vm.Navigation.Any(item => item.Key == "virtualserial") && vm.ToolCount == vm.Home.Tiles.Length, "Hidden module remained in the navigation/count");
            Check(!App.Descendants<Button>(window).Any(button => Equals(button.CommandParameter, "virtualserial") && button.IsVisible), "Hidden module still has a visible home entry");
            Check(App.Descendants<TextBlock>(window).Single(block => block.Name == "HomeToolCount").Text == vm.ToolCount.ToString(), "Home count not updated");
            var homeTiles = App.Descendants<ItemsControl>(window).Single(control => ReferenceEquals(control.ItemsSource, vm.Home.Tiles));
            var networkCard = App.Descendants<Button>(homeTiles).Single(button => Equals(button.CommandParameter, "netconfig"));
            Check(networkCard.IsVisible && App.Descendants<Button>(window).Count(button => Equals(button.CommandParameter, "netconfig")) == 1, "Network configuration must have one home card and no separate banner");
            networkCard.Command.Execute(networkCard.CommandParameter); await Layout(window);
            Check(ReferenceEquals(vm.CurrentPage, vm.NetworkConfig) && ReferenceEquals(list.SelectedItem, vm.SelectedNav), "Network configuration navigation failed");
            Check(!App.Descendants<TextBlock>(window).Any(block => block.Text.Contains("支持卓岚 ZLAN 设备")), "Removed network description remained visible");
            vm.Navigate("serial"); await Layout(window);
            Check(!vm.IsSettingsSelected && ReferenceEquals(list.SelectedItem, vm.SelectedNav), "Visible module selection did not recover");
            Check(vm.VersionLabel == "LINKLAB  /  1.0", "Unexpected application version");
            window.Width = width; window.Height = height; vm.Navigate("home"); await Layout(window);
            File.WriteAllText(Path.Combine(output, "navigation-scroll-result.txt"), $"PASS: virtual serial hidden from sidebar and home; legacy route falls back to home; home shows {vm.ToolCount} tools including network configuration; visible module selection and pinned settings work at minimum window size; version remains 1.0.\n");
            return;
        }
        vm.Navigate("virtualserial"); await Layout(window);
        while (!vm.VirtualSerial.RefreshCommand.CanExecute(null)) await Task.Delay(10);
        await Layout(window);
        Check(!vm.IsSettingsSelected && ReferenceEquals(list.SelectedItem, vm.SelectedNav), "Module selection did not recover");
        Check(vm.VersionLabel == "LINKLAB  /  1.0", "Unexpected application version");

        var page = App.Descendants<ScrollViewer>(window).Single(x => ScrollChain.GetEnabled(x));
        var grid = App.Descendants<DataGrid>(page).Single();
        var gridScroll = App.Descendants<ScrollViewer>(grid).First();
        var log = App.Descendants<TextBox>(page).Single(x => x.Name == "PortLog");
        var create = App.Descendants<Button>(page).Single(x => x.Name == "CreatePortPairButton");
        var status = App.Descendants<TextBlock>(page).Single(x => x.Name == "PortOperationStatus");
        var portA = App.Descendants<ComboBox>(page).Single(x => x.Name == "CreatePortACombo");
        var portB = App.Descendants<ComboBox>(page).Single(x => x.Name == "CreatePortBCombo");
        Check(!portA.IsEditable && !portB.IsEditable && portA.Items.Count > 0 && portB.Items.Count > 0, "Free port dropdowns missing or editable");
        Check((string?)portA.SelectedItem == vm.VirtualSerial.PortA && (string?)portB.SelectedItem == vm.VirtualSerial.PortB, "Initial dropdown selections not displayed");
        string previousA = vm.VirtualSerial.PortA;
        string changedA = portA.Items.Cast<string>().First(port => port != previousA);
        portA.SetCurrentValue(ComboBox.SelectedItemProperty, changedA); await Layout(window);
        Check(vm.VirtualSerial.PortA == changedA && !portB.Items.Contains(changedA) && portB.Items.Contains(previousA) && (string?)portB.SelectedItem == vm.VirtualSerial.PortB, "Dropdown mutual exclusion broke bindings");
        portA.SetCurrentValue(ComboBox.SelectedItemProperty, previousA); await Layout(window);
        portA.IsDropDownOpen = true; await Layout(window);
        var popup = (Popup)portA.Template.FindName("PART_Popup", portA);
        Check(popup.Child is not null && App.Descendants<VirtualizingStackPanel>(popup.Child).Any(), "Dropdown has no virtualizing panel");
        Check(App.Descendants<ComboBoxItem>(popup.Child!).Count() < 100, "Dropdown rendered all COM names at once");
        portA.IsDropDownOpen = false; await Layout(window);
        Check(status.Text == vm.VirtualSerial.Status && ReferenceEquals(create.Command, vm.VirtualSerial.CreateCommand), "Create/status binding missing");
        Check(status.TranslatePoint(new Point(), page).Y > create.TranslatePoint(new Point(), page).Y &&
            status.TranslatePoint(new Point(0, status.ActualHeight), page).Y < grid.TranslatePoint(new Point(), page).Y,
            "Create feedback is not next to the action above the table");
        var logScroll = App.Descendants<ScrollViewer>(log).First();
        var originalPairs = vm.VirtualSerial.Pairs.ToArray(); var originalSelection = vm.VirtualSerial.Selected;
        vm.VirtualSerial.Pairs.Clear(); vm.VirtualSerial.Selected = null;
        await Middle(page, window);
        double before = page.VerticalOffset;
        Wheel(gridScroll, -120); await Layout(window);
        Check(page.VerticalOffset > before, "Empty table swallowed downward wheel");
        before = page.VerticalOffset;
        Wheel(gridScroll, 120); await Layout(window);
        Check(page.VerticalOffset < before, "Empty table swallowed upward wheel");

        for (int i = 0; i < 30; i++) vm.VirtualSerial.Pairs.Add(new(new(i, $"COM{100 + i * 2}", $"COM{101 + i * 2}"), false));
        await Middle(page, window); gridScroll.ScrollToTop(); await Layout(window);
        before = page.VerticalOffset;
        Wheel(gridScroll, -120); await Layout(window);
        Check(gridScroll.VerticalOffset > 0 && page.VerticalOffset == before, "Populated table lost native scrolling");
        gridScroll.ScrollToBottom(); await Layout(window);
        Wheel(gridScroll, -120); await Layout(window);
        Check(page.VerticalOffset > before, "Table bottom swallowed wheel");
        vm.VirtualSerial.Pairs.Clear(); vm.VirtualSerial.Selected = null;

        log.SetCurrentValue(TextBox.TextProperty, string.Join('\n', Enumerable.Range(0, 100).Select(i => $"Test log line {i}")));
        await Middle(page, window); logScroll.ScrollToTop(); await Layout(window);
        before = page.VerticalOffset;
        Wheel(logScroll, -120); await Layout(window);
        Check(logScroll.VerticalOffset > 0 && page.VerticalOffset == before, "Log lost native scrolling");
        logScroll.ScrollToBottom(); await Layout(window);
        Wheel(logScroll, -120); await Layout(window);
        Check(page.VerticalOffset > before, "Log bottom swallowed wheel");
        logScroll.ScrollToTop(); await Layout(window);
        before = page.VerticalOffset;
        Wheel(logScroll, 120); await Layout(window);
        Check(page.VerticalOffset < before, "Log top swallowed wheel");
        log.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();

        await Middle(page, window);
        var bar = (ScrollBar)page.Template.FindName("PART_VerticalScrollBar", page);
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        before = page.VerticalOffset;
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 24) { RoutedEvent = Thumb.DragDeltaEvent });
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 24, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Layout(window);
        Check(page.VerticalOffset > before, "Scrollbar drag did not move the page");
        double dragged = page.VerticalOffset; await Layout(window);
        Check(page.VerticalOffset == dragged, "Scrollbar jumped after layout");
        foreach (var pair in originalPairs) vm.VirtualSerial.Pairs.Add(pair);
        vm.VirtualSerial.Selected = originalSelection;
        await App.Capture(window, Path.Combine(output, "virtual-serial-scroll-compact.png"));
        window.Width = width; window.Height = height; vm.Navigate("home"); await Layout(window);
        File.WriteAllText(Path.Combine(output, "navigation-scroll-result.txt"), "PASS: automatic port detection on navigation; noneditable dropdowns, initial selections, live mutual exclusion and virtualized popup; pinned settings at minimum window size; selection switching; version 1.0; empty table wheel; populated table and long log native scroll / edge chaining; scrollbar thumb drag without bounce.\n");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException(message); }
    private static async Task Layout(Window window) { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static async Task Middle(ScrollViewer page, Window window) { await Layout(window); page.ScrollToVerticalOffset(page.ScrollableHeight / 2); await Layout(window); }
    private static void Wheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(args);
        if (!args.Handled) { args.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(args); }
    }
}
