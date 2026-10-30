using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xehn.App;
public interface IEngineService : IDisposable
{
 string Home { get; }
 Task RunAsync(string command, Action<string> log, string? settingsJson = null, bool prepared = false);
 Task CancelAsync();
 Task<Session> ReadSessionAsync();
 Task<(double Up, double Down)> TrafficAsync(int port);
}
public sealed class EngineService : IEngineService
{
    public string Home { get; }
    private readonly string binary;
    private Process? active;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    public EngineService(string home, string? binaryPath = null)
    {
        Home = Path.GetFullPath(home);
        binary = binaryPath ?? Path.Combine(AppContext.BaseDirectory, "engine", "xehn-engine.exe");
    }
    public async Task RunAsync(string command, Action<string> log, string? settingsJson = null, bool prepared = false)
    {
        if (active != null) throw new InvalidOperationException("引擎正在执行另一项操作。");
        if (!File.Exists(binary)) throw new FileNotFoundException("尚未构建连接引擎，请运行 build.ps1。", binary);
        Directory.CreateDirectory(Home);
        var start = new ProcessStartInfo(binary) { WorkingDirectory = Home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(command);
        start.Environment["WARP_CLIENT_HOME"] = Home;
        start.Environment["NO_COLOR"] = "1";
        if (settingsJson != null) start.Environment["WARP_DESKTOP_SETTINGS"] = settingsJson;
        if (prepared) start.Environment["WARP_PREPARED"] = "1";
        using var process = new Process { StartInfo = start };
        active = process;
        try
        {
            if (!process.Start()) throw new IOException("无法启动本地引擎。");
            async Task Read(StreamReader stream)
            {
                while (await stream.ReadLineAsync() is { } line)
                    log(Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", ""));
            }
            await Task.WhenAll(process.WaitForExitAsync(), Read(process.StandardOutput), Read(process.StandardError));
            if (process.ExitCode != 0) throw new IOException($"引擎操作失败（{process.ExitCode}），详情见日志。");
        }
        finally { active = null; }
    }
    public async Task CancelAsync()
    {
        var p = active;
        if (p == null) return;
        try
        {
            if (p.HasExited) return;
            await p.StandardInput.WriteLineAsync("cancel");
            await p.StandardInput.FlushAsync();
        }
        // Exit/disposal can race the cancellation request; the worker is already gone.
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }
    public async Task<Session> ReadSessionAsync()
    {
        var file = Path.Combine(Home, "state.json");
        if (!File.Exists(file)) return new();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = doc.RootElement;
        string Text(string key) => root.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        int Number(string key) => root.TryGetProperty(key, out var v) ? v.GetInt32() : 0;
        var alive = false;
        if (root.TryGetProperty("connected", out var c) && c.GetBoolean() && Number("core_pid") > 0)
        {
            try { using var p = Process.GetProcessById(Number("core_pid")); alive = !p.HasExited && string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.Combine(Home, "core", "mihomo.exe"), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return new(alive, Text("exit_ip"), Text("country"), Text("colo"), Number("latency_ms"), Text("started_at"), Number("api_port"), Text("proxy_warn"));
    }
    public async Task<(double Up, double Down)> TrafficAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var response = await http.GetAsync($"http://127.0.0.1:{port}/traffic", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("内核未返回流量数据。");
        using var doc = JsonDocument.Parse(line);
        return (doc.RootElement.GetProperty("up").GetDouble(), doc.RootElement.GetProperty("down").GetDouble());
    }
    public void Dispose() => http.Dispose();
}
public sealed record Session(bool Connected = false, string ExitIp = "", string Country = "", string Colo = "", int Latency = 0, string Started = "", int ApiPort = 9090, string ProxyWarning = "");
