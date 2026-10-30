using System.IO;
using System.Text.Json;

namespace Xehn.App;

public sealed record ResourceInfo(string CoreVersion = "", bool CoreNeedsDownload = false, string CoreCheck = "", string GeoUpdated = "", bool GeoNeedsDownload = false, bool GeoPending = false);
public sealed record ResourceProgress(string Resource, string Phase, string File, long Done, long Total);
public interface IResourceService : IDisposable
{
    Task<ResourceInfo> RunAsync(string command, Action<string> log, Action<ResourceProgress>? progress = null);
    Task CancelAsync();
}
public sealed class ResourceService(string home) : IResourceService
{
    private readonly EngineService worker = new(home);
    public async Task<ResourceInfo> RunAsync(string command, Action<string> log, Action<ResourceProgress>? progress = null)
    {
        ResourceInfo? result = null;
        await worker.RunAsync(command, line =>
        {
            if (line.StartsWith("@@RESOURCE_PROGRESS ", StringComparison.Ordinal))
            {
                var sample = JsonSerializer.Deserialize<ResourceProgress>(line[20..], ClientSettings.JsonOptions);
                if (sample != null) progress?.Invoke(sample);
            }
            else if (line.StartsWith("@@RESOURCES ", StringComparison.Ordinal))
                result = JsonSerializer.Deserialize<ResourceInfo>(line[12..], ClientSettings.JsonOptions);
            else log(line);
        });
        return result ?? throw new IOException("资源操作未返回检查结果。");
    }
    public Task CancelAsync() => worker.CancelAsync();
    public void Dispose() => worker.Dispose();
}
