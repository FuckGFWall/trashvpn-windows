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
            var connect = vm.ToggleAsync();
            check(fake.Calls.Count == 1 && vm.CanChangeProtocol, "connect waits for existing preparation without duplicate scan");
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
            fake.Last.Emit("connection", state: "disconnected");
            check(!vm.IsConnected, "failed background verification immediately retracts connection");
            fake.Fail(); PumpUntil(() => connect.IsCompleted);
            check(!vm.IsConnected && !vm.IsBusy, "failed connect leaves workflow idle");

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
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
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
        public Task<Session> ReadSessionAsync() => PendingRead?.Task ?? Task.FromResult(new Session(Connected));
        public Task<(double Up, double Down)> TrafficAsync(int port) => Task.FromResult((0d, 0d));
        public void Dispose() { }
    }
    private sealed class Call(string command, Action<string> log, string settings, bool prepared)
    {
        public string Command => command;
        public bool Prepared => prepared;
        public string Protocol => JsonSerializer.Deserialize<ClientSettings>(settings, ClientSettings.JsonOptions)!.Proto;
        public bool VerifyBeforeConnect => JsonSerializer.Deserialize<ClientSettings>(settings, ClientSettings.JsonOptions)!.VerifyBeforeConnect;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(string type, string phase = "", string state = "", int done = 0, int total = 0, string text = "test") => log("@@XEHN " + JsonSerializer.Serialize(new { type, phase, state, done, total, text }));
    }
}
