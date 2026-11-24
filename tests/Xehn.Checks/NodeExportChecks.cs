using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xehn.App;

internal static class NodeExportChecks
{
    public static void Run(Action<bool, string> check)
    {
        var protocol = "wg";
        var snapshot = "{}";
        var service = new ExportFixture(); var clipboard = new ClipboardFixture();
        var timer = NewTimer(); var delayCount = 0; var error = "";
        using var vm = new NodeExportViewModel(service, () => snapshot, () => protocol, () => true, message => error = message,
            clipboard, (duration, token) => { check(duration == TimeSpan.FromSeconds(2), "node copy feedback lasts two seconds"); delayCount++; return timer.Task.WaitAsync(token); });
        check(vm.Format == "clash" && vm.CanCopy && !vm.Copied, "node export defaults to compatible Clash format");
        check(vm.Hint.Contains("全部有效缓存节点") && vm.Hint.Contains("固定节点不限制"), "export tooltip explains full-cache scope independent of connection pin");
        var first = vm.CopyAsync();
        check(vm.Copied && !first.IsCompleted && clipboard.Text == service.Result && clipboard.Writes == 1, "successful copy shows success only after writing clipboard");
        timer.SetResult(); PumpUntil(() => first.IsCompleted);
        check(!vm.Copied && vm.CanCopy, "success mark returns to copy without changing control availability");
        timer = NewTimer(); var old = vm.CopyAsync();
        vm.Format = "singbox";
        PumpUntil(() => old.IsCompleted);
        check(!vm.Copied && vm.CanCopy, "format change clears old success and cancels its timer");
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.CopyAsync();
        check(vm.IsCopying && !vm.CanCopy && !vm.Copied, "pending export cannot show premature success or duplicate requests");
        var calls = service.Calls; vm.CopyAsync().GetAwaiter().GetResult();
        check(service.Calls == calls, "double click does not start another exporter");
        protocol = "awg"; vm.RefreshFormats(); service.Pending.SetResult("old-protocol-config"); service.Pending = null;
        PumpUntil(() => pending.IsCompleted);
        check(clipboard.Writes == 2 && !vm.Copied && vm.CanCopy && vm.Format == "clash" && vm.Formats.Count == 1, "protocol switch filters unsupported formats, selects Clash and rejects late clipboard result");
        vm.Format = "xray";
        check(vm.Format == "clash", "hidden formats cannot be selected programmatically");
        protocol = "unsupported"; vm.RefreshFormats();
        check(vm.Formats.Count == 0 && !vm.CanChooseFormat && !vm.CanCopy, "protocol with no export formats greys both controls");
        vm.CopyAsync().GetAwaiter().GetResult();
        check(service.Calls == calls, "unsupported protocol/format does not start worker");
        protocol = "wg"; vm.RefreshFormats();
        check(vm.Formats.Count == 3 && vm.Format == "clash" && vm.CanChooseFormat && vm.CanCopy, "returning to WireGuard restores three formats and both controls");
        vm.Format = "clash"; clipboard.Fail = true;
        vm.CopyAsync().GetAwaiter().GetResult();
        check(!vm.Copied && error.Contains("剪贴板") && delayCount == 2, "clipboard failure reports error without green tick or feedback timer");
        clipboard.Fail = false; service.Failure = new IOException("暂无有效节点缓存"); error = "";
        vm.CopyAsync().GetAwaiter().GetResult();
        check(!vm.Copied && error.Contains("缓存") && clipboard.Writes == 2, "missing cache leaves clipboard intact and gives actionable error");
        service.Failure = null; service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleSettings = vm.CopyAsync(); snapshot = "{\"ip_family\":\"v6\"}";
        service.Pending.SetResult("old-family-config"); service.Pending = null; PumpUntil(() => staleSettings.IsCompleted);
        check(clipboard.Writes == 2, "settings changes during export cannot copy stale address-family snapshot");
        var closing = vm.CopyAsync(); vm.Dispose(); PumpUntil(() => closing.IsCompleted);
        check(!vm.CanCopy, "closing cancels pending feedback cleanly");
    }

    public static void PreviewCopied(MainWindow window, Action snapshot, Action<bool, string> check)
    {
        var timer = NewTimer();
        var protocol = "wg";
        using var vm = new NodeExportViewModel(new ExportFixture(), () => "{}", () => protocol, () => true, _ => { }, new ClipboardFixture(), (_, token) => timer.Task.WaitAsync(token));
        var row = (Grid)window.FindName("NodeExportRow");
        var button = (Button)window.FindName("CopyNodeButton");
        var mark = (System.Windows.Shapes.Path)window.FindName("NodeCopySuccessMark");
        row.DataContext = new { NodeExport = vm };
        var picker = (ComboBox)window.FindName("ExportFormatPicker");
        var copy = vm.CopyAsync(); snapshot();
        check(mark.Visibility == Visibility.Visible && ((System.Windows.Media.SolidColorBrush)mark.Stroke).Color == System.Windows.Media.Color.FromRgb(0x27, 0x94, 0x53), "copy button displays green vector check inside its original bounds");
        var width = button.ActualWidth; var height = button.ActualHeight;
        timer.SetResult(); PumpUntil(() => copy.IsCompleted); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        check(mark.Visibility == Visibility.Collapsed && button.ActualWidth == width && button.ActualHeight == height, "copy feedback recovery causes no layout jump");
        check(picker.Items.Count == 3 && picker.IsEnabled && button.IsEnabled, "dynamic format selector renders all supported client formats");
        check(picker.SelectionBoxItem.ToString() == "Clash（Mihomo）", "dynamic selector displays a clean client label rather than a record name");
        protocol = "unsupported"; vm.RefreshFormats();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        check(picker.Items.Count == 0 && !picker.IsEnabled && !button.IsEnabled, "actual selector and copy button both disable when protocol has no exporter");
        row.ClearValue(FrameworkElement.DataContextProperty);
    }

    private static TaskCompletionSource NewTimer() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Node export check did not settle.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    private sealed class ExportFixture : INodeExportService
    {
        public readonly string Result = "synthetic-private-node-config";
        public TaskCompletionSource<string>? Pending;
        public Exception? Failure;
        public int Calls;
        public Task<string> ExportAsync(string settingsJson, string format) { Calls++; return Failure != null ? Task.FromException<string>(Failure) : Pending?.Task ?? Task.FromResult(Result); }
    }
    private sealed class ClipboardFixture : ITextClipboard
    {
        public string Text = "untouched";
        public int Writes;
        public bool Fail;
        public void SetText(string text) { if (Fail) throw new IOException("剪贴板被占用"); Text = text; Writes++; }
    }
}
