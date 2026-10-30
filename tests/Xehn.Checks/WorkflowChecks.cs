using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Xehn.App;

internal static class WorkflowChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var fake = new FakeEngine(Path.Combine(output, "workflow", Guid.NewGuid().ToString("N")));
            using var vm = new MainViewModel(fake);
            check(!vm.VerifyBeforeConnect && vm.ScanIntensity == "medium", "fast-connect / medium-scan defaults");
            vm.InitializePreparation();
            check(fake.Calls.Count == 1 && fake.Last.Command == "desktop-prepare", "startup prepares without connecting");
            var old = fake.Last;
            old.Emit("progress", phase: "register", done: 1, total: 2);
            check(vm.RegistrationProgress == 50, "registration progress reflects engine units");
            check(vm.IsStatusWorking && !vm.IsPowerWorking && vm.SidebarStatus == "正在检查", "background preparation uses only the small sidebar progress ring");
            var connect = vm.ToggleAsync();
            check(fake.Calls.Count == 1 && vm.CanChangeProtocol, "connect waits for existing preparation without duplicate scan");
            check(vm.IsPowerWorking && vm.IsStatusWorking, "connection request starts both progress rings while preparing");
            vm.Protocol = "wg";
            PumpUntil(() => fake.Calls.Count == 2);
            check(fake.CancelCount == 1 && fake.Last.Protocol == "wg", "protocol switch cancels and starts new snapshot");
            old.Emit("progress", phase: "scan", done: 99, total: 100);
            check(vm.ScanProgress != 99, "late progress from previous protocol is ignored");
            fake.Last.Emit("progress", phase: "scan", done: 2, total: 8);
            check(vm.ScanProgress == 25, "scan progress reflects actual candidates");
            fake.Last.Emit("progress", phase: "scan", done: 0, total: 100, text: "补充探测备用端口");
            check(vm.ScanProgress == 25 && vm.ScanText.Contains("备用端口"), "phase change preserves total progress and detailed step text");
            fake.Last.Emit("progress", phase: "scan", done: 90, total: 100, text: "验证隧道 · 完成 4/14 · 可用 2/2 · 跳过 10");
            fake.Last.Emit("progress", phase: "scan", done: 30, total: 100, text: "保存节点缓存 · 跳过 10");
            check(vm.ScanProgress == 90 && vm.ScanText.Contains("跳过 10"), "late lower progress does not roll back the progress bar");
            fake.Complete();
            PumpUntil(() => fake.Last.Command == "desktop-connect");
            check(fake.Calls.Count == 3 && fake.Last.Protocol == "wg" && fake.Last.Prepared, "pending connect follows latest prepared protocol");
            check(vm.ScanProgress == 100 && vm.ScanText.Contains("跳过 10"), "preparation retains its final outcome detail");
            fake.Last.Emit("connection", state: "provisional");
            check(vm.IsConnected && vm.Status.Contains("检测中"), "fast mode exposes provisional connection");
            check(vm.ConnectionHeading == "已连接" && vm.IsPowerWorking, "provisional connection heading stays connected while the ring indicates verification");
            fake.Last.Emit("connection", state: "disconnected");
            check(!vm.IsConnected, "failed background verification immediately retracts connection");
            fake.Fail(); PumpUntil(() => connect.IsCompleted);
            check(!vm.IsConnected && !vm.IsBusy, "failed connect leaves workflow idle");
            check(!vm.IsPowerWorking && !vm.IsStatusWorking, "failed connection clears both busy indicators");

            vm.VerifyBeforeConnect = true; vm.ScanIntensity = "high";
            PumpUntil(() => fake.Last.Command == "desktop-prepare");
            fake.Complete(); PumpUntil(() => !vm.IsPreparing);
            var count = fake.Calls.Count;
            fake.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldPoll = vm.RefreshAsync();
            var strict = vm.ToggleAsync();
            PumpUntil(() => fake.Last.Command == "desktop-connect");
            check(fake.Calls.Count == count + 1 && fake.Last.VerifyBeforeConnect && !vm.IsConnected, "warm strict mode reuses prep and waits for verification");
            fake.Connected = true; fake.Last.Emit("connection", state: "verified"); fake.Complete();
            fake.PendingRead.SetResult(new Session(false)); fake.PendingRead = null;
            PumpUntil(() => oldPoll.IsCompleted);
            check(vm.IsConnected, "stale polling cannot overwrite a new connection");
            PumpUntil(() => strict.IsCompleted);
            check(vm.IsConnected, "verified session shown connected");
            var disconnect = vm.ToggleAsync(); fake.Connected = false; fake.Complete(); PumpUntil(() => disconnect.IsCompleted);
            check(!vm.IsConnected, "disconnect clears connected session");

            vm.Protocol = "masque";
            PumpUntil(() => fake.Last.Command == "desktop-prepare");
            var waiting = vm.ToggleAsync();
            var cancelled = vm.ToggleAsync();
            PumpUntil(() => waiting.IsCompleted && cancelled.IsCompleted && !vm.IsPreparing);
            check(!vm.IsBusy && !vm.IsConnected && fake.Last.Command == "desktop-prepare", "cancel during preparation never starts a core");

            vm.Protocol = "awg"; vm.Protocol = "wg"; vm.Protocol = "masque-mm";
            PumpUntil(() => fake.Last.Protocol == "masque-mm");
            check(fake.MaxConcurrent == 1, "rapid protocol switches serialize engine ownership");
            var stop = vm.StopPreparationAsync(); PumpUntil(() => stop.IsCompleted);
            check(!vm.IsPreparing, "closing cancels background preparation cleanly");
            DesktopChecks(check, output);
            VersionOneChecks(check, output);
            TrafficChecks(check, output);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void TrafficChecks(Action<bool, string> check, string output)
    {
        var time = new ManualTimeProvider();
        var fake = new FakeEngine(Path.Combine(output, "traffic", Guid.NewGuid().ToString("N"))) { Connected = true, Up = 8192, Down = 4096 };
        using var vm = new MainViewModel(fake, new FakeDesktop(), timeProvider: time);
        vm.RefreshAsync().GetAwaiter().GetResult();
        time.Advance(2); fake.Up = fake.Down = 0; vm.RefreshAsync().GetAwaiter().GetResult();
        check(vm.Upload == "0 KB/s" && vm.Download == "0 KB/s" && vm.UploadSeries.HasVisibleTraffic && vm.DownloadSeries.HasVisibleTraffic, "connected zero upload / download display their values without clearing either traffic history");
        time.Advance(2); fake.FailTraffic = true; vm.RefreshAsync().GetAwaiter().GetResult();
        check(vm.UploadSeries.Snapshot().Length == 2 && vm.UploadSeries.HasVisibleTraffic, "transient traffic API errors preserve history without fabricating a zero sample");
        fake.Connected = false; fake.FailTraffic = false; time.Advance(2); vm.RefreshAsync().GetAwaiter().GetResult();
        check(vm.Upload == "" && vm.Download == "" && vm.UploadSeries.HasVisibleTraffic && vm.DownloadSeries.HasVisibleTraffic, "disconnect blanks instantaneous rates but retains both curves");
        time.Advance(30); fake.Connected = true; fake.Up = fake.Down = 2048; vm.RefreshAsync().GetAwaiter().GetResult();
        check(vm.UploadSeries.Snapshot().Any(p => p.Value == 8192) && vm.UploadSeries.Snapshot().Last().Value == 2048, "reconnect appends to the same still-visible time window");
        time.Advance(100); fake.Connected = false; vm.RefreshAsync().GetAwaiter().GetResult();
        check(!vm.UploadSeries.HasVisibleTraffic && !vm.DownloadSeries.HasVisibleTraffic, "disconnected history disappears only on timed expiry");
    }

    private static void VersionOneChecks(Action<bool, string> check, string output)
    {
        FakeEngine NewEngine() => new(Path.Combine(output, "version-one", Guid.NewGuid().ToString("N")));
        check(DesktopIntegration.NeedsStartupElevation("tun", false) && !DesktopIntegration.NeedsStartupElevation("proxy", false) && !DesktopIntegration.NeedsStartupElevation("tun", true), "saved TUN startup requests elevation only when required");
        var ownerThread = Environment.CurrentManagedThreadId; var dispatched = false;
        var shell = DesktopIntegration.RunOnStaAsync(() => { Thread.Sleep(100); return (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()); });
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatched = true));
        PumpUntil(() => shell.IsCompleted && dispatched);
        check(shell.Result.Item1 != ownerThread && shell.Result.Item2 == ApartmentState.STA && dispatched, "slow shell / elevation launch runs on separate STA without freezing WPF dispatcher");
        var denied = DesktopIntegration.RunOnStaAsync<object>(() => throw new System.ComponentModel.Win32Exception(1223));
        PumpUntil(() => denied.IsCompleted);
        check(denied.Exception?.InnerException is System.ComponentModel.Win32Exception error && error.NativeErrorCode == 1223, "STA launcher preserves UAC cancellation error for rollback");
        var engine = NewEngine(); new ClientSettings { CaptureMode = "tun" }.Save(engine.Home);
        check(ClientSettings.Load(engine.Home).CaptureMode == "tun", "TUN preference survives exit and next startup");

        var modes = NewEngine(); modes.Connected = true;
        using var vm = new MainViewModel(modes, new FakeDesktop()); vm.RefreshAsync().GetAwaiter().GetResult();
        vm.VerifyBeforeConnect = true; vm.SplitSelected = true;
        check(!vm.IsConnected && vm.Status.Contains("验证"), "strict hot switch withdraws connected UI until validation");
        modes.Last.Emit("connection", state: "verifying");
        check(!vm.IsConnected && vm.Status.Contains("验证"), "strict hot switch remains verifying before data-plane success");
        modes.Last.Emit("connection", state: "verified"); modes.Complete(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.IsConnected && vm.SplitSelected, "strict hot switch displays connected after verification");
        vm.VerifyBeforeConnect = false; vm.GlobalSelected = true;
        modes.Last.Emit("connection", state: "provisional");
        check(vm.IsConnected && vm.Status.Contains("检测中"), "fast hot switch explicitly displays connected-but-checking");
        modes.Fail(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.SplitSelected && vm.Notice.Contains("失败"), "failed hot verification rolls mode back and surfaces failure");
        modes.Verification = "failed"; vm.RefreshAsync().GetAwaiter().GetResult();
        check(vm.Status.Contains("异常") && !vm.Status.Contains("已连接") && !vm.CanChangeModes && vm.CanToggleFromTray, "unverified rollback cannot masquerade as connected and still permits disconnect cleanup");

        var network = NewEngine(); var resources = new FakeResources();
        using var model = new MainViewModel(network, new FakeDesktop(), resources);
        model.InitializeResourcesAsync().GetAwaiter().GetResult();
        check(model.AppVersion == "1.0.0" && model.GeoNeedsDownload && model.GeoUpdated == "2026-10-20", "resource initialization reports version and old GeoFiles date");
        check(model.GeoResourceMessage.Contains("检查完成") && model.GeoResourceMessage.Contains("下载更新"), "missing / stale GeoFiles check explains the required update in its card");
        network.Connected = true; network.Verification = "checking"; model.RefreshAsync().GetAwaiter().GetResult();
        check(resources.Calls.Count == 1, "automatic GeoFiles never download before verified VPN");
        network.Verification = "verified"; model.RefreshAsync().GetAwaiter().GetResult();
        check(resources.Calls.Last() == "resources-download-geo-proxy" && model.CanToggleFromTray, "automatic stale GeoFiles use explicit VPN proxy without blocking disconnect");
        resources.EmitProgress(new("geo", "download", "geosite.dat", 256, 1024));
        check(model.GeoDownloadActive && !model.GeoDownloadIndeterminate && model.GeoDownloadProgress == 25, "GeoFiles progress uses real byte ratio in its own card");
        resources.EmitProgress(new("geo", "download", "geosite.dat", 1024, 1024));
        check(!model.GeoDownloadActive && !model.ResourceOperation.IsCompleted, "GeoFiles bar hides on file completion without waiting for the resource operation");
        resources.EmitProgress(new("geo", "download", "geoip.metadb", 512, 0));
        check(model.GeoDownloadActive && model.GeoDownloadIndeterminate && model.GeoDownloadProgress == 0, "next GeoFiles file restarts indeterminate progress when content length is unknown");
        resources.EmitProgress(new("geo", "downloaded", "geoip.metadb", 0, 0));
        check(!model.GeoDownloadActive && !model.GeoDownloadIndeterminate && !model.ResourceOperation.IsCompleted, "unknown-length file EOF also hides its bar while validation remains pending");
        resources.EmitProgress(new("geo", "validate", "", 0, 0));
        check(!model.GeoDownloadActive && model.GeoResourceMessage.Contains("校验") && model.GeoResourceSuccessText == "", "GeoFiles validation has text only, not a lingering bar or false success");
        resources.CompleteDownload(); PumpUntil(() => model.ResourceOperation.IsCompleted);
        check(model.GeoPending && model.GeoUpdated == "2026-10-20" && !resources.Calls.Contains("resources-apply"), "connected GeoFiles download is staged without changing live update date");
        check(model.GeoResourceMessage.Contains("等待") && !model.GeoResourceMessage.Contains("成功") && !model.GeoDownloadActive, "staged download is not misreported as a successful replacement");
        var disconnect = model.ToggleAsync(); network.Connected = false; network.Complete(); PumpUntil(() => disconnect.IsCompleted);
        check(!model.GeoPending && model.GeoUpdated == "2026-10-30" && resources.Calls.Contains("resources-apply"), "disconnect applies staged pair and refreshes date");
        check(model.GeoResourceMessage.Contains("替换成功"), "GeoFiles card confirms actual replacement after disconnect");
        model.RunResourceActionAsync("resources-check-core").GetAwaiter().GetResult();
        check(model.CoreNeedsDownload && model.CoreCheckText.Contains("不支持"), "unsupported protocol check exposes core download action");
        check(model.CoreResourceSuccessText == "" && model.CoreResourceDetail.Contains("不支持"), "unsupported-core result cannot retain a stale inline success label");
        model.RunResourceActionAsync("resources-info").GetAwaiter().GetResult();
        check(model.CoreNeedsDownload, "GeoFiles inspection cannot erase an unsupported-core verdict");
        check(model.GeoResourceMessage.Contains("检查通过") && model.GeoResourceMessage.Contains("无需更新"), "successful GeoFiles check gives an explicit card-level confirmation");
        resources.FailInfo = true; model.RunResourceActionAsync("resources-info").GetAwaiter().GetResult();
        check(model.GeoResourceMessage.Contains("检查未完成") && !model.GeoResourceMessage.Contains("检查通过"), "failed GeoFiles check replaces any stale successful-check message");
        resources.FailInfo = false;
        network.Connected = true; model.RefreshAsync().GetAwaiter().GetResult(); var count = resources.Calls.Count;
        model.RunResourceActionAsync("resources-download-core").GetAwaiter().GetResult();
        check(resources.Calls.Count == count, "core download cannot overwrite a running connection");
        network.Connected = false; model.RefreshAsync().GetAwaiter().GetResult();
        resources.HoldCoreDownload = true;
        var coreDownload = model.RunResourceActionAsync("resources-download-core");
        resources.EmitProgress(new("core", "download", "mihomo.zip", 750, 1000));
        check(model.CoreDownloadActive && !model.CoreDownloadIndeterminate && model.CoreDownloadProgress == 75 && !model.GeoDownloadActive, "core download progress is separate from GeoFiles progress");
        resources.EmitProgress(new("core", "download", "mihomo.zip", 1000, 1000));
        check(!model.CoreDownloadActive && !coreDownload.IsCompleted, "core bar hides as soon as known-length file transfer completes");
        resources.EmitProgress(new("core", "validate", "", 0, 0));
        check(!model.CoreDownloadActive && !model.CanToggleFromTray && model.CoreResourceMessage.Contains("校验"), "core validation keeps the install lock but no download bar");
        resources.CompleteCoreDownload(); PumpUntil(() => coreDownload.IsCompleted);
        check(model.CoreVersion == "9.0.0" && !model.CoreNeedsDownload, "validated core install refreshes displayed version");
        check(model.CoreResourceMessage.Contains("成功") && !model.CoreDownloadActive, "core card confirms successful install / replacement");
        resources.CoreSupported = true; model.RunResourceActionAsync("resources-check-core").GetAwaiter().GetResult();
        check(model.CoreResourceSuccessText == "检查通过" && model.CoreResourceDetail == "", "successful core inspection also appears only inline with its buttons");

        var retryEngine = NewEngine(); var retryResources = new FakeResources { FailDownload = true };
        using var retry = new MainViewModel(retryEngine, new FakeDesktop(), retryResources);
        retry.InitializeResourcesAsync().GetAwaiter().GetResult(); retryEngine.Connected = true;
        retry.RefreshAsync().GetAwaiter().GetResult(); retry.RefreshAsync().GetAwaiter().GetResult(); retry.RefreshAsync().GetAwaiter().GetResult();
        check(!retry.GeoDownloadActive && !retry.GeoDownloadIndeterminate, "failed resource download never leaves a progress bar active");
        check(retryResources.Calls.Count(c => c == "resources-download-geo-proxy") == 1, "failed automatic update does not loop on each poll");
        retryEngine.SessionId++; retry.RefreshAsync().GetAwaiter().GetResult();
        check(retryResources.Calls.Count(c => c == "resources-download-geo-proxy") == 2, "new VPN session can retry failed automatic update");

        var ageEngine = NewEngine(); var ageResources = new FakeResources(); ageResources.SetGeoAge(false);
        using var age = new MainViewModel(ageEngine, new FakeDesktop(), ageResources);
        age.InitializeResourcesAsync().GetAwaiter().GetResult(); ageEngine.Connected = true; age.RefreshAsync().GetAwaiter().GetResult();
        check(!ageResources.Calls.Contains("resources-download-geo-proxy"), "current GeoFiles are not downloaded unnecessarily");
        ageResources.SetGeoAge(true); ageEngine.SessionId++; age.RefreshAsync().GetAwaiter().GetResult();
        check(ageResources.Calls.Last() == "resources-download-geo-proxy", "new verified session rechecks GeoFiles age instead of keeping startup age forever");
        ageResources.CompleteDownload(); PumpUntil(() => age.ResourceOperation.IsCompleted);

        var applyEngine = NewEngine(); var applyResources = new FakeResources { FailApply = true };
        using var apply = new MainViewModel(applyEngine, new FakeDesktop(), applyResources);
        apply.InitializeResourcesAsync().GetAwaiter().GetResult();
        var applyDownload = apply.RunResourceActionAsync("resources-download-geo");
        check(!apply.CanToggleFromTray, "offline GeoFiles download / replacement prevents a competing connection");
        applyResources.CompleteDownload(); PumpUntil(() => applyDownload.IsCompleted);
        check(apply.GeoPending && apply.GeoResourceMessage.Contains("已下载") && apply.GeoResourceMessage.Contains("替换未完成") && !apply.GeoResourceMessage.Contains("成功"), "replacement failure distinguishes completed staging from failed installation");

        var cancelEngine = NewEngine(); var cancelResources = new FakeResources();
        using var cancel = new MainViewModel(cancelEngine, new FakeDesktop(), cancelResources);
        cancel.InitializeResourcesAsync().GetAwaiter().GetResult(); cancelEngine.Connected = true; cancel.RefreshAsync().GetAwaiter().GetResult();
        var closing = cancel.ShutdownAsync(); PumpUntil(() => cancelEngine.Calls.Count > 0);
        check(cancelResources.CancelCount == 1 && cancelEngine.Last.Command == "desktop-disconnect", "exit cancels automatic proxy download before disconnect cleanup");
        cancelEngine.Connected = false; cancelEngine.Complete(); PumpUntil(() => closing.IsCompleted);
        check(closing.Result, "exit with in-flight automatic update settles without deadlock");
    }

    private sealed class FakeResources : IResourceService
    {
        public List<string> Calls { get; } = [];
        public int CancelCount;
        public bool FailDownload;
        public bool HoldCoreDownload;
        public bool FailApply;
        public bool FailInfo;
        public bool CoreSupported;
        private ResourceInfo current = new("1.19.32", false, "", "2026-10-20", true, false);
        private TaskCompletionSource<ResourceInfo>? download;
        private TaskCompletionSource<ResourceInfo>? coreDownload;
        private Action<ResourceProgress>? progress;
        public Task<ResourceInfo> RunAsync(string command, Action<string> log, Action<ResourceProgress>? onProgress = null)
        {
            progress = onProgress;
            Calls.Add(command);
            if (command == "resources-info" && FailInfo) return Task.FromException<ResourceInfo>(new IOException("fixture inspection failure"));
            if (command == "resources-check-core") return Task.FromResult(current with { CoreNeedsDownload = !CoreSupported, CoreCheck = CoreSupported ? "全部协议检查通过" : "不支持 fixture 协议" });
            if (command == "resources-download-core") { if (HoldCoreDownload) { coreDownload = new(TaskCreationOptions.RunContinuationsAsynchronously); return coreDownload.Task; } current = current with { CoreVersion = "9.0.0", CoreNeedsDownload = false }; return Task.FromResult(current); }
            if (command == "resources-apply") { if (FailApply) return Task.FromException<ResourceInfo>(new IOException("fixture replacement failure")); current = current with { GeoPending = false, GeoUpdated = "2026-10-30", GeoNeedsDownload = false }; onProgress?.Invoke(new("geo", "installed", "", 1, 1)); return Task.FromResult(current); }
            if (command.Contains("download-geo"))
            {
                if (FailDownload) return Task.FromException<ResourceInfo>(new IOException("fixture download failure"));
                download = new(TaskCreationOptions.RunContinuationsAsynchronously); return download.Task;
            }
            return Task.FromResult(current);
        }
        public void CompleteDownload() { current = current with { GeoPending = true }; download!.SetResult(current); }
        public void SetGeoAge(bool due) { current = current with { GeoNeedsDownload = due }; }
        public void CompleteCoreDownload() { current = current with { CoreVersion = "9.0.0", CoreNeedsDownload = false }; coreDownload!.SetResult(current); }
        public void EmitProgress(ResourceProgress value) => progress?.Invoke(value);
        public Task CancelAsync() { CancelCount++; download?.TrySetCanceled(); coreDownload?.TrySetCanceled(); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private static void DesktopChecks(Action<bool, string> check, string output)
    {
        FakeEngine NewEngine() => new(Path.Combine(output, "desktop", Guid.NewGuid().ToString("N")));
        var fake = NewEngine(); var desktop = new FakeDesktop();
        using var vm = new MainViewModel(fake, desktop);
        check(vm.CloseToTray && !vm.AutoStart, "tray-on-close defaults on; autostart defaults off");
        check(MainWindow.ShouldHideOnClose(true, true, false) && !MainWindow.ShouldHideOnClose(false, true, false) &&
            !MainWindow.ShouldHideOnClose(true, false, false) && !MainWindow.ShouldHideOnClose(true, true, true), "close policy never hides on explicit exit or missing tray");
        var shown = 0; var exits = 0;
        using var tray = new TrayService(vm, () => shown++, () => { exits++; return Task.CompletedTask; }, visible: false);
        check(tray.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>().Select(i => i.Header as string).SequenceEqual(new[] { "显示主界面", "连接 VPN", "退出" }), "real tray menu has exactly the requested three actions");
        ((System.Windows.Controls.MenuItem)tray.ContextMenu.Items[0]).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); ((System.Windows.Controls.MenuItem)tray.ContextMenu.Items[2]).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        check(shown == 1 && exits == 1, "tray show / exit actions dispatch to lifecycle handlers");
        vm.AutoStart = true;
        check(desktop.AutoStartEnabled && ClientSettings.Load(fake.Home).AutoStart, "autostart opt-in persists through isolated integration");
        vm.AutoStart = false;
        check(!desktop.AutoStartEnabled && !ClientSettings.Load(fake.Home).AutoStart, "autostart opt-out persists");
        desktop.FailAutoStart = true; vm.AutoStart = true;
        check(!vm.AutoStart && vm.Notice.Contains("自启动设置失败"), "autostart failure retains prior preference");
        check(DesktopIntegration.AutoStartCommand(@"D:\app path\Xehn.App.exe") == "\"D:\\app path\\Xehn.App.exe\"", "autostart quotes executable paths containing spaces");
        fake.Connected = true; vm.RefreshAsync().GetAwaiter().GetResult();
        check(vm.CanChangeModes && !vm.CanConfigure && ((System.Windows.Controls.MenuItem)tray.ContextMenu.Items[1]).Header as string == "关闭 VPN", "connected mode selectors enabled; tray reflects live session");
        vm.SplitSelected = true;
        check(fake.Last.Command == "desktop-modes" && fake.Last.Route == "bypass" && !vm.CanChangeModes && !((System.Windows.Controls.MenuItem)tray.ContextMenu.Items[1]).IsEnabled, "routing hot switch uses one serialized engine reload and disables tray toggle");
        vm.TunSelected = true;
        check(fake.Calls.Count == 1, "overlapping mode request cannot start a second operation");
        fake.Complete(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.SplitSelected && vm.IsConnected && ClientSettings.Load(fake.Home).ProxyMode == "bypass", "successful hot switch commits selection without disconnecting");
        vm.GlobalSelected = true; fake.Fail(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.SplitSelected && vm.IsConnected && ClientSettings.Load(fake.Home).ProxyMode == "bypass" && vm.Notice.Contains("失败"), "failed reload restores radio selection and leaves old preference");
        vm.TunSelected = true;
        check(fake.Last.Capture == "tun", "elevated proxy-to-TUN hot switch uses current connection");
        fake.Complete(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        vm.ProxySelected = true; fake.Complete(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.ProxySelected && vm.IsConnected && desktop.RestartCount == 0, "TUN-to-proxy hot switch needs no restart");
        desktop.IsElevated = false; desktop.AcceptRestart = false;
        var count = fake.Calls.Count;
        vm.TunSelected = true; PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.ProxySelected && vm.IsConnected && fake.Calls.Count == count && desktop.RestartReconnect && desktop.PrepareCount == 0, "UAC cancellation preserves connected proxy and settings");
        desktop.AcceptRestart = true;
        vm.TunSelected = true;
        check(fake.Last.Command == "desktop-disconnect" && vm.ProxySelected, "accepted elevation cleans old connection before committing TUN");
        fake.Connected = false; fake.Complete(); PumpUntil(() => vm.ModeChangeTask.IsCompleted);
        check(vm.TunSelected && !vm.IsConnected && ClientSettings.Load(fake.Home).CaptureMode == "tun" && desktop.RestartReconnect, "elevation handoff persists target and reconnect intent after cleanup");
        check(fake.MaxConcurrent == 1, "mode / elevation operations keep single engine ownership");

        var idle = NewEngine(); var idleDesktop = new FakeDesktop { IsElevated = false };
        using var idleVm = new MainViewModel(idle, idleDesktop);
        idleVm.TunSelected = true; PumpUntil(() => idleVm.ModeChangeTask.IsCompleted);
        check(idleVm.TunSelected && !idleDesktop.RestartReconnect && idle.Calls.Count == 0, "disconnected TUN elevation preserves disconnected intent");

        var running = NewEngine(); running.Connected = true;
        using var runningVm = new MainViewModel(running, new FakeDesktop());
        runningVm.RefreshAsync().GetAwaiter().GetResult();
        var shutdown = runningVm.ShutdownAsync();
        check(running.Last.Command == "desktop-disconnect" && !runningVm.CanToggleFromTray, "explicit exit disconnects active VPN instead of hiding");
        check(!shutdown.IsCompleted, "application exit cannot finish before engine disconnect completes");
        running.Connected = false; running.Complete(); PumpUntil(() => shutdown.IsCompleted);
        check(shutdown.Result && !runningVm.IsConnected, "exit waits for disconnect and state cleanup");

        var lingering = NewEngine(); lingering.Connected = true;
        using var lingeringVm = new MainViewModel(lingering, new FakeDesktop());
        var lingeringExit = lingeringVm.ShutdownAsync(); lingering.Complete(); PumpUntil(() => lingeringExit.IsCompleted);
        check(!lingeringExit.Result && lingeringVm.Notice.Contains("仍处于连接状态"), "exit refuses to close when disconnect returns without clearing the actual session");
        var lingeringRetry = lingeringVm.ShutdownAsync(); lingering.Connected = false; lingering.Complete(); PumpUntil(() => lingeringRetry.IsCompleted);
        check(lingeringRetry.Result, "exit may finish only after the lingering session is truly cleared");

        var pending = NewEngine(); using var pendingVm = new MainViewModel(pending, new FakeDesktop());
        pendingVm.InitializePreparation(); var connection = pendingVm.ToggleAsync();
        check(!pendingVm.CanToggleFromTray && pendingVm.TrayConnectionLabel.Contains("正在连接"), "tray connection action is greyed while connecting / queued");
        var cancelExit = pendingVm.ShutdownAsync(); PumpUntil(() => cancelExit.IsCompleted && connection.IsCompleted);
        check(cancelExit.Result && pending.Calls.All(c => c.Command != "desktop-connect"), "exit cancels queued preparation without starting VPN");

        var failing = NewEngine(); failing.Connected = true;
        new ClientSettings().Save(failing.Home);
        using var failingVm = new MainViewModel(failing, new FakeDesktop()); failingVm.RefreshAsync().GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.Combine(failing.Home, "settings.json.tmp"));
        failingVm.SplitSelected = true; failing.Complete();
        PumpUntil(() => failing.Calls.Count == 2);
        check(failing.Last.Route == "global", "settings write failure rolls running engine back to previous routing");
        failing.Complete(); PumpUntil(() => failingVm.ModeChangeTask.IsCompleted);
        check(failingVm.GlobalSelected && ClientSettings.Load(failing.Home).ProxyMode == "global", "write-failure rollback keeps UI and persisted mode consistent");

        var cleanup = NewEngine(); cleanup.Connected = true;
        using var cleanupVm = new MainViewModel(cleanup, new FakeDesktop()); cleanupVm.RefreshAsync().GetAwaiter().GetResult();
        var failedExit = cleanupVm.ShutdownAsync(); cleanup.Fail(); PumpUntil(() => failedExit.IsCompleted);
        check(!failedExit.Result && cleanupVm.Notice.Contains("退出清理失败"), "cleanup failure keeps application open for retry");
        var retriedExit = cleanupVm.ShutdownAsync();
        check(cleanup.Last.Command == "desktop-disconnect", "failed exit retries cleanup instead of starting a new connection");
        cleanup.Connected = false; cleanup.Complete(); PumpUntil(() => retriedExit.IsCompleted);
        check(retriedExit.Result, "successful cleanup retry permits exit");

        var switching = NewEngine(); switching.Connected = true;
        using var switchingVm = new MainViewModel(switching, new FakeDesktop()); switchingVm.RefreshAsync().GetAwaiter().GetResult();
        switchingVm.SplitSelected = true;
        var switchExit = switchingVm.ShutdownAsync(); PumpUntil(() => switching.Last.Command == "desktop-disconnect");
        switching.Connected = false; switching.Complete(); PumpUntil(() => switchExit.IsCompleted);
        check(switchExit.Result && switchingVm.GlobalSelected && switching.MaxConcurrent == 1, "exit during hot reload waits for cancellation / rollback before disconnecting");

        var unpolled = NewEngine(); unpolled.Connected = true;
        using var unpolledVm = new MainViewModel(unpolled, new FakeDesktop());
        var unpolledExit = unpolledVm.ShutdownAsync();
        check(unpolled.Last.Command == "desktop-disconnect", "exit re-reads disk session even before first UI poll");
        unpolled.Connected = false; unpolled.Complete(); PumpUntil(() => unpolledExit.IsCompleted);
        check(unpolledExit.Result, "unpolled session is cleaned before exit");

        var geo = NewEngine(); using var geoVm = new MainViewModel(geo, new FakeDesktop());
        var download = geoVm.GeoAsync(true); var geoExit = geoVm.ShutdownAsync();
        PumpUntil(() => geoExit.IsCompleted && download.IsCompleted);
        check(geoExit.Result && geo.CancelCount == 1, "exit cancels and awaits an in-flight database download");

        var inFlight = NewEngine(); using var flightVm = new MainViewModel(inFlight, new FakeDesktop());
        flightVm.InitializePreparation(); inFlight.Complete(); PumpUntil(() => !flightVm.IsPreparing);
        var flight = flightVm.ToggleAsync(); PumpUntil(() => inFlight.Last.Command == "desktop-connect");
        var flightExit = flightVm.ShutdownAsync(); PumpUntil(() => flightExit.IsCompleted && flight.IsCompleted);
        check(flightExit.Result && inFlight.CancelCount == 1 && !flightVm.IsConnected, "exit cancels and awaits an engine connection in progress");
    }

    private sealed class FakeDesktop : IDesktopIntegration
    {
        public bool IsElevated { get; set; } = true;
        public bool AutoStartEnabled { get; private set; }
        public bool AcceptRestart = true, RestartReconnect, FailAutoStart;
        public int RestartCount, PrepareCount;
        public void SetAutoStart(bool enabled) { if (FailAutoStart) throw new IOException("fixture startup failure"); AutoStartEnabled = enabled; }
        public async Task<bool> RestartElevatedAsync(bool reconnect, Func<Task<bool>> prepare)
        { RestartCount++; RestartReconnect = reconnect; if (!AcceptRestart) return false; PrepareCount++; return await prepare(); }
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Workflow check did not settle.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }

    private sealed class FakeEngine(string home) : IEngineService
    {
        public string Home => home;
        public List<Call> Calls { get; } = [];
        public Call Last => Calls[^1];
        public int CancelCount, MaxConcurrent;
        private int concurrent;
        public bool Connected;
        public string Verification = "verified";
        public int SessionId;
        public double Up, Down;
        public bool FailTraffic;
        public TaskCompletionSource<Session>? PendingRead;
        public async Task RunAsync(string command, Action<string> log, string? settingsJson = null, bool prepared = false)
        {
            var call = new Call(command, log, settingsJson ?? "{}", prepared); Calls.Add(call);
            MaxConcurrent = Math.Max(MaxConcurrent, ++concurrent);
            try { await call.Finished.Task; } finally { concurrent--; }
        }
        public Task CancelAsync()
        {
            if (Calls.Count > 0 && !Last.Finished.Task.IsCompleted) { CancelCount++; Last.Finished.TrySetCanceled(); }
            return Task.CompletedTask;
        }
        public void Complete() => Last.Finished.TrySetResult();
        public void Fail() => Last.Finished.TrySetException(new IOException("simulated verification failure"));
        public Task<Session> ReadSessionAsync() => PendingRead?.Task ?? Task.FromResult(new Session(Connected, Started: "fixture-" + SessionId, Verification: Verification));
        public Task<(double Up, double Down)> TrafficAsync(int port) => FailTraffic ? Task.FromException<(double, double)>(new IOException("fixture traffic unavailable")) : Task.FromResult((Up, Down));
        public void Dispose() { }
    }
    private sealed class Call(string command, Action<string> log, string settings, bool prepared)
    {
        public string Command => command;
        public bool Prepared => prepared;
        public string Protocol => JsonSerializer.Deserialize<ClientSettings>(settings, ClientSettings.JsonOptions)!.Proto;
        public string Capture => JsonSerializer.Deserialize<ClientSettings>(settings, ClientSettings.JsonOptions)!.CaptureMode;
        public string Route => JsonSerializer.Deserialize<ClientSettings>(settings, ClientSettings.JsonOptions)!.ProxyMode;
        public bool VerifyBeforeConnect => JsonSerializer.Deserialize<ClientSettings>(settings, ClientSettings.JsonOptions)!.VerifyBeforeConnect;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(string type, string phase = "", string state = "", int done = 0, int total = 0, string text = "test") => log("@@XEHN " + JsonSerializer.Serialize(new { type, phase, state, done, total, text }));
    }
}
