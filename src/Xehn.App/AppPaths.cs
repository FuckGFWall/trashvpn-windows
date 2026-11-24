using System.IO;

namespace Xehn.App;

internal static class AppPaths
{
    internal const string InstallMarker = "installed.mode";
    internal const string DataMarker = ".trashvpn-windows-data";
    internal const string OwnerId = "TrashVPN.Windows.XEHN";
    internal const string InstalledDataFolder = "trashvpn-windows";
    internal const string PreviousDataFolder = "trashvpn";
    internal static readonly string[] SeedFiles = ["mihomo.exe", "geoip.metadb", "geosite.dat"];
    private static readonly Lazy<string> dataHome = new(() => ResolveDataHome(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    public static string DataHome => dataHome.Value;

    internal static bool IsInstalled(string executableDirectory) => File.Exists(Path.Combine(executableDirectory, InstallMarker));

    internal static string ResolveDataHome(string executableDirectory, string localApplicationData)
    {
        if (!IsInstalled(executableDirectory)) return Path.Combine(executableDirectory, "data");
        if (!Path.IsPathFullyQualified(localApplicationData)) throw new IOException("无法定位本地应用数据目录。");
        var current = Path.Combine(localApplicationData, InstalledDataFolder);
        var previous = Path.Combine(localApplicationData, PreviousDataFolder);
        // Windows normally ignores case. Never open data from the older,
        // unrelated TrashVPN client. Reuse the previous name only for an
        // upgrade of this client, identified by its own marker.
        if (!Path.Exists(current) && HasOwnershipMarker(previous)) return previous;
        return current;
    }

    public static void PrepareInstalledData() => PrepareInstalledData(AppContext.BaseDirectory, DataHome);

    internal static void PrepareInstalledData(string executableDirectory, string home)
    {
        if (!IsInstalled(executableDirectory)) return;
        // Never create or take ownership of the old name, even if it
        // disappeared after the path was selected for an existing user.
        var previous = string.Equals(Path.GetFileName(home), PreviousDataFolder, StringComparison.OrdinalIgnoreCase);
        if (!previous) Directory.CreateDirectory(home);
        if (previous && !HasOwnershipMarker(home))
            throw new IOException("数据目录已存在且不属于此安装，未修改原有文件。");
        RequireRegularDirectory(home);
        var ownership = Path.Combine(home, DataMarker);
        if (!File.Exists(ownership))
        {
            if (Directory.EnumerateFileSystemEntries(home).Any())
                throw new IOException("数据目录已存在且不属于此安装，未修改原有文件。");
            using var marker = new StreamWriter(new FileStream(ownership, FileMode.CreateNew));
            marker.Write(OwnerId);
        }
        else if (!HasOwnershipMarker(home))
            throw new IOException("数据目录已存在且不属于此安装，未修改原有文件。");

        var core = Path.Combine(home, "core");
        Directory.CreateDirectory(core);
        RequireRegularDirectory(core);
        foreach (var file in SeedFiles)
        {
            var destination = Path.Combine(core, file);
            if (!File.Exists(destination))
                File.Copy(Path.Combine(executableDirectory, "seed", "core", file), destination);
        }
    }

    private static bool HasOwnershipMarker(string path)
    {
        try
        {
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var marker = Path.Combine(path, DataMarker);
            return File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0 &&
                string.Equals(File.ReadAllText(marker), OwnerId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RequireRegularDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("数据目录不能是符号链接或目录联接。");
    }
}
