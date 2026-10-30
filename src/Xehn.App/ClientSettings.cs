using System.IO;
using System.Text.Json;

namespace Xehn.App;
public sealed class ClientSettings
{
    public string Proto { get; set; } = "awg";
    public string Theme { get; set; } = "light";
    public string CaptureMode { get; set; } = "proxy";
    public string ProxyMode { get; set; } = "global";
    public string IpFamily { get; set; } = "v4";
    public string OuterProto { get; set; } = "wg";
    public string InnerProto { get; set; } = "wg";
    public int MixedPort { get; set; } = 7891;
    public int ApiPort { get; set; } = 9090;
    public int Mtu { get; set; }
    public int TimeoutSec { get; set; } = 2;
    public bool AllowLan { get; set; }
    public bool AutoConnect { get; set; }
    public bool AutoRepair { get; set; } = true;
    public bool Rescan { get; set; }
    public bool VerifyBeforeConnect { get; set; }
    public string ScanIntensity { get; set; } = "medium";
    public string DnsCustom { get; set; } = "";
    public bool DnsLeak { get; set; } = true;
    public bool DnsOff { get; set; }
    public string Validate()
    {
        if (!new[] { "low", "medium", "high" }.Contains(ScanIntensity)) return "未知的优选强度。";
        if (MixedPort is < 1024 or > 65535 || ApiPort is < 1024 or > 65535) return "端口需在 1024–65535 之间。";
        if (MixedPort == ApiPort) return "代理端口与 API 端口不能相同。";
        if (Mtu != 0 && Mtu is < 576 or > 9000) return "MTU 应为 0（自动）或 576–9000。";
        if (TimeoutSec is < 1 or > 60) return "探测超时应为 1–60 秒。";
        if (!new[] { "awg", "wg", "masque", "masque-h2", "wwarp", "masque-mm" }.Contains(Proto)) return "未知的连接协议。";
        if (!new[] { "proxy", "tun" }.Contains(CaptureMode)) return "未知的接管方式。";
        if (!new[] { "global", "bypass" }.Contains(ProxyMode)) return "未知的路由模式。";
        if (!new[] { "v4", "v6", "both" }.Contains(IpFamily)) return "未知的地址族。";
        if (Proto is "wwarp" or "masque-mm" && IpFamily != "v4") return "嵌套协议仅支持 IPv4，请修改地址族。";
        return "";
    }
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static ClientSettings Load(string home)
    {
        var path = Path.Combine(home, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("设置文件为空。") : new();
    }
    public void Save(string home)
    {
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, true);
    }
    public static string Speed(double? bytes) => bytes is null or <= 0 ? "" : bytes >= 1048576 ? $"{bytes / 1048576:0.00} MB/s" : bytes >= 1024 ? $"{bytes / 1024:0.0} KB/s" : $"{bytes:0} B/s";
}
