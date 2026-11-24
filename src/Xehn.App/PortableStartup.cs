using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Xehn.App;

internal static class PortableStartup
{
    internal const string MoveToWritableFolder = "便携版目录不可写。请把完整程序移到可写目录后重试。";
    internal const string ElevationCanceled = "已取消管理员授权，便携版未启动。";
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadOnlyVolume = 0x00080000;
    private const uint OpenExisting = 3;

    // Returns Stop for either a successful handoff or an error. The elevated
    // copy runs this check again, but never asks for elevation a second time.
    internal static async Task<(bool Stop, string? Error)> CheckAsync(string executableDirectory, IDesktopIntegration desktop)
    {
        if (AppPaths.IsInstalled(executableDirectory)) return (false, null);
        try
        {
            CheckDirectory(executableDirectory, optional: false);
            var data = Path.Combine(executableDirectory, "data");
            if (CheckDirectory(data, optional: true))
                CheckDirectory(Path.Combine(data, "core"), optional: true);
            return (false, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (desktop.IsElevated) return (true, WithPathError(ex));
            try
            {
                return await desktop.RestartElevatedAsync(false, () => Task.FromResult(true))
                    ? (true, null)
                    : (true, ElevationCanceled + "\n" + WithPathError(ex));
            }
            catch (Exception e)
            {
                return (true, "管理员启动未完成：" + e.Message + "\n" + WithPathError(ex));
            }
        }
        catch (IOException ex) { return (true, WithPathError(ex)); }
    }

    private static string WithPathError(Exception ex) => MoveToWritableFolder + "\n" + ex.Message;

    private static bool CheckDirectory(string path, bool optional)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (IOException ex) when (optional && ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException(path);

        // Ask Windows for the actual directory creation rights of this token,
        // without creating, replacing or deleting a test file.
        using var handle = CreateFile(path, FileAddFile | FileAddSubdirectory,
            FileShare.ReadWrite | FileShare.Delete, nint.Zero, OpenExisting, FileFlagBackupSemantics, nint.Zero);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            var detail = path + ": " + error.Message;
            if (error.NativeErrorCode is 5 or 740 or 1314) throw new UnauthorizedAccessException(detail, error);
            throw new IOException(detail, error);
        }
        if (GetVolumeInformationByHandle(handle, nint.Zero, 0, nint.Zero, nint.Zero, out var flags, nint.Zero, 0) &&
            (flags & FileReadOnlyVolume) != 0) throw new IOException(path);
        return true;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare sharing,
        nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle file, nint volumeName, uint volumeNameSize,
        nint serial, nint maximumComponentLength, out uint flags, nint fileSystemName, uint fileSystemNameSize);
}
