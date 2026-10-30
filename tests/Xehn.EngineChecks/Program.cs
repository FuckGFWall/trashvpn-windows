using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xehn.App;

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
Check(string.Join('\n', lines).Contains("fluent-0.1"), "hidden-process UTF-8 stdout / engine version");
var failed = false;
try { await engine.RunAsync("geo-check", lines.Enqueue).WaitAsync(TimeSpan.FromSeconds(15)); }
catch (IOException) { failed = true; }
Check(failed && lines.Any(l => l.Contains("GeoIP")), "nonzero exit and missing-geodata error reach C#");
using var ready = new EngineService(preparedHome, binary);
var readyLines = new ConcurrentQueue<string>();
await ready.RunAsync("geo-check", readyLines.Enqueue).WaitAsync(TimeSpan.FromSeconds(15));
Check(readyLines.Any(l => l.Contains("初检通过")), "bundled GeoIP / GeoSite read from isolated data directory");
Check(!(await engine.ReadSessionAsync()).Connected, "missing state is disconnected");
await File.WriteAllTextAsync(Path.Combine(testHome, "state.json"), "{\"connected\":true,\"core_pid\":0}");
Check(!(await engine.ReadSessionAsync()).Connected, "stale state cannot fake a live core");
var modeRejected = false;
try { await engine.RunAsync("desktop-modes", lines.Enqueue, System.Text.Json.JsonSerializer.Serialize(new ClientSettings(), ClientSettings.JsonOptions)).WaitAsync(TimeSpan.FromSeconds(15)); }
catch (IOException) { modeRejected = true; }
Check(modeRejected && lines.Any(l => l.Contains("没有可热切换的本地连接")) && !File.Exists(Path.Combine(testHome, "core", "config.yaml")), "hot reload rejects stale session before API / proxy mutation");

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
Console.WriteLine($"{passed} engine bridge checks passed. No connect/disconnect, proxy or TUN commands executed.");
