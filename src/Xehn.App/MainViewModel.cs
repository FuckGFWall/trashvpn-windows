using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Text.Json;

namespace Xehn.App;
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public NodeExportViewModel NodeExport { get; }
    private readonly ClientSettings settings;
    private readonly IEngineService engine;
    private readonly IDesktopIntegration desktop;
    private readonly IResourceService resources;
    private readonly Func<IReadOnlyList<string>> lanAddresses;
    public string LanProxyEndpoints { get; private set; } = "";
    private bool resourcesReady, resourcesBusy, resourcesInstalling, coreCapabilityChecked;
    private Task? resourceInitialization;
    private Task resourceOperation = Task.CompletedTask;
    private string autoGeoSession = "";
    private string inspectedGeoSession = "";
    private DateTimeOffset nextGeoInspection = DateTimeOffset.MinValue;
    public Task ResourceOperation => resourceOperation;
    public string AppVersion => $"v{AppReleaseUpdate.CurrentVersion}";
    public string AppDownloadUrl => AppReleaseUpdate.DownloadPage;
    public string AppUpdateActionLabel => HasAppUpdate ? "下载" : "检查";
    public bool AppUpdateChecking => updateCancellation != null;
    public bool CanCheckAppUpdate => !AppUpdateChecking;
    public bool AppUpdateCheckSucceeded { get; private set; }
    public bool HasAppUpdate => AppReleaseUpdate.ParseTag(updateHistory.LatestTag) is { } latest && latest > AppReleaseUpdate.CurrentVersion;
    public string AppUpdateDetail => updateMessage.Length > 0 ? updateMessage : HasAppUpdate ? $"发现新版本 {updateHistory.LatestTag}" : "";
    private readonly TimeProvider clock;
    private readonly Func<int, CancellationToken, Task<Version>> getReleaseVersion;
    private AppReleaseUpdate.History updateHistory;
    private CancellationTokenSource? updateCancellation;
    private int updateProxyPort;
    private string updateMessage = "";
    public string CoreVersion { get; private set; } = "尚未检查";
    public string CoreCheckText { get; private set; } = "";
    public string GeoUpdated { get; private set; } = "尚未更新";
    public bool CoreNeedsDownload { get; private set; }
    public bool GeoNeedsDownload { get; private set; }
    public bool GeoPending { get; private set; }
    public bool CoreDownloadActive { get; private set; }
    public bool GeoDownloadActive { get; private set; }
    public double CoreDownloadProgress { get; private set; }
    public double GeoDownloadProgress { get; private set; }
    public bool CoreDownloadIndeterminate { get; private set; }
    public bool GeoDownloadIndeterminate { get; private set; }
    public string CoreResourceMessage { get; private set; } = "";
    public string GeoResourceMessage { get; private set; } = "";
    public bool CoreResourceSucceeded { get; private set; }
    public bool GeoResourceSucceeded { get; private set; }
    public string CoreResourceSuccessText => CoreResourceMessage.Contains("成功", StringComparison.Ordinal) ? "安装 / 替换成功" : CoreResourceMessage.Contains("检查通过", StringComparison.Ordinal) || CoreResourceMessage.Length == 0 && CoreCheckText == "全部协议检查通过" ? "检查通过" : "";
    public string GeoResourceSuccessText => GeoResourceMessage.Contains("替换成功", StringComparison.Ordinal) ? "替换成功" : GeoResourceMessage.Contains("检查通过", StringComparison.Ordinal) ? "检查通过，无需更新" : "";
    public bool CoreChecking { get; private set; }
    public bool GeoChecking { get; private set; }
    public bool CoreDownloading { get; private set; }
    public bool GeoDownloading { get; private set; }
    public bool CoreResourceWorking => CoreChecking || CoreDownloading;
    public bool GeoResourceWorking => GeoChecking || GeoDownloading;
    private bool geoUserChecked;
    private bool CoreDownloadOffered => CoreNeedsDownload && coreCapabilityChecked;
    private bool GeoDownloadOffered => GeoNeedsDownload && !GeoPending && geoUserChecked;
    public string CoreResourceActionLabel => CoreDownloadOffered ? "下载" : "检查";
    public string GeoResourceActionLabel => GeoDownloadOffered ? "下载" : "检查";
    public string CoreResourceActionCommand => CoreDownloadOffered ? "resources-download-core" : "resources-check-core";
    public string GeoResourceActionCommand => GeoDownloadOffered ? "resources-download-geo" : "resources-info";
    public bool CanRunCoreResourceAction => CoreDownloadOffered ? CanDownloadCore : CanCheckResources;
    public bool CanRunGeoResourceAction => GeoDownloadOffered ? CanDownloadGeo : CanCheckResources;
    public string CoreResourceDetail => CoreChecking || CoreResourceSuccessText.Length > 0 ? "" : CoreResourceMessage.Length > 0 ? CoreResourceMessage : CoreCheckText;
    public string GeoResourceDetail => GeoChecking || GeoResourceSuccessText.Length > 0 ? "" : GeoResourceMessage;
    public bool CanCheckResources => !disposed && !resourcesBusy && !exiting;
    public bool CanDownloadCore => CanCheckResources && !IsBusy && !connected && !connecting;
    public bool CanDownloadGeo => CanCheckResources && !preparing && !engineConnecting && !switchingModes && !busy;
    private readonly SemaphoreSlim workflow = new(1, 1);
    private Task<bool>? preparation;
    private int generation;
    private int sessionRevision;
    private bool preparing, prepared, initialized, engineConnecting, disposed;
    private bool switchingModes, exiting;
    private bool cleanupFailed;
    private Task connectionOperation = Task.CompletedTask;
    private Task geoOperation = Task.CompletedTask;
    public Task ModeChangeTask { get; private set; } = Task.CompletedTask;
    public bool IsPreparing => preparing;
    public bool ConnectionRequested => connecting;
    public bool CanChangeProtocol => !preparationAction && !resourcesInstalling && !cleanupFailed && !switchingModes && !exiting && (!busy || preparing) && !connected && !engineConnecting && settingsReadable;
    public string RegistrationText { get; private set; } = "注册 · 待检查";
    public string ScanText { get; private set; } = "优选 · 待检查";
    public double RegistrationProgress { get; private set; }
    public double ScanProgress { get; private set; }
    public bool RegistrationIndeterminate { get; private set; }
    public bool ScanIndeterminate { get; private set; }
    private string preparationCaption = "等待检查账号与缓存";
    private bool preparationPaused, preparationAction;
    public double PreparationProgress => Math.Clamp(RegistrationProgress / 3 + ScanProgress * 2 / 3, 0, 100);
    public bool PreparationReady => prepared;
    public bool PreparationActive => preparing && !preparationPaused && !preparationAction && !prepared;
    public bool RegistrationComplete => RegistrationProgress >= 100;
    public string PreparationCaption => prepared ? "缓存已就绪" : preparationPaused ? "已暂停 · " + preparationCaption : preparationCaption;
    public string PreparationActionLabel => preparationPaused ? "继续" : "暂停";
    public bool CanTogglePreparation => CanRescan && (preparing || preparationPaused);
    public bool CanRescan => !disposed && !preparationAction && !resourcesInstalling && !cleanupFailed && !switchingModes && !exiting && !connected && !connecting && !engineConnecting && (!busy || preparing) && settingsReadable;

    private bool busy, connected, polling, connecting, cancelling;
    private bool settingsReadable = true;
    private string status = "未连接", notice = "", logs = "", geoStatus = "尚未检查";
    public string Home => engine.Home;
    public bool IsBusy => busy || preparing || switchingModes;
    public bool IsConnected => connected;
    private bool verifying;
    // Post-connect verification and hot reload keep the completed appearance;
    // only an actual new connection fills the power button with water.
    public bool IsPowerWorking => connecting && !connected && !cancelling;
    public PowerButtonState PowerState => connected || switchingModes && verifying ? PowerButtonState.Connected : IsPowerWorking ? PowerButtonState.Connecting : PowerButtonState.Idle;
    public bool IsStatusWorking => preparing || switchingModes || verifying || connecting;
    public bool IsCheckingNodes => !connected && !cancelling && (preparing || connecting && !engineConnecting);
    public string ConnectionHeading => Status == "已连接（检测中）" ? "已连接" : Status;
    public string SidebarStatus => IsCheckingNodes ? "正在检查" : ConnectionHeading;
    public bool CanConfigure => !preparationAction && !resourcesInstalling && !cleanupFailed && !switchingModes && !exiting && (!busy || preparing) && !connected && settingsReadable;
    public bool CanChangeModes => !resourcesInstalling && !cleanupFailed && !busy && !switchingModes && !exiting && settingsReadable;
    public bool CanToggleFromTray => !resourcesInstalling && !busy && !connecting && !switchingModes && !exiting && settingsReadable;
    public string TrayConnectionLabel => connecting ? "正在连接 VPN…" : connected || cleanupFailed ? "关闭 VPN" : "连接 VPN";
    public string Status { get => status; private set { status = value; Changed(); Changed(nameof(ConnectionHeading)); Changed(nameof(SidebarStatus)); } }
    public string PowerLabel => connecting ? "取消连接" : cleanupFailed ? "重试清理连接" : connected ? "断开连接" : "连接 WARP";
    public string Notice { get => notice; set { notice = value; Changed(); } }
    public string Logs { get => logs; private set { logs = value; Changed(); } }
    public string GeoStatus { get => geoStatus; private set { geoStatus = value; Changed(); } }
    public string ExitIp { get; private set; } = "";
    public string Region { get; private set; } = "";
    public string DataCenter { get; private set; } = "";
    public string Latency { get; private set; } = "";
    public string SessionTime { get; private set; } = "点击电源，开启连接";
    public string Upload { get; private set; } = "";
    public string Download { get; private set; } = "";
    public TrafficSeries UploadSeries { get; }
    public TrafficSeries DownloadSeries { get; }
    public string Protocol { get => settings.Proto; set { if (value != null && value != settings.Proto && CanChangeProtocol) { settings.Proto = value; NodeExport.RefreshFormats(); if (Save() && initialized) StartPreparation(); } } }
    public string Theme { get => settings.Theme; set { if (value != null) { settings.Theme = value; App.ApplyTheme(value); Save(); } } }
    public string Language
    {
        get => Localization.Resolve(settings.Language, System.Globalization.CultureInfo.CurrentUICulture);
        set
        {
            if (value is not ("zh" or "en" or "fa") || value == Language) return;
            var before = settings.Language;
            settings.Language = value;
            if (Save()) Localization.Current.Select(value);
            else { settings.Language = before; Changed(); }
        }
    }
    public string Family { get => settings.IpFamily; set { if (value != null && value != settings.IpFamily) { settings.IpFamily = value; if (Save() && initialized) StartPreparation(); } } }
    public int MixedPort { get => settings.MixedPort; set { settings.MixedPort = value; if (settings.ApiPort == value) settings.ApiPort = value == 9090 ? 9091 : 9090; UpdateLanEndpoints(); Save(); } }
    public int Mtu { get => settings.Mtu; set { settings.Mtu = value; Save(); } }
    public int Timeout { get => settings.TimeoutSec; set { settings.TimeoutSec = value; Save(); } }
    public string Dns { get => settings.DnsCustom; set { settings.DnsCustom = value; Save(); } }
    public bool AllowLan { get => settings.AllowLan; set { settings.AllowLan = value; UpdateLanEndpoints(); Save(); } }
    public bool DnsLeak { get => settings.DnsLeak; set { settings.DnsLeak = value; Save(); } }
    public bool DnsOff { get => settings.DnsOff; set { settings.DnsOff = value; Save(); } }
    public bool AutoConnectAfterPrepare { get => settings.AutoConnectAfterPrepare; set { settings.AutoConnectAfterPrepare = value; Save(); } }
    public bool CacheNeverExpires { get => settings.CacheNeverExpires; set { if (value == settings.CacheNeverExpires) return; settings.CacheNeverExpires = value; Changed(nameof(CanEditCacheDays)); if (Save() && initialized) StartPreparation(); } }
    public int CacheDays { get => settings.CacheDays; set { if (value == settings.CacheDays) return; settings.CacheDays = value; if (Save() && initialized && !settings.CacheNeverExpires) StartPreparation(); } }
    public bool CanEditCacheDays => !CacheNeverExpires;
    public bool CloseToTray { get => settings.CloseToTray; set { settings.CloseToTray = value; Save(); } }
    public bool AutoStart
    {
        get => settings.AutoStart;
        set
        {
            if (value == settings.AutoStart || !settingsReadable) return;
            var old = settings.AutoStart;
            try
            {
                desktop.SetAutoStart(value); settings.AutoStart = value;
                if (!Save()) { desktop.SetAutoStart(old); settings.AutoStart = old; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            { settings.AutoStart = old; Notice = "开机自启动设置失败：" + e.Message; }
            Changed();
        }
    }
    public bool VerifyBeforeConnect { get => settings.VerifyBeforeConnect; set { settings.VerifyBeforeConnect = value; Save(); } }
    public string ScanIntensity { get => settings.ScanIntensity; set { if (value != null && value != settings.ScanIntensity) { settings.ScanIntensity = value; if (Save() && initialized) StartPreparation(); } } }
    public bool ProxySelected { get => settings.CaptureMode == "proxy"; set { if (value) RequestModes("proxy", settings.ProxyMode); } }
    public bool TunSelected { get => settings.CaptureMode == "tun"; set { if (value) RequestModes("tun", settings.ProxyMode); } }
    public bool GlobalSelected { get => settings.ProxyMode == "global"; set { if (value) RequestModes(settings.CaptureMode, "global"); } }
    public bool SplitSelected { get => settings.ProxyMode == "bypass"; set { if (value) RequestModes(settings.CaptureMode, "bypass"); } }
    public MainViewModel(IEngineService? service = null, IDesktopIntegration? integration = null, IResourceService? resourceService = null, TimeProvider? timeProvider = null, Func<IReadOnlyList<string>>? lanAddressProvider = null, Func<int, CancellationToken, Task<Version>>? releaseVersionProvider = null)
    {
        // Never share the installed client's account, state, or core process.
        engine = service ?? new EngineService(AppPaths.DataHome);
        desktop = integration ?? new DesktopIntegration();
        resources = resourceService ?? new ResourceService(engine.Home);
        lanAddresses = lanAddressProvider ?? LanProxyInfo.ActiveAddresses;
        clock = timeProvider ?? TimeProvider.System;
        getReleaseVersion = releaseVersionProvider ?? AppReleaseUpdate.FetchVersionAsync;
        updateHistory = AppReleaseUpdate.History.Load(Home);
        UploadSeries = new(timeProvider); DownloadSeries = new(timeProvider);
        try { settings = ClientSettings.Load(Home); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { settings = new(); settingsReadable = false; Notice = "无法读取设置，已禁止连接以保护原文件：" + e.Message; }
        try { settings.AutoStart = desktop.AutoStartEnabled; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { Notice = "无法读取开机自启动状态：" + e.Message; }
        App.ApplyTheme(settings.Theme);
        Localization.Current.Select(settings.Language);
        NodeExport = new(new NodeExportService(Home), SettingsJson, () => settings.Proto, () => settingsReadable, message => Notice = message);
        UpdateLanEndpoints();
    }
    private void UpdateLanEndpoints()
    {
        try { LanProxyEndpoints = settings.AllowLan ? LanProxyInfo.Format(lanAddresses(), settings.MixedPort) : ""; }
        catch (System.Net.NetworkInformation.NetworkInformationException) { LanProxyEndpoints = "暂时无法读取局域网地址"; }
        Changed(nameof(LanProxyEndpoints));
    }
    private void RequestModes(string capture, string route)
    {
        if (capture == settings.CaptureMode && route == settings.ProxyMode) return;
        if (!CanChangeModes) { Changed(""); return; }
        ModeChangeTask = ChangeModesAsync(capture, route);
    }
    private async Task ChangeModesAsync(string capture, string route)
    {
        SetUpdateProxy(0);
        switchingModes = true; ++sessionRevision; Changed("");
        var oldCapture = settings.CaptureMode; var oldRoute = settings.ProxyMode;
        var applied = false;
        var restartSettingsSaved = false;
        try
        {
            await StopResourceWorkAsync();
            var snapshot = JsonSerializer.Deserialize<ClientSettings>(SettingsJson(), ClientSettings.JsonOptions)!;
            snapshot.CaptureMode = capture; snapshot.ProxyMode = route;
            var error = snapshot.Validate();
            if (error.Length > 0) { Notice = error; return; }
            if (capture == "tun" && !desktop.IsElevated)
            {
                Notice = "TUN 需要管理员权限，正在请求授权重启…";
                var accepted = await desktop.RestartElevatedAsync(connected, async () =>
                {
                    // UAC has succeeded. Until this callback, leave the old VPN
                    // and settings untouched, including on cancellation.
                    await StopPreparationAsync();
                    if (connected || cleanupFailed)
                    {
                        cleanupFailed = true;
                        await engine.RunAsync("desktop-disconnect", Log);
                        cleanupFailed = false;
                        connected = (await engine.ReadSessionAsync()).Connected;
                        if (connected) { Notice = "旧连接未断开，已取消重启。"; return false; }
                    }
                    settings.CaptureMode = capture; settings.ProxyMode = route;
                    return restartSettingsSaved = Save("");
                });
                if (!accepted) { settings.CaptureMode = oldCapture; settings.ProxyMode = oldRoute; Notice = "已取消管理员重启，保留原模式。"; }
                return;
            }
            if (connected)
            {
                if (settings.VerifyBeforeConnect) connected = false;
                Status = "正在切换模式，等待真实连接验证"; Changed("");
                await workflow.WaitAsync();
                try { await engine.RunAsync("desktop-modes", line => HandleEngineLine(line, generation), JsonSerializer.Serialize(snapshot, ClientSettings.JsonOptions)); }
                finally { workflow.Release(); }
                applied = true;
            }
            settings.CaptureMode = capture; settings.ProxyMode = route;
            if (!Save("")) throw new IOException("模式已应用，但设置未保存。");
        }
        catch (Exception e)
        {
            settings.CaptureMode = oldCapture; settings.ProxyMode = oldRoute;
            if (restartSettingsSaved) Save("");
            Notice = "模式切换失败：" + e.Message; Log(e.Message);
            if (applied)
            {
                try { await engine.RunAsync("desktop-modes", Log, SettingsJson()); }
                catch (Exception rollback) { Notice += " 回滚失败，请检查当前模式：" + rollback.Message; }
            }
        }
        finally { verifying = false; switchingModes = false; await RefreshAsync(); Changed(""); }
    }
    private bool Save([CallerMemberName] string name = "")
    {
        Changed(name);
        if (!settingsReadable) return false;
        var error = settings.Validate();
        if (error.Length > 0) { Notice = error; return false; }
        try { settings.Save(Home); Notice = ""; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = "设置保存失败：" + e.Message; return false; }
    }
    public void Log(string line)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Logs += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
            if (Logs.Length > 60000) Logs = Logs[^50000..];
        });
    }
    private void SetBusy(bool value)
    {
        busy = value; Changed("");
    }
    public void InitializePreparation()
    {
        initialized = true;
        if (!connected && settingsReadable) StartPreparation();
    }
    private string SettingsJson() => JsonSerializer.Serialize(settings, ClientSettings.JsonOptions);
    public void StartPreparation(bool forceScan = false, bool resume = false)
    {
        if (disposed || preparationAction || exiting || switchingModes || connected || engineConnecting || !settingsReadable || settings.Validate().Length > 0) return;
        var version = ++generation;
        prepared = false;
        preparationPaused = false;
        preparationCaption = "检查账号与节点缓存";
        RegistrationText = "注册 · 检查账号"; ScanText = "优选 · 等待注册";
        if (!resume) RegistrationProgress = ScanProgress = 0;
        RegistrationIndeterminate = true; ScanIndeterminate = false;
        var snapshot = SettingsJson();
        if (forceScan)
        {
            var request = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!;
            request["rescan"] = true; snapshot = request.ToJsonString();
        }
        preparation = PrepareAsync(version, snapshot);
        Changed("");
    }
    public void RestartPreparation() { if (CanRescan) StartPreparation(forceScan: true); }
    public async Task TogglePreparationAsync()
    {
        if (!CanTogglePreparation) return;
        if (preparationPaused) { StartPreparation(resume: true); return; }
        preparationAction = true; preparationPaused = true; Changed("");
        try { await StopPreparationAsync(); }
        finally { preparationAction = false; Changed(""); }
    }
    private async Task<bool> PrepareAsync(int version, string snapshot)
    {
        if (preparing) { try { await engine.CancelAsync(); } catch (Exception e) { Log(e.Message); } }
        await workflow.WaitAsync();
        if (version != generation || disposed) { workflow.Release(); return false; }
        preparing = true; Changed("");
        try
        {
            await engine.RunAsync("desktop-prepare", line => HandleEngineLine(line, version), snapshot);
            if (version != generation || disposed) return false;
            prepared = true;
            RegistrationProgress = ScanProgress = 100;
            RegistrationText = "注册 · 已就绪";
            return true;
        }
        catch (Exception e)
        {
            if (version == generation && !disposed) { preparationCaption = "注册或优选未完成，请查看提示"; Notice = cancelling ? "已取消准备。" : "准备失败：" + e.Message; Log(e.Message); }
            return false;
        }
        finally
        {
            preparing = false;
            if (version == generation) { RegistrationIndeterminate = ScanIndeterminate = false; }
            workflow.Release(); Changed("");
            if (version == generation && prepared && settings.AutoConnectAfterPrepare)
                _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version == generation && prepared && settings.AutoConnectAfterPrepare && CanRescan && !preparationPaused)
                        _ = ToggleAsync();
                }));
        }
    }
    private void HandleEngineLine(string line, int version)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (version != generation || disposed) return;
            if (line.StartsWith("@@RESOURCE_PROGRESS ", StringComparison.Ordinal))
            { var progress = JsonSerializer.Deserialize<ResourceProgress>(line[20..], ClientSettings.JsonOptions); if (progress != null) HandleResourceProgress(progress); return; }
            if (!line.StartsWith("@@XEHN ", StringComparison.Ordinal)) { Log(line); return; }
            try
            {
                using var doc = JsonDocument.Parse(line[7..]); var root = doc.RootElement;
                if (root.GetProperty("type").GetString() == "connection")
                {
                    var value = root.GetProperty("state").GetString();
                    if (cancelling && value != "disconnected") return;
                    connected = value is "provisional" or "verified";
                    verifying = value is "provisional" or "verifying";
                    Status = value == "provisional" ? "已连接（检测中）" : value == "verifying" ? "正在验证真实连接" : connected ? "已连接" : "未连接";
                }
                else
                {
                    var phase = root.GetProperty("phase").GetString();
                    var text = root.GetProperty("text").GetString() ?? "";
                    var done = root.GetProperty("done").GetDouble(); var total = root.GetProperty("total").GetDouble();
                    var percent = total > 0 ? Math.Clamp(done * 100 / total, 0, 100) : 0;
                    if (phase == "register") { RegistrationText = "注册 · " + text; RegistrationProgress = Math.Max(RegistrationProgress, percent); RegistrationIndeterminate = total <= 0; }
                    if (phase == "scan") { RegistrationProgress = 100; RegistrationIndeterminate = false; ScanText = "优选 · " + text; ScanProgress = Math.Max(ScanProgress, percent); ScanIndeterminate = total <= 0 && ScanProgress == 0; }
                    if (phase is "register" or "scan")
                    {
                        var detail = root.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() ?? "" : text;
                        if (root.TryGetProperty("step", out var step) && root.TryGetProperty("steps", out var steps) && root.TryGetProperty("operation", out var operation))
                            preparationCaption = $"{step.GetInt32()}/{steps.GetInt32()} {operation.GetString()}" + (detail.Length > 0 ? " · " + detail : "");
                        else preparationCaption = (phase == "register" ? "注册 · " : "优选 · ") + text;
                        preparationCaption = preparationCaption.Replace('\n', ' ').Replace('\r', ' ');
                    }
                    if (phase == "error") { preparationCaption = "注册或优选未完成，请查看提示"; ScanText = "优选 · 未完成，请查看提示"; RegistrationIndeterminate = ScanIndeterminate = false; }
                }
                Changed("");
            }
            catch (JsonException) { Log("引擎进度消息格式错误。"); }
        });
    }
    public Task ToggleAsync()
    {
        var task = ToggleCoreAsync();
        if (!cancelling && !task.IsCompleted) connectionOperation = task;
        return task;
    }
    private async Task ToggleCoreAsync()
    {
        if (disposed || preparationAction) return;
        if (connecting)
        {
            if (cancelling) return;
            cancelling = true; Status = "正在取消";
            if (!engineConnecting) { ++generation; prepared = false; preparationCaption = "已取消准备"; }
            connected = false; Changed("");
            try { await engine.CancelAsync(); } catch (Exception e) { Notice = e.Message; }
            return;
        }
        if (resourcesInstalling || busy || switchingModes || exiting || !settingsReadable) return;
        var disconnect = connected || cleanupFailed;
        if (!disconnect && !Save()) return;
        if (!disconnect && TunSelected)
        {
            if (!desktop.IsElevated)
            {
                switchingModes = true; Changed("");
                try { if (!await desktop.RestartElevatedAsync(true, async () => { await StopPreparationAsync(); return Save(""); })) Notice = "已取消管理员重启。"; }
                catch (Exception e) { Notice = "管理员重启失败：" + e.Message; }
                finally { switchingModes = false; Changed(""); }
                return;
            }
        }
        connecting = !disconnect; SetBusy(true); Notice = ""; Status = disconnect ? "正在断开" : "正在检查";
        try
        {
            if (disconnect) { SetUpdateProxy(0); await StopResourceWorkAsync(); }
            if (!disconnect)
            {
                if (!prepared && preparation?.IsCompleted != false) StartPreparation();
                while (!prepared && !cancelling)
                {
                    var pending = preparation;
                    if (pending == null) throw new IOException("连接准备尚未开始。");
                    var ok = await pending;
                    if (pending != preparation) continue; // protocol switched: follow the newest request
                    if (!ok) throw new IOException("注册或优选未完成，未启动连接。");
                }
                if (cancelling) return;
            }
            await workflow.WaitAsync();
            try
            {
                if (cancelling) return;
                if (!disconnect && !Save()) throw new IOException("设置无效，未启动连接。");
                engineConnecting = !disconnect;
                if (engineConnecting) Status = "正在连接";
                Changed("");
                var version = generation;
                await engine.RunAsync(disconnect ? "desktop-disconnect" : "desktop-connect", line => HandleEngineLine(line, version), SettingsJson(), prepared: !disconnect);
                if (disconnect) cleanupFailed = false;
            }
            finally { engineConnecting = false; workflow.Release(); }
        }
        catch (Exception e) { if (disconnect) cleanupFailed = true; prepared = false; connected = verifying = false; Notice = cancelling ? "已取消连接。" : e.Message; Log(e.Message); }
        finally
        {
            connecting = false; cancelling = false; SetBusy(false);
            RegistrationIndeterminate = ScanIndeterminate = false;
            await RefreshAsync(); Changed("");
            if (disconnect && resourcesReady && !cleanupFailed && !exiting) await RunResourceActionAsync("resources-info", showCheckResult: false);
        }
    }
    public async Task RefreshAsync()
    {
        if (!disposed && settings.AllowLan) UpdateLanEndpoints();
        if (polling || busy || switchingModes || exiting || disposed) return;
        var version = generation;
        var revision = sessionRevision;
        polling = true;
        try
        {
            var state = await engine.ReadSessionAsync();
            // A connect or protocol switch may have begun while disk IO was pending.
            if (busy || switchingModes || exiting || disposed || version != generation || revision != sessionRevision) return;
            connected = state.Connected;
            verifying = connected && state.Verification == "checking";
            SetUpdateProxy(connected && state.Verification == "verified" && state.MixedPort is >= 1024 and <= 65535 ? state.MixedPort : 0);
            if (connected && state.Verification == "failed") cleanupFailed = true;
            Status = connected ? state.Verification == "failed" ? "连接异常，请断开后重试" : state.Verification == "checking" ? "已连接（检测中）" : "已连接" : "未连接";
            ExitIp = connected ? state.ExitIp : "";
            Region = connected ? state.Country : "";
            DataCenter = connected ? state.Colo : "";
            Latency = connected && state.Latency > 0 ? $"{state.Latency} ms" : "";
            SessionTime = connected && DateTimeOffset.TryParse(state.Started, out var start) ? (DateTimeOffset.Now - start).ToString(@"hh\:mm\:ss") : "点击电源，开启连接";
            if (state.ProxyWarning.Length > 0 && connected) Notice = "内核已连接，但系统代理设置失败，请查看日志。";
            if (connected)
            {
                try
                {
                    var (up, down) = await engine.TrafficAsync(state.ApiPort);
                    if (busy || switchingModes || disposed || version != generation || revision != sessionRevision) return;
                    Upload = ClientSettings.Speed(up); Download = ClientSettings.Speed(down);
                    UploadSeries.Add(up); DownloadSeries.Add(down);
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
                { if (busy || switchingModes || disposed || version != generation || revision != sessionRevision) return; ClearTraffic(); Status = "流量接口不可用"; }
            }
            else ClearTraffic(disconnected: true);
            Changed("");
            if (updateProxyPort != 0) _ = CheckForAppUpdateAsync(automatic: true);
            if (resourcesReady && !connected && GeoPending && !resourcesBusy)
                resourceOperation = ResourceActionCoreAsync("resources-apply", automatic: true);
            var resourceSession = state.Started.Length > 0 ? state.Started : "current";
            if (resourcesReady && connected && state.Verification == "verified" && !resourcesBusy &&
                (inspectedGeoSession != resourceSession || DateTimeOffset.UtcNow >= nextGeoInspection))
            {
                inspectedGeoSession = resourceSession; nextGeoInspection = DateTimeOffset.UtcNow.AddHours(1);
                autoGeoSession = "";
                resourceOperation = ResourceActionCoreAsync("resources-info", automatic: true);
            }
            if (resourcesReady && connected && state.Verification == "verified" && !resourcesBusy && GeoNeedsDownload && !GeoPending && autoGeoSession != (state.Started.Length > 0 ? state.Started : "current"))
            {
                autoGeoSession = state.Started.Length > 0 ? state.Started : "current";
                resourceOperation = ResourceActionCoreAsync("resources-download-geo-proxy", automatic: true);
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { if (busy || switchingModes || exiting || disposed || version != generation || revision != sessionRevision) return; SetUpdateProxy(0); ClearTraffic(); Notice = "读取连接状态失败：" + e.Message; Changed(""); }
        finally { polling = false; }
    }
    private void SetUpdateProxy(int port)
    {
        if (updateProxyPort == port) return;
        updateProxyPort = port;
        updateCancellation?.Cancel();
    }
    private void SaveUpdateHistory()
    {
        try { updateHistory.Save(Home); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("无法保存更新检查记录：" + e.Message); }
    }
    public async Task CheckForAppUpdateAsync(bool automatic = false)
    {
        if (AppUpdateChecking || exiting || disposed) return;
        if (!connected || updateProxyPort == 0 || switchingModes || busy)
        {
            if (!automatic) { updateMessage = "请先连接 VPN，再检查更新。"; Changed(nameof(AppUpdateDetail)); }
            return;
        }
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (automatic && updateHistory.LastAttempt == today) return;
        using var request = new CancellationTokenSource();
        updateCancellation = request;
        AppUpdateCheckSucceeded = false;
        updateMessage = "";
        updateHistory = updateHistory with { LastAttempt = today };
        SaveUpdateHistory();
        Changed(nameof(AppUpdateChecking)); Changed(nameof(CanCheckAppUpdate)); Changed(nameof(AppUpdateCheckSucceeded)); Changed(nameof(AppUpdateDetail));
        try
        {
            var latest = await getReleaseVersion(updateProxyPort, request.Token);
            request.Token.ThrowIfCancellationRequested();
            updateHistory = updateHistory with { LatestTag = $"v{latest}" };
            SaveUpdateHistory();
            AppUpdateCheckSucceeded = !automatic;
            if (!automatic && !HasAppUpdate) updateMessage = "已是最新版本。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { if (!automatic && !exiting && !disposed) updateMessage = "连接已改变，请重试检查更新。"; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            Log("检查 XEHN 更新失败：" + e.Message);
            if (!automatic) updateMessage = "检查更新失败，请稍后重试。";
        }
        finally
        {
            updateCancellation = null;
            Changed(nameof(AppUpdateChecking)); Changed(nameof(CanCheckAppUpdate)); Changed(nameof(AppUpdateCheckSucceeded)); Changed(nameof(AppUpdateDetail)); Changed(nameof(HasAppUpdate)); Changed(nameof(AppUpdateActionLabel));
        }
    }
    private void ClearTraffic(bool disconnected = false)
    {
        Upload = Download = "";
        if (disconnected) { UploadSeries.Add(0); DownloadSeries.Add(0); }
        // An unavailable API is not a measured zero. Only blank the current
        // labels; history continues to age out using its real timestamps.
    }
    public Task InitializeResourcesAsync() => resourceInitialization ??= InitializeResourcesCoreAsync();
    private async Task InitializeResourcesCoreAsync()
    {
        if (resourcesReady) return;
        CoreVersion = "正在读取版本…"; Changed("");
        await RunResourceActionAsync("resources-info", showCheckResult: false);
        resourcesReady = true;
    }
    public Task RunResourceActionAsync(string command, bool showCheckResult = true)
    {
        if (!CanCheckResources || command == "resources-download-core" && !CanDownloadCore || command == "resources-download-geo" && !CanDownloadGeo) return Task.CompletedTask;
        return resourceOperation = ResourceActionCoreAsync(command, automatic: false, showCheckResult);
    }
    private void UpdateResourceInfo(ResourceInfo info, bool updateCore)
    {
        // Version discovery is independent of a prior protocol-support verdict:
        // a later ordinary refresh can recover a transient cold-start read.
        if (info.CoreVersion.Length > 0) CoreVersion = "v" + info.CoreVersion.TrimStart('v', 'V');
        else if (updateCore) CoreVersion = info.CoreCheck == "未找到内核" ? "未安装" : "版本暂时无法读取";
        if (updateCore) { CoreNeedsDownload = info.CoreNeedsDownload; CoreCheckText = info.CoreCheck; }
        GeoUpdated = info.GeoUpdated.Length > 0 ? info.GeoUpdated : "尚未更新";
        GeoNeedsDownload = info.GeoNeedsDownload; GeoPending = info.GeoPending;
        if (CoreNeedsDownload) CoreResourceSucceeded = false;
        if (GeoNeedsDownload || GeoPending) GeoResourceSucceeded = false;
        Changed("");
    }
    public void ClearResourceSuccess()
    {
        CoreResourceSucceeded = GeoResourceSucceeded = false;
        Changed("");
    }
    private async Task ResourceActionCoreAsync(string command, bool automatic, bool showCheckResult = false)
    {
        resourcesBusy = true;
        resourcesInstalling = command is "resources-download-core" or "resources-apply" || command == "resources-download-geo" && !connected;
        var coreDownload = command == "resources-download-core";
        var coreCheck = command == "resources-check-core";
        var geoDownload = command.Contains("download-geo", StringComparison.Ordinal);
        var geoCheck = command == "resources-info" && (showCheckResult || !resourcesReady);
        if (coreCheck || coreDownload) CoreResourceSucceeded = false;
        if (geoCheck || geoDownload || command == "resources-apply") GeoResourceSucceeded = false;
        CoreChecking = coreCheck; GeoChecking = geoCheck;
        CoreDownloading = coreDownload; GeoDownloading = geoDownload;
        if (geoCheck) GeoResourceMessage = "";
        if (coreCheck) CoreResourceMessage = "";
        if (!automatic) Notice = "";
        if (coreDownload) { CoreDownloadActive = CoreDownloadIndeterminate = true; CoreDownloadProgress = 0; CoreResourceMessage = "正在获取内核下载信息…"; }
        if (geoDownload) { GeoDownloadActive = GeoDownloadIndeterminate = true; GeoDownloadProgress = 0; GeoResourceMessage = "正在下载 GeoFiles…"; }
        Changed("");
        try
        {
            var info = await resources.RunAsync(command, Log, HandleResourceProgress);
            var applyFailed = false;
            var appliedGeo = false;
            if (!connected && info.GeoPending && !exiting && !preparing && !busy && !switchingModes)
            {
                resourcesInstalling = true; Changed("");
                await workflow.WaitAsync();
                try { var applied = await resources.RunAsync("resources-apply", Log, HandleResourceProgress); info = applied with { CoreVersion = info.CoreVersion, CoreNeedsDownload = info.CoreNeedsDownload, CoreCheck = info.CoreCheck }; appliedGeo = !info.GeoPending; }
                catch (Exception e)
                {
                    applyFailed = true; Log("GeoFiles 替换：" + e.Message);
                    GeoResourceMessage = "GeoFiles 已下载，但替换未完成，请重试。";
                    if (!exiting) Notice = GeoResourceMessage;
                }
                finally { workflow.Release(); }
            }
            UpdateResourceInfo(info, coreCheck || coreDownload || command == "resources-info" && !coreCapabilityChecked);
            if (coreCheck || coreDownload) coreCapabilityChecked = true;
            if (geoCheck && showCheckResult || geoDownload) geoUserChecked = true;
            if (coreDownload) CoreResourceMessage = $"mihomo 安装 / 替换成功（{info.CoreVersion}）。";
            if (coreCheck) CoreResourceMessage = info.CoreNeedsDownload ? info.CoreCheck : "mihomo 检查通过，支持全部协议。";
            if (coreDownload || coreCheck) CoreResourceSucceeded = !info.CoreNeedsDownload;
            if (geoCheck && !applyFailed && !appliedGeo && !info.GeoPending)
                GeoResourceMessage = info.GeoNeedsDownload ? "检查完成：GeoFiles 缺失、不完整或已超过 7 天，请下载更新。" : "GeoFiles 检查通过：文件齐全，未超过 7 天，无需更新。";
            if (geoCheck && showCheckResult && !applyFailed && !info.GeoPending && !info.GeoNeedsDownload || appliedGeo) GeoResourceSucceeded = true;
            if (info.GeoPending && !applyFailed) { GeoResourceMessage = "下载完成，等待 VPN 断开后替换。"; Notice = "GeoFiles 已下载，将在本次 VPN 断开后替换。"; }
            else if (!automatic && !applyFailed) Notice = "";
        }
        catch (Exception e) { if (command == "resources-info" && CoreVersion == "正在读取版本…") { CoreVersion = "版本暂时无法读取"; CoreCheckText = "内核版本读取未完成，请重试。"; } if (coreDownload || coreCheck) CoreResourceSucceeded = false; if (geoDownload || geoCheck || command == "resources-apply") GeoResourceSucceeded = false; Log("资源操作：" + e.Message); if (coreDownload) CoreResourceMessage = "内核下载 / 替换未完成，请重试。"; if (coreCheck) CoreResourceMessage = "mihomo 检查未完成，请重试；详情见日志。"; if (geoDownload) GeoResourceMessage = "GeoFiles 下载未完成，请重试。"; if (geoCheck) GeoResourceMessage = "GeoFiles 检查未完成，请重试；详情见日志。"; if (command == "resources-apply") GeoResourceMessage = "GeoFiles 已下载，但替换未完成，请重试。"; if (!exiting) Notice = "资源检查 / 下载未完成：" + e.Message; }
        finally { CoreChecking = GeoChecking = CoreDownloading = GeoDownloading = false; resourcesBusy = resourcesInstalling = false; if (coreDownload) CoreDownloadActive = CoreDownloadIndeterminate = false; if (geoDownload) GeoDownloadActive = GeoDownloadIndeterminate = false; Changed(""); }
    }
    private void HandleResourceProgress(ResourceProgress progress)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (disposed) return;
            var value = progress.Total > 0 ? Math.Clamp(progress.Done * 100.0 / progress.Total, 0, 100) : 0;
            var text = progress.Phase switch
            {
                "download" => $"{progress.File} · {progress.Done / 1048576.0:0.00} MB" + (progress.Total > 0 ? $" / {progress.Total / 1048576.0:0.00} MB（{value:0}%）" : ""),
                "downloaded" => "下载完成，正在校验…",
                "validate" => "下载完成，正在校验…",
                "staged" => "下载完成，等待 VPN 断开后替换。",
                "installed" => progress.Resource == "core" ? "mihomo 已替换成功。" : "GeoFiles 已替换成功。",
                "error" => "GeoFiles 替换未完成，请检查后重试。",
                _ => "正在处理…"
            };
            // The bar represents file transfer only. Keep the workflow lock
            // during verification / installation, but do not leave a full or
            // spinning download bar behind after EOF (including unknown sizes).
            var downloading = progress.Phase == "download" && (progress.Total <= 0 || progress.Done < progress.Total);
            if (progress.Resource == "core") { CoreResourceSucceeded = progress.Phase == "installed"; CoreDownloadProgress = value; CoreDownloadActive = downloading; CoreDownloadIndeterminate = downloading && progress.Total <= 0; CoreResourceMessage = text; }
            if (progress.Resource == "geo") { GeoResourceSucceeded = progress.Phase == "installed"; GeoDownloadProgress = value; GeoDownloadActive = downloading; GeoDownloadIndeterminate = downloading && progress.Total <= 0; GeoResourceMessage = text; }
            Changed("");
        });
    }
    private async Task StopResourceWorkAsync()
    {
        if (!resourceOperation.IsCompleted) { await resources.CancelAsync(); await resourceOperation; if (!exiting) autoGeoSession = ""; }
    }
    public Task GeoAsync(bool repair)
    {
        if (IsBusy || !CanConfigure) return Task.CompletedTask;
        return geoOperation = GeoCoreAsync(repair);
    }
    private async Task GeoCoreAsync(bool repair)
    {
        SetBusy(true); GeoStatus = repair ? "正在下载缺失数据库…" : "正在检查…";
        try
        {
            await StopResourceWorkAsync();
            await engine.RunAsync(repair ? "geo-repair" : "geo-check", Log);
            GeoStatus = "GeoIP / GeoSite 已就绪（本地大小检查，非远端版本检查）";
        }
        catch (Exception e) { GeoStatus = "数据库检查 / 下载未完成，详情见日志。"; Log(e.Message); }
        finally { SetBusy(false); }
    }
    public async Task StopPreparationAsync()
    {
        ++generation; prepared = false;
        try { await engine.CancelAsync(); } catch (Exception e) { Log(e.Message); }
        if (preparation != null) await preparation;
        RegistrationIndeterminate = ScanIndeterminate = false; Changed("");
    }
    public async Task<bool> ShutdownAsync()
    {
        exiting = true; ++sessionRevision; Changed("");
        SetUpdateProxy(0);
        try
        {
            if (switchingModes || busy) await engine.CancelAsync();
            await StopResourceWorkAsync();
            await ModeChangeTask;
            await geoOperation;
            if (connecting) await ToggleAsync();
            await connectionOperation;
            await StopPreparationAsync();
            connected = (await engine.ReadSessionAsync()).Connected;
            if (connected || cleanupFailed)
            {
                SetBusy(true); Status = "正在退出，清理连接";
                try { await engine.RunAsync("desktop-disconnect", Log); cleanupFailed = false; }
                catch { cleanupFailed = true; throw; }
                finally { SetBusy(false); }
            }
            connected = (await engine.ReadSessionAsync()).Connected;
            if (connected) Notice = "退出清理未完成：VPN 仍处于连接状态，请重试退出。";
            return !connected && !busy && !cleanupFailed;
        }
        catch (Exception e) { Notice = "退出清理失败：" + e.Message; return false; }
        finally { exiting = false; Changed(""); }
    }
    public void Dispose() { disposed = true; ++generation; SetUpdateProxy(0); NodeExport.Dispose(); _ = engine.CancelAsync(); _ = resources.CancelAsync(); resources.Dispose(); engine.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}
