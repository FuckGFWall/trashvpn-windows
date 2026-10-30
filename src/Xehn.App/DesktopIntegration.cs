using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

namespace Xehn.App;

public interface IDesktopIntegration
{
    bool IsElevated { get; }
    bool AutoStartEnabled { get; }
    void SetAutoStart(bool enabled);
    Task<bool> RestartElevatedAsync(bool reconnect, Func<Task<bool>> prepare);
}

public sealed class DesktopIntegration : IDesktopIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Xehn.Fluent.Desktop";
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "Xehn.App.exe");
    public static string AutoStartCommand(string executable) => $"\"{Path.GetFullPath(executable)}\"";
    public bool IsElevated
    {
        get { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public bool AutoStartEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return string.Equals(key?.GetValue(RunName) as string, AutoStartCommand(Executable), StringComparison.OrdinalIgnoreCase); }
    }
    public void SetAutoStart(bool enabled)
    {
        // This value belongs only to this desktop edition, never to the old GUI.
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            if (!File.Exists(Executable)) throw new FileNotFoundException("请先构建主程序后启用开机自启动。", Executable);
            var command = AutoStartCommand(Executable);
            if (command.Length > 260) throw new IOException("程序路径过长，无法注册开机自启动。");
            key.SetValue(RunName, command, RegistryValueKind.String);
        }
        else key.DeleteValue(RunName, false);
        if (AutoStartEnabled != enabled) throw new IOException("开机自启动设置未生效。");
    }
    public async Task<bool> RestartElevatedAsync(bool reconnect, Func<Task<bool>> prepare)
    {
        var token = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "ready"));
        using var abort = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "abort"));
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "started"));
        var start = new ProcessStartInfo(Executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in new[] { "--elevated-restart", Environment.ProcessId.ToString(), token, reconnect ? "1" : "0" }) start.ArgumentList.Add(arg);
        Process? child;
        // ShellExecute / UAC can wait on shell extensions. Never block WPF's
        // dispatcher while the elevated child is trying to handshake with us.
        try { child = await StartOnStaAsync(start).WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; } // UAC declined: keep existing connection.
        using (child)
        {
            if (child == null) throw new IOException("无法启动管理员进程。");
            try
            {
                using var exited = new EventWaitHandle(false, EventResetMode.ManualReset);
                exited.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(child.Handle, ownsHandle: false);
                if (await Task.Run(() => WaitHandle.WaitAny([started, exited], TimeSpan.FromSeconds(20))) != 0) throw new IOException("管理员进程未就绪或提前退出，未关闭当前客户端。");
                if (!await prepare()) return false;
                if (child.HasExited) throw new IOException("管理员进程提前退出，未关闭当前客户端。");
                // The elevated child waits for BOTH this signal and parent exit,
                // so it cannot race the single-instance mutex or the old core.
                ready.Set();
                ((App)Application.Current).IsRestarting = true;
                Application.Current.Shutdown();
                return true;
            }
            finally { abort.Set(); }
        }
    }
    public static Task<Process?> StartOnStaAsync(ProcessStartInfo start) => RunOnStaAsync(() => Process.Start(start));
    public static Task<T> RunOnStaAsync<T>(Func<T> operation)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.SetResult(operation()); } catch (Exception e) { result.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task;
    }
    public static bool NeedsStartupElevation(string captureMode, bool elevated) => captureMode == "tun" && !elevated;
    private static string EventName(string token, string kind) => @"Local\Xehn.Restart." + token + "." + kind;
    public static async Task<bool> WaitForRestartAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--elevated-restart" || !int.TryParse(args[1], out var pid) || pid <= 0 ||
            !Guid.TryParseExact(args[2], "N", out _) || args[3] is not ("0" or "1") || !new DesktopIntegration().IsElevated) return false;
        try
        {
            using var parent = Process.GetProcessById(pid);
            // Do not wait on arbitrary processes supplied on a command line.
            if (!string.Equals(parent.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) return false;
            using var ready = EventWaitHandle.OpenExisting(EventName(args[2], "ready"));
            using var abort = EventWaitHandle.OpenExisting(EventName(args[2], "abort"));
            using var started = EventWaitHandle.OpenExisting(EventName(args[2], "started"));
            started.Set();
            if (await Task.Run(() => WaitHandle.WaitAny([ready, abort], TimeSpan.FromMinutes(2))) != 0) return false;
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or Win32Exception or WaitHandleCannotBeOpenedException or TimeoutException or InvalidOperationException or UnauthorizedAccessException) { return false; }
    }
}
