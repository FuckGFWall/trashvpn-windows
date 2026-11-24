using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Xehn.App;

public static class LanProxyInfo
{
    // Enumerate local adapter metadata only; never send traffic or use a public
    // IP lookup. Prefer IPv4 because it can be entered directly by LAN clients.
    public static IReadOnlyList<string> ActiveAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address.ToString()).ToArray();

    public static string Format(IEnumerable<string> addresses, int port)
    {
        var usable = addresses.Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(a => a != null && !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any) && !a.IsIPv6LinkLocal && !a.IsIPv6Multicast)
            .Select(a => a!).Distinct().ToArray();
        var v4 = usable.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)).ToArray();
        var selected = v4.Length > 0 ? v4 : usable.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
        return selected.Length == 0 ? "未检测到可用的局域网地址" : string.Join("\n", selected.Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]:{port}" : $"{a}:{port}"));
    }
}
