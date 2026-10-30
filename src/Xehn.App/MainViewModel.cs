using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Windows;
using System.Text.Json;

namespace Xehn.App;
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ClientSettings settings;
    private readonly IEngineService engine;
    private readonly SemaphoreSlim workflow = new(1, 1);
    private Task<bool>? preparation;
    private int generation;
    private bool preparing, prepared, initialized, engineConnecting, disposed;
    public bool IsPreparing => preparing;
    public bool ConnectionRequested => connecting;
    public bool CanChangeProtocol => (!busy || preparing) && !connected && !engineConnecting && settingsReadable;
    public string RegistrationText { get; private set; } = "注册 · 待检查";
    public string ScanText { get; private set; } = "优选 · 待检查";
    public double RegistrationProgress { get; private set; }
    public double ScanProgress { get; private set; }
    public bool RegistrationIndeterminate { get; private set; }
    public bool ScanIndeterminate { get; private set; }

    private bool busy, connected, polling, connecting, cancelling;
    private bool settingsReadable = true;
    private string status = "未连接", notice = "", logs = "", geoStatus = "尚未检查";
    public string Home => engine.Home;
    public bool IsBusy => busy || preparing;
    public bool IsConnected => connected;
    public bool CanConfigure => (!busy || preparing) && !connected && settingsReadable;
    public string Status { get => status; private set { status = value; Changed(); } }
    public string PowerLabel => connecting ? "取消连接" : connected ? "断开连接" : "连接 WARP";
    public string Notice { get => notice; set { notice = value; Changed(); } }
    public string Logs { get => logs; private set { logs = value; Changed(); } }
    public string GeoStatus { get => geoStatus; private set { geoStatus = value; Changed(); } }
    public string ExitIp { get; private set; } = "";
    public string Region { get; private set; } = "";
    public string Latency { get; private set; } = "";
    public string SessionTime { get; private set; } = "点击电源，开启连接";
    public string Upload { get; private set; } = "";
    public string Download { get; private set; } = "";
    public double[] UploadSamples { get; private set; } = [];
    public double[] DownloadSamples { get; private set; } = [];
    public string Protocol { get => settings.Proto; set { if (value != null && value != settings.Proto && CanChangeProtocol) { settings.Proto = value; if (Save() && initialized) StartPreparation(); } } }
    public string Theme { get => settings.Theme; set { if (value != null) { settings.Theme = value; App.ApplyTheme(value); Save(); } } }
    public string Family { get => settings.IpFamily; set { if (value != null && value != settings.IpFamily) { settings.IpFamily = value; if (Save() && initialized) StartPreparation(); } } }
    public int MixedPort { get => settings.MixedPort; set { settings.MixedPort = value; Save(); } }
    public int ApiPort { get => settings.ApiPort; set { settings.ApiPort = value; Save(); } }
    public int Mtu { get => settings.Mtu; set { settings.Mtu = value; Save(); } }
    public int Timeout { get => settings.TimeoutSec; set { settings.TimeoutSec = value; Save(); } }
    public string Dns { get => settings.DnsCustom; set { settings.DnsCustom = value; Save(); } }
    public bool AllowLan { get => settings.AllowLan; set { settings.AllowLan = value; Save(); } }
    public bool DnsLeak { get => settings.DnsLeak; set { settings.DnsLeak = value; Save(); } }
    public bool DnsOff { get => settings.DnsOff; set { settings.DnsOff = value; Save(); } }
    public bool AutoConnect { get => settings.AutoConnect; set { settings.AutoConnect = value; Save(); } }
    public bool Rescan { get => settings.Rescan; set { settings.Rescan = value; Save(); } }
    public bool VerifyBeforeConnect { get => settings.VerifyBeforeConnect; set { settings.VerifyBeforeConnect = value; Save(); } }
    public string ScanIntensity { get => settings.ScanIntensity; set { if (value != null && value != settings.ScanIntensity) { settings.ScanIntensity = value; if (Save() && initialized) StartPreparation(); } } }
    public bool AutoRepair { get => settings.AutoRepair; set { settings.AutoRepair = value; Save(); } }
    public bool ProxySelected { get => settings.CaptureMode == "proxy"; set { if (value) { settings.CaptureMode = "proxy"; Save(); Changed(nameof(TunSelected)); } } }
    public bool TunSelected { get => settings.CaptureMode == "tun"; set { if (value) { settings.CaptureMode = "tun"; Save(); Changed(nameof(ProxySelected)); } } }
    public bool GlobalSelected { get => settings.ProxyMode == "global"; set { if (value) { settings.ProxyMode = "global"; Save(); Changed(nameof(SplitSelected)); } } }
    public bool SplitSelected { get => settings.ProxyMode == "bypass"; set { if (value) { settings.ProxyMode = "bypass"; Save(); Changed(nameof(GlobalSelected)); } } }
    public MainViewModel(IEngineService? service = null)
    {
        // Never share the installed client's account, state, or core process.
        engine = service ?? new EngineService(Path.Combine(AppContext.BaseDirectory, "data"));
        try { settings = ClientSettings.Load(Home); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { settings = new(); settingsReadable = false; Notice = "无法读取设置，已禁止连接以保护原文件：" + e.Message; }
        App.ApplyTheme(settings.Theme);
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
        busy = value; Changed(nameof(IsBusy)); Changed(nameof(CanConfigure)); Changed(nameof(PowerLabel)); Changed(nameof(CanChangeProtocol));
    }
    public void InitializePreparation()
    {
        initialized = true;
        if (!connected && settingsReadable) StartPreparation();
    }
    private string SettingsJson() => JsonSerializer.Serialize(settings, ClientSettings.JsonOptions);
    public void StartPreparation()
    {
        if (disposed || connected || engineConnecting || !settingsReadable || settings.Validate().Length > 0) return;
        var version = ++generation;
        prepared = false;
        RegistrationText = "注册 · 检查账号"; ScanText = "优选 · 等待注册";
        RegistrationProgress = ScanProgress = 0;
        RegistrationIndeterminate = true; ScanIndeterminate = false;
        var snapshot = SettingsJson();
        preparation = PrepareAsync(version, snapshot);
        Changed("");
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
            RegistrationText = "注册 · 已就绪"; ScanText = "优选 · 缓存已就绪";
            return true;
        }
        catch (Exception e)
        {
            if (version == generation && !disposed) { Notice = cancelling ? "已取消准备。" : "准备失败：" + e.Message; Log(e.Message); }
            return false;
        }
        finally
        {
            preparing = false;
            if (version == generation) { RegistrationIndeterminate = ScanIndeterminate = false; }
            workflow.Release(); Changed("");
        }
    }
    private void HandleEngineLine(string line, int version)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (version != generation || disposed) return;
            if (!line.StartsWith("@@XEHN ", StringComparison.Ordinal)) { Log(line); return; }
            try
            {
                using var doc = JsonDocument.Parse(line[7..]); var root = doc.RootElement;
                if (root.GetProperty("type").GetString() == "connection")
                {
                    var value = root.GetProperty("state").GetString();
                    if (cancelling && value != "disconnected") return;
                    connected = value is "provisional" or "verified";
                    Status = value == "provisional" ? "已连接（检测中）" : connected ? "已连接" : "未连接";
                }
                else
                {
                    var phase = root.GetProperty("phase").GetString();
                    var text = root.GetProperty("text").GetString() ?? "";
                    var done = root.GetProperty("done").GetDouble(); var total = root.GetProperty("total").GetDouble();
                    var percent = total > 0 ? Math.Clamp(done * 100 / total, 0, 100) : 0;
                    if (phase == "register") { RegistrationText = "注册 · " + text; RegistrationProgress = percent; RegistrationIndeterminate = total <= 0; }
                    if (phase == "scan") { ScanText = "优选 · " + (text.StartsWith("Phase") ? "探测与验证节点" : text); ScanProgress = percent; ScanIndeterminate = total <= 0; }
                    if (phase == "error") { ScanText = "优选 · 未完成，请查看提示"; RegistrationIndeterminate = ScanIndeterminate = false; }
                }
                Changed("");
            }
            catch (JsonException) { Log("引擎进度消息格式错误。"); }
        });
    }
    public async Task ToggleAsync()
    {
        if (connecting)
        {
            if (cancelling) return;
            cancelling = true; Status = "正在取消";
            if (!engineConnecting) { ++generation; prepared = false; }
            connected = false; Changed("");
            try { await engine.CancelAsync(); } catch (Exception e) { Notice = e.Message; }
            return;
        }
        if (busy || !settingsReadable) return;
        if (!connected && !Save()) return;
        if (!connected && TunSelected)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { Notice = "TUN 需要管理员权限。请关闭后以管理员身份重新运行。"; return; }
        }
        var disconnect = connected;
        connecting = !disconnect; SetBusy(true); Notice = ""; Status = disconnect ? "正在断开" : "正在连接";
        try
        {
            if (!disconnect)
            {
                if (!prepared && preparation?.IsCompleted != false) StartPreparation();
                if (settings.Rescan && prepared) StartPreparation();
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
                engineConnecting = !disconnect; Changed("");
                var version = generation;
                await engine.RunAsync(disconnect ? "disconnect" : "desktop-connect", line => HandleEngineLine(line, version), SettingsJson(), prepared: !disconnect);
            }
            finally { engineConnecting = false; workflow.Release(); }
        }
        catch (Exception e) { prepared = false; connected = false; Notice = cancelling ? "已取消连接。" : e.Message; Log(e.Message); }
        finally
        {
            connecting = false; cancelling = false; SetBusy(false);
            RegistrationIndeterminate = ScanIndeterminate = false;
            await RefreshAsync(); Changed("");
        }
    }
    public async Task RefreshAsync()
    {
        if (polling || busy || disposed) return;
        var version = generation;
        polling = true;
        try
        {
            var state = await engine.ReadSessionAsync();
            // A connect or protocol switch may have begun while disk IO was pending.
            if (busy || disposed || version != generation) return;
            connected = state.Connected; Status = connected ? "已连接" : "未连接";
            ExitIp = connected ? state.ExitIp : "";
            Region = connected ? string.Join(" · ", new[] { state.Country, state.Colo }.Where(s => s.Length > 0)) : "";
            Latency = connected && state.Latency > 0 ? $"{state.Latency} ms" : "";
            SessionTime = connected && DateTimeOffset.TryParse(state.Started, out var start) ? (DateTimeOffset.Now - start).ToString(@"hh\:mm\:ss") : "点击电源，开启连接";
            if (state.ProxyWarning.Length > 0 && connected) Notice = "内核已连接，但系统代理设置失败，请查看日志。";
            if (connected)
            {
                try
                {
                    var (up, down) = await engine.TrafficAsync(state.ApiPort);
                    if (busy || disposed || version != generation) return;
                    Upload = ClientSettings.Speed(up); Download = ClientSettings.Speed(down);
                    UploadSamples = up > 0 ? UploadSamples.Append(up).TakeLast(45).ToArray() : [];
                    DownloadSamples = down > 0 ? DownloadSamples.Append(down).TakeLast(45).ToArray() : [];
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
                { if (busy || disposed || version != generation) return; ClearTraffic(); Status = "流量接口不可用"; }
            }
            else ClearTraffic();
            Changed("");
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { if (busy || disposed || version != generation) return; ClearTraffic(); Notice = "读取连接状态失败：" + e.Message; Changed(""); }
        finally { polling = false; }
    }
    private void ClearTraffic() { Upload = Download = ""; UploadSamples = []; DownloadSamples = []; }
    public async Task GeoAsync(bool repair)
    {
        if (IsBusy || !CanConfigure) return;
        SetBusy(true); GeoStatus = repair ? "正在下载缺失数据库…" : "正在检查…";
        try
        {
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
    public void Dispose() { disposed = true; ++generation; _ = engine.CancelAsync(); engine.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}
