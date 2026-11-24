using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Xehn.App;
using Localization = Xehn.App.Localization;

internal static class InstallAndLocaleChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        check(Localization.Resolve(null, new CultureInfo("zh-TW")) == "zh" &&
              Localization.Resolve(null, new CultureInfo("fa-IR")) == "fa" &&
              Localization.Resolve(null, new CultureInfo("en-GB")) == "en" &&
              Localization.Resolve(null, new CultureInfo("de-DE")) == "en" &&
              Localization.Resolve("fa", new CultureInfo("zh-CN")) == "fa", "detect Chinese, Persian and English from Windows and fall back to English for other languages; saved value wins");

        var fixture = Path.Combine(output, "installation-checks", Guid.NewGuid().ToString("N"));
        CheckPortableStartup(check, fixture);
        var app = Path.Combine(fixture, "Program Files", "TrashVPN");
        var local = Path.Combine(fixture, "AppData", "Local");
        Directory.CreateDirectory(app);
        check(AppPaths.ResolveDataHome(app, local) == Path.Combine(app, "data"), "a portable application retains its own data directory");
        File.WriteAllText(Path.Combine(app, AppPaths.InstallMarker), AppPaths.OwnerId);
        var home = AppPaths.ResolveDataHome(app, local);
        check(home == Path.Combine(local, "trashvpn-windows"), "new installations use a distinct lowercase LocalAppData directory");
        var legacy = Path.Combine(local, "TrashVPN");
        Directory.CreateDirectory(legacy);
        var legacySettings = Path.Combine(legacy, "settings.json");
        File.WriteAllText(legacySettings, """{"language":"en"}""");
        check(AppPaths.ResolveDataHome(app, local) == home, "an old TrashVPN directory does not prevent a new installation from choosing its own data home");
        var emptyLegacy = Path.Combine(fixture, "empty-legacy", "TrashVPN");
        Directory.CreateDirectory(emptyLegacy);
        bool refusedLegacy = false;
        try { AppPaths.PrepareInstalledData(app, emptyLegacy); }
        catch (IOException) { refusedLegacy = true; }
        check(refusedLegacy && !Directory.EnumerateFileSystemEntries(emptyLegacy).Any(), "even an empty unrelated directory at the old name cannot be claimed");
        var seed = Path.Combine(app, "seed", "core");
        Directory.CreateDirectory(seed);
        foreach (var name in AppPaths.SeedFiles) File.WriteAllText(Path.Combine(seed, name), "fixture " + name);
        File.WriteAllText(Path.Combine(seed, "account.json"), "must not be copied");
        AppPaths.PrepareInstalledData(app, home);
        check(File.ReadAllText(legacySettings) == """{"language":"en"}""" &&
              Directory.GetFileSystemEntries(legacy).SequenceEqual([legacySettings]), "first launch neither modifies nor claims the unrelated legacy data");
        var core = Path.Combine(home, "core");
        check(File.ReadAllText(Path.Combine(home, AppPaths.DataMarker)) == AppPaths.OwnerId &&
              Directory.GetFiles(core).Length == AppPaths.SeedFiles.Length &&
              AppPaths.SeedFiles.All(n => File.ReadAllText(Path.Combine(core, n)) == "fixture " + n), "installed first run marks its own directory and seeds only mihomo/GeoFiles");
        File.WriteAllText(Path.Combine(core, "mihomo.exe"), "later core");
        new ClientSettings { Language = "fa" }.Save(home);
        AppPaths.PrepareInstalledData(app, home);
        check(File.ReadAllText(Path.Combine(core, "mihomo.exe")) == "later core" &&
              ClientSettings.Load(home).Language == "fa", "installed restart preserves a newer core and the saved UI language");
        var priorLocal = Path.Combine(fixture, "previous-xehn", "Local");
        var priorHome = Path.Combine(priorLocal, "trashvpn");
        Directory.CreateDirectory(priorHome);
        File.WriteAllText(Path.Combine(priorHome, AppPaths.DataMarker), AppPaths.OwnerId);
        new ClientSettings { Language = "fa" }.Save(priorHome);
        var upgradedHome = AppPaths.ResolveDataHome(app, priorLocal);
        check(upgradedHome == priorHome && ClientSettings.Load(upgradedHome).Language == "fa" &&
              !Directory.Exists(Path.Combine(priorLocal, AppPaths.InstalledDataFolder)), "upgrading this client keeps earlier data only when it has the expected ownership marker");
        var preferredHome = Path.Combine(priorLocal, AppPaths.InstalledDataFolder);
        AppPaths.PrepareInstalledData(app, preferredHome);
        check(AppPaths.ResolveDataHome(app, priorLocal) == preferredHome, "the new data directory wins when both marked directories exist");
        var foreign = Path.Combine(fixture, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "another app");
        bool refused = false;
        try { AppPaths.PrepareInstalledData(app, foreign); }
        catch (IOException) { refused = true; }
        check(refused && Directory.GetFileSystemEntries(foreign).Length == 1, "installed startup will not claim or overwrite an unrelated pre-existing data directory");

        using var model = AppUpdateChecks.PreviewAvailable(output);
        using var tray = new TrayService(model, () => { }, () => Task.CompletedTask, visible: false);
        string Label(int index) => ((MenuItem)tray.ContextMenu.Items[index]).Header.ToString()!;
        model.Language = "en";
        check(Label(0) == "Show window" && Label(1) == "Disconnect VPN" && Label(2) == "Exit",
            "open tray commands are localized even without recreation");
        model.Language = "fa";
        check(tray.ContextMenu.FlowDirection == FlowDirection.RightToLeft && Label(2) == "خروج" &&
            ClientSettings.Load(model.Home).Language == "fa", "Persian tray is RTL and language changes are persisted");
        model.Language = "zh";
    }

    private static void CheckPortableStartup(Action<bool, string> check, string fixture)
    {
        var portable = Directory.CreateDirectory(Path.Combine(fixture, "portable"));
        var data = Path.Combine(portable.FullName, "data");
        var desktop = new PortableDesktop();
        (bool Stop, string? Error) Start(PortableDesktop target) =>
            PortableStartup.CheckAsync(portable.FullName, target).GetAwaiter().GetResult();

        check(Start(desktop) == (false, null) && desktop.Requests == 0 && !Directory.Exists(data),
            "writable portable startup neither elevates nor creates probe files or data folders");
        var core = Directory.CreateDirectory(Path.Combine(data, "core"));
        WithNoWriteAccess(core, () =>
        {
            var canceled = Start(desktop);
            check(canceled.Stop && canceled.Error!.Contains(PortableStartup.ElevationCanceled) &&
                  canceled.Error.Contains(core.FullName) && desktop.Requests == 1 && !desktop.Reconnect,
                "portable startup requests a single cold elevation for an otherwise writable folder with protected core data");
        });
        check(Start(desktop) == (false, null) && desktop.Requests == 1, "ordinary startup can proceed once its directories are writable");

        WithNoWriteAccess(portable, () =>
        {
            var granted = new PortableDesktop { Granted = true };
            check(Start(granted) == (true, null) && granted.Requests == 1 && !granted.Reconnect,
                "a protected extraction directory hands off startup without reconnecting or opening the unelevated UI");
            var elevated = new PortableDesktop { IsElevated = true };
            var blocked = Start(elevated);
            check(blocked.Stop && blocked.Error!.Contains(PortableStartup.MoveToWritableFolder) && elevated.Requests == 0,
                "an elevated copy rechecks accessibility and cannot enter an elevation loop");
        });

        var invalid = Directory.CreateDirectory(Path.Combine(fixture, "invalid-portable"));
        File.WriteAllText(Path.Combine(invalid.FullName, "data"), "not a directory");
        var unusable = PortableStartup.CheckAsync(invalid.FullName, desktop).GetAwaiter().GetResult();
        check(unusable.Stop && unusable.Error!.Contains(PortableStartup.MoveToWritableFolder) && desktop.Requests == 1,
            "a file in place of the portable data directory is reported without requesting elevation");

        File.WriteAllText(Path.Combine(portable.FullName, AppPaths.InstallMarker), AppPaths.OwnerId);
        WithNoWriteAccess(portable, () =>
        {
            check(Start(desktop) == (false, null) && desktop.Requests == 1, "the installed edition skips the portable directory probe");
        });
    }

    private static void WithNoWriteAccess(DirectoryInfo folder, Action check)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var blocked = folder.GetAccessControl(AccessControlSections.Access);
        var original = blocked.GetSecurityDescriptorBinaryForm();
        blocked.AddAccessRule(new FileSystemAccessRule(identity.User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
        folder.SetAccessControl(blocked);
        try { check(); }
        finally
        {
            blocked.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            folder.SetAccessControl(blocked);
        }
    }

    private sealed class PortableDesktop : IDesktopIntegration
    {
        public bool IsElevated { get; init; }
        public bool Granted { get; init; }
        public int Requests { get; private set; }
        public bool Reconnect { get; private set; }
        public bool AutoStartEnabled => false;
        public void SetAutoStart(bool enabled) => throw new NotSupportedException();
        public async Task<bool> RestartElevatedAsync(bool reconnect, Func<Task<bool>> prepare)
        {
            Requests++;
            Reconnect = reconnect;
            return Granted && await prepare();
        }
    }
}
