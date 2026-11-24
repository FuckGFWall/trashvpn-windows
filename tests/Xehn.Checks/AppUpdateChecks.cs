using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Xehn.App;

internal static class AppUpdateChecks
{
    private static Session Verified => new(Connected: true, MixedPort: 17991, ApiPort: 19090);
    private static string NewHome(string output) => Path.Combine(output, "app-update", Guid.NewGuid().ToString("N"));

    public static void Run(Action<bool, string> check, string output)
    {
        var current = AppReleaseUpdate.CurrentVersion;
        using var release = JsonDocument.Parse("""{"name":"Ignored","tag_name":"v1.0.0","html_url":"https://example.invalid"}""");
        check(AppReleaseUpdate.ParseRelease(release.RootElement) == current &&
            AppReleaseUpdate.ParseTag("v1.10.0") > AppReleaseUpdate.ParseTag("v1.2.0") &&
            AppReleaseUpdate.ParseTag("v1.0.0-beta.2") == null &&
            AppReleaseUpdate.ParseTag("1.1.0.1") == null, "GitHub Release tag, not the name or URL, drives numeric comparison of stable versions");
        check(Task.Run(UsesLocalProxyAsync).GetAwaiter().GetResult(), "production HTTPS update check sends CONNECT api.github.com:443 to the supplied loopback proxy with no successful direct fallback");

        var engine = new UpdateEngine(NewHome(output));
        var clock = new UpdateClock();
        var ports = new List<int>();
        var next = current;
        CancellationToken token = default;
        TaskCompletionSource<Version>? hold = null;
        bool fail = false;
        Task<Version> Fetch(int port, CancellationToken cancellation)
        {
            ports.Add(port);
            token = cancellation;
            return fail ? Task.FromException<Version>(new HttpRequestException("fixture offline")) : hold?.Task ?? Task.FromResult(next);
        }
        using (var model = new MainViewModel(engine, timeProvider: clock, releaseVersionProvider: Fetch))
        {
            model.RefreshAsync().GetAwaiter().GetResult();
            model.CheckForAppUpdateAsync().GetAwaiter().GetResult();
            check(ports.Count == 0 && model.AppUpdateDetail.Contains("请先连接"), "disconnected manual and automatic checks make no requests");
            engine.State = Verified with { Verification = "checking" };
            model.RefreshAsync().GetAwaiter().GetResult();
            model.CheckForAppUpdateAsync().GetAwaiter().GetResult();
            engine.State = Verified with { MixedPort = 0 };
            model.RefreshAsync().GetAwaiter().GetResult();
            check(ports.Count == 0, "provisional or incomplete session state never starts an update request");

            engine.State = Verified;
            model.RefreshAsync().GetAwaiter().GetResult();
            model.RefreshAsync().GetAwaiter().GetResult();
            check(ports.SequenceEqual([17991]) && !model.HasAppUpdate && model.AppUpdateDetail == "", "same v1.0.0 release is not an update; daily check uses the live mixed port rather than settings or the controller port");
            model.CheckForAppUpdateAsync().GetAwaiter().GetResult();
            check(ports.Count == 2 && model.AppUpdateDetail == "已是最新版本。" && model.AppUpdateCheckSucceeded, "manual re-check gives a current-version result even after the daily check");

            next = new(1, 10, 0);
            clock.Now = clock.Now.AddMinutes(3);
            hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            model.RefreshAsync().GetAwaiter().GetResult();
            model.RefreshAsync().GetAwaiter().GetResult();
            model.CheckForAppUpdateAsync().GetAwaiter().GetResult();
            check(ports.Count == 3 && model.AppUpdateChecking && !model.CanCheckAppUpdate, "crossing local midnight triggers exactly one new in-flight check despite poll and click overlap");
            hold.SetResult(next);
            PumpUntil(() => !model.AppUpdateChecking);
            hold = null;
            check(model.HasAppUpdate && model.AppUpdateDetail == "发现新版本 v1.10.0", "a later Release reveals the new-version notice and download action");
        }
        using var reopened = new MainViewModel(engine, timeProvider: clock, releaseVersionProvider: Fetch);
        reopened.RefreshAsync().GetAwaiter().GetResult();
        check(reopened.HasAppUpdate && ports.Count == 3, "latest tag and today's attempt survive restarting the application");

        clock.Now = clock.Now.AddDays(1);
        fail = true;
        reopened.RefreshAsync().GetAwaiter().GetResult();
        reopened.RefreshAsync().GetAwaiter().GetResult();
        check(ports.Count == 4 && reopened.HasAppUpdate && !reopened.AppUpdateChecking, "automatic failure does not spin or erase an already verified update");
        reopened.CheckForAppUpdateAsync().GetAwaiter().GetResult();
        check(ports.Count == 5 && reopened.AppUpdateDetail.Contains("检查更新失败"), "failed automatic check can still be retried manually on the same day");

        fail = false;
        hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reopened.CheckForAppUpdateAsync();
        engine.State = new();
        reopened.RefreshAsync().GetAwaiter().GetResult();
        check(token.IsCancellationRequested, "losing the verified VPN cancels the in-flight release fetch");
        hold.SetResult(current);
        PumpUntil(() => pending.IsCompleted);
        check(reopened.HasAppUpdate && !reopened.AppUpdateChecking, "a late response from the disconnected VPN cannot overwrite a valid update");
        hold = null;
        engine.State = Verified with { MixedPort = 17992 };
        reopened.RefreshAsync().GetAwaiter().GetResult();
        next = new(0, 9, 0);
        reopened.CheckForAppUpdateAsync().GetAwaiter().GetResult();
        check(!reopened.HasAppUpdate && ports[^1] == 17992, "an older release clears the update affordances and a recheck uses the new session port");
    }

    public static MainViewModel PreviewAvailable(string output)
    {
        var engine = new UpdateEngine(NewHome(output)) { State = Verified };
        var model = new MainViewModel(engine, releaseVersionProvider: (_, _) => Task.FromResult(new Version(1, 2, 0)));
        model.RefreshAsync().GetAwaiter().GetResult();
        return model;
    }

    private static async Task<bool> UsesLocalProxyAsync()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fetch = AppReleaseUpdate.FetchVersionAsync(((IPEndPoint)proxy.LocalEndpoint).Port, deadline.Token);
        using var peer = await proxy.AcceptTcpClientAsync(deadline.Token);
        using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var request = await reader.ReadLineAsync(deadline.Token);
        string? header;
        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(deadline.Token))) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        try { await fetch; return false; }
        catch (HttpRequestException) { return request == "CONNECT api.github.com:443 HTTP/1.1"; }
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(1);
        }
        if (!ready()) throw new TimeoutException("App update fixture did not finish.");
    }

    private sealed class UpdateClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 23, 15, 58, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Fixture UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }

    private sealed class UpdateEngine(string home) : IEngineService
    {
        public string Home => home;
        public Session State = new();
        public Task RunAsync(string command, Action<string> log, string? settingsJson = null, bool prepared = false) => throw new NotSupportedException(command);
        public Task CancelAsync() => Task.CompletedTask;
        public Task<Session> ReadSessionAsync() => Task.FromResult(State);
        public Task<(double Up, double Down)> TrafficAsync(int port) => Task.FromResult((0d, 0d));
        public void Dispose() { }
    }
}
