using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xehn.App;

try
{
if (args.Length != 2) throw new ArgumentException("Usage: Xehn.EngineChecks <engine.exe> <prepared data directory>");
var binary = Path.GetFullPath(args[0]);
var preparedHome = Path.GetFullPath(args[1]);
var testHome = Path.Combine(Path.GetDirectoryName(preparedHome)!, "engine-checks", Guid.NewGuid().ToString("N"));
var passed = 0;
void Check(bool result, string label)
{
    if (!result) throw new Exception("FAIL: " + label);
    passed++; Console.WriteLine("PASS: " + label);
}
using var engine = new EngineService(testHome, binary);
var lines = new ConcurrentQueue<string>();
await engine.RunAsync("version", lines.Enqueue).WaitAsync(TimeSpan.FromSeconds(15));
Check(string.Join('\n', lines).Contains("1.0.0"), "hidden-process UTF-8 stdout / engine version");
var failed = false;
try { await engine.RunAsync("geo-check", lines.Enqueue).WaitAsync(TimeSpan.FromSeconds(15)); }
catch (IOException) { failed = true; }
Check(failed && lines.Any(l => l.Contains("GeoIP")), "nonzero exit and missing-geodata error reach C#");
using var ready = new EngineService(preparedHome, binary);
var readyLines = new ConcurrentQueue<string>();
await ready.RunAsync("geo-check", readyLines.Enqueue).WaitAsync(TimeSpan.FromSeconds(15));
Check(readyLines.Any(l => l.Contains("初检通过")), "bundled GeoIP / GeoSite read from isolated data directory");
Check(!(await engine.ReadSessionAsync()).Connected, "missing state is disconnected");
await File.WriteAllTextAsync(Path.Combine(testHome, "state.json"), "{\"connected\":true,\"core_pid\":0,\"mixed_port\":17777,\"api_port\":19090}");
var stale = await engine.ReadSessionAsync();
Check(!stale.Connected && stale.MixedPort == 17777 && stale.ApiPort == 19090, "session reports distinct mixed and API ports; stale state cannot fake a live core");
var modeRejected = false;
try { await engine.RunAsync("desktop-modes", lines.Enqueue, System.Text.Json.JsonSerializer.Serialize(new ClientSettings(), ClientSettings.JsonOptions)).WaitAsync(TimeSpan.FromSeconds(15)); }
catch (IOException) { modeRejected = true; }
Check(modeRejected && lines.Any(l => l.Contains("没有可热切换的本地连接")) && !File.Exists(Path.Combine(testHome, "core", "config.yaml")), "hot reload rejects stale session before API / proxy mutation");
string resourceJson = "";
await engine.RunAsync("resources-info", line => { if (line.StartsWith("@@RESOURCES ")) resourceJson = line[12..]; }).WaitAsync(TimeSpan.FromSeconds(15));
var missingResources = System.Text.Json.JsonSerializer.Deserialize<ResourceInfo>(resourceJson, ClientSettings.JsonOptions)!;
Check(missingResources.CoreNeedsDownload && missingResources.GeoNeedsDownload, "resource bridge reports missing core / GeoFiles without downloads");
resourceJson = "";
await ready.RunAsync("resources-check-core", line => { if (line.StartsWith("@@RESOURCES ")) resourceJson = line[12..]; }).WaitAsync(TimeSpan.FromSeconds(120));
var checkedCore = System.Text.Json.JsonSerializer.Deserialize<ResourceInfo>(resourceJson, ClientSettings.JsonOptions)!;
Check(!checkedCore.CoreNeedsDownload && checkedCore.CoreVersion.Length > 0 && checkedCore.CoreCheck.Contains("全部协议"), "real core version and every protocol checked with parser only");
var proxyUpdateRejected = false;
try { await engine.RunAsync("resources-download-geo-proxy", lines.Enqueue).WaitAsync(TimeSpan.FromSeconds(15)); }
catch (IOException) { proxyUpdateRejected = true; }
Check(proxyUpdateRejected && !Directory.Exists(Path.Combine(testHome, "core", ".geofiles-staging")), "automatic GeoFiles refuses stale VPN without direct-network fallback");

// A loopback fixture, not a proxy or tunnel; verifies the mihomo traffic stream contract.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serve = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync() is { Length: > 0 }) { }
        var body = "{\"up\":1024,\"down\":2048}\n";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    });
    var sample = await engine.TrafficAsync(port);
    await serve.WaitAsync(TimeSpan.FromSeconds(5));
    Check(sample.Up == 1024 && sample.Down == 2048, "mihomo traffic JSON stream parsed correctly");
}
finally { listener.Stop(); }
Check(!File.Exists(Path.Combine(testHome, "warpscout-account.json")), "safe checks never register a WARP account");
// A synthetic account/cache exercises the real read-only export bridge. No
// user account or system clipboard is read, copied, or printed by these tests.
var exportHome = Path.Combine(testHome, "export-fixture"); Directory.CreateDirectory(exportHome);
var identity = new { id = "fixture-id-not-for-export", token = "fixture-token-not-for-export", private_key = Convert.ToBase64String(new byte[32]), peer_public_key = "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=", ipv4 = "172.16.0.2", ipv6 = "2606:4700:110::1234" };
await File.WriteAllTextAsync(Path.Combine(exportHome, "warpscout-account.json"), System.Text.Json.JsonSerializer.Serialize(identity));
var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { proto = "wg", ipv6 = false, at = DateTimeOffset.UtcNow, results = new[] {
    new { endpoint = "192.0.2.1:2408", working = true }, new { endpoint = "192.0.2.2:443", working = true },
    new { endpoint = "192.0.2.3:8443", working = true }, new { endpoint = "192.0.2.1:443", working = true },
    new { endpoint = "192.0.2.5:443", working = false }, new { endpoint = "192.0.2.1:443", working = true },
    new { endpoint = "bad-address", working = true } } });
await File.WriteAllTextAsync(Path.Combine(exportHome, "cache.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["scan-wg-v4"] = new { payload, stored_at = DateTimeOffset.UtcNow, expires_at = DateTimeOffset.UtcNow.AddHours(1) } }));
var beforeExport = Directory.GetFiles(exportHome).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
var exporter = new NodeExportService(exportHome, binary);
var exportRequest = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new ClientSettings { Proto = "wg" }, ClientSettings.JsonOptions))!;
exportRequest["pin"] = "192.0.2.1:2408";
var exportSettings = exportRequest.ToJsonString();
foreach (var format in new[] { "clash", "xray", "singbox" })
{
    var config = await exporter.ExportAsync(exportSettings, format).WaitAsync(TimeSpan.FromSeconds(10));
    Check(config.Contains("192.0.2.1") && config.Contains(identity.private_key) && !config.Contains(identity.token) && !config.Contains(identity.id), format + " export crosses hidden-process bridge with node keys but no API credentials");
    if (format != "clash")
    {
        using var parsed = System.Text.Json.JsonDocument.Parse(config);
        Check(parsed.RootElement.TryGetProperty(format == "xray" ? "outbounds" : "endpoints", out var nodes), format + " exports modern node section");
        Check(nodes.GetArrayLength() == 4 && nodes.EnumerateArray().Select(node => node.GetProperty("tag").GetString()).Distinct().Count() == 4, format + " exports all four cached nodes with unique tags despite pin, duplicate and failed entries");
    }
    else Check(config.Split('\n').Count(line => line.TrimStart().StartsWith("- name: ")) == 4, "Clash exports all four cached nodes despite pin, duplicate and failed entries");
    Check(config.Contains("192.0.2.2:443") && config.Contains("192.0.2.3:8443") && config.Contains("192.0.2.1:443") && !config.Contains("192.0.2.5") && !config.Contains("bad-address"), format + " full-node names identify every additional usable endpoint");
}
Check(Directory.GetFiles(exportHome).Length == beforeExport.Count && beforeExport.All(file => File.ReadAllText(Path.Combine(exportHome, file.Key!)) == file.Value), "export bridge does not write logs, settings, core, account, cache or connection state");
var unsupportedExport = false;
try { await exporter.ExportAsync(System.Text.Json.JsonSerializer.Serialize(new ClientSettings { Proto = "awg" }, ClientSettings.JsonOptions), "xray"); }
catch (IOException e) { unsupportedExport = e.Message.Contains("WireGuard"); }
Check(unsupportedExport, "unsupported export reports actionable error without protocol downgrade");
// A fully warm synthetic fixture exercises progress serialization only:
// valid account + fresh matching cache means no registration, scan or core.
var warmHome = Path.Combine(testHome, "cached-preparation"); Directory.CreateDirectory(warmHome);
await File.WriteAllTextAsync(Path.Combine(warmHome, "warpscout-account.json"), System.Text.Json.JsonSerializer.Serialize(identity));
async Task SeedWarmCache(int ageDays)
{
    var at=DateTimeOffset.UtcNow.AddDays(-ageDays);
    var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { proto = "wg", intensity = "medium", ipv6 = false, at, results = Enumerable.Range(1, 4).Select(i => new { endpoint = $"192.0.2.{i}:2408", working = true }) });
    await File.WriteAllTextAsync(Path.Combine(warmHome, "cache.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["scan-wg-v4"] = new { payload, stored_at = at, expires_at = at.AddHours(6) } }));
}
await SeedWarmCache(0);
// A removed legacy preference cannot force a network sweep when absent from
// the new desktop request. Today's scan is valid under the one-day default.
await File.WriteAllTextAsync(Path.Combine(warmHome,"settings.json"),"{\"rescan\":true}");
var warmBefore = Directory.GetFiles(warmHome).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
using var warm = new EngineService(warmHome, binary);
var preparationEvents = new List<System.Text.Json.JsonElement>();
await warm.RunAsync("desktop-prepare", line => {
    if (!line.StartsWith("@@XEHN ")) return;
    using var doc = System.Text.Json.JsonDocument.Parse(line[7..]);
    if (doc.RootElement.GetProperty("type").GetString() == "progress") preparationEvents.Add(doc.RootElement.Clone());
}, System.Text.Json.JsonSerializer.Serialize(new ClientSettings { Proto = "wg" }, ClientSettings.JsonOptions)).WaitAsync(TimeSpan.FromSeconds(10));
Check(preparationEvents.Any(e => e.GetProperty("phase").GetString() == "register" && e.GetProperty("step").GetInt32() == 1 && e.GetProperty("steps").GetInt32() == 6), "real bridge reports protocol-specific major steps for cached account");
Check(preparationEvents.Any(e => e.TryGetProperty("stage", out var stage) && stage.GetString() == "cached" && e.GetProperty("detail").GetString()!.Contains("4 个可用节点")) && preparationEvents[^1].GetProperty("done").GetDouble() == 100, "real warm-cache bridge reports actual node count and final full progress");
Check(Directory.GetFiles(warmHome).Length == warmBefore.Count && warmBefore.All(file => File.ReadAllText(Path.Combine(warmHome, file.Key!)) == file.Value) && !Directory.Exists(Path.Combine(warmHome, "core")), "warm preparation fixture never modifies account/cache, starts a core or writes connection state");
var warmExporter = new NodeExportService(warmHome,binary);
await SeedWarmCache(2);
var expiredExportRejected=false;
try { await warmExporter.ExportAsync(System.Text.Json.JsonSerializer.Serialize(new ClientSettings{Proto="wg"},ClientSettings.JsonOptions),"clash"); }
catch(IOException e) { expiredExportRejected=e.Message.Contains("缓存"); }
Check(expiredExportRejected,"export bridge uses the default one-day expiry without scanning");
await SeedWarmCache(100);
var permanent=await warmExporter.ExportAsync(System.Text.Json.JsonSerializer.Serialize(new ClientSettings{Proto="wg",CacheNeverExpires=true},ClientSettings.JsonOptions),"clash");
Check(permanent.Split('\n').Count(line=>line.TrimStart().StartsWith("- name: "))==4,"never-expire setting exports every usable node from an old scan without registration or refresh");
Console.WriteLine($"{passed} engine bridge checks passed. No connect/disconnect, proxy or TUN commands executed.");
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
