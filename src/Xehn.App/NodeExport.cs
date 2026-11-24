using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Xehn.App;

public interface INodeExportService
{
    Task<string> ExportAsync(string settingsJson, string format);
}

public sealed class NodeExportService(string home, string? binaryPath = null) : INodeExportService
{
    public async Task<string> ExportAsync(string settingsJson, string format)
    {
        var request = JsonNode.Parse(settingsJson) ?? throw new IOException("无法读取导出设置。");
        request["export_format"] = format;
        // Separate read-only process: export does not cancel or wait for the
        // connection/preparation worker. Never send its credential output to Log.
        using var worker = new EngineService(home, binaryPath);
        string? config = null, error = null;
        await worker.RunAsync("desktop-export", line =>
        {
            if (!line.StartsWith("@@NODE_EXPORT ", StringComparison.Ordinal)) return;
            using var result = JsonDocument.Parse(line[14..]);
            if (result.RootElement.TryGetProperty("error", out var failure)) error = failure.GetString();
            if (result.RootElement.TryGetProperty("config", out var value)) config = value.GetString();
        }, request.ToJsonString());
        if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
        return !string.IsNullOrWhiteSpace(config) ? config : throw new IOException("未收到节点配置，请等待优选完成后再试。");
    }
}

public interface ITextClipboard { void SetText(string text); }
public sealed class TextClipboard : ITextClipboard { public void SetText(string text) => Clipboard.SetText(text); }
public sealed record NodeExportFormat(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class NodeExportViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly INodeExportService service;
    private readonly ITextClipboard clipboard;
    private readonly Func<string> settingsJson, protocol;
    private readonly Func<bool> settingsReadable;
    private readonly Action<string> reportError;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource feedback = new();
    private int revision;
    private bool disposed;
    private string format = "clash";
    private static readonly NodeExportFormat[] AllFormats = [new("clash", "Clash（Mihomo）"), new("xray", "v2rayN / Xray"), new("singbox", "sing-box")];
    private static readonly NodeExportFormat[] ClashFormat = [AllFormats[0]];
    public IReadOnlyList<NodeExportFormat> Formats => protocol() switch
    {
        "wg" => AllFormats,
        "awg" or "masque" or "masque-h2" or "wwarp" or "masque-mm" => ClashFormat,
        _ => [],
    };
    public string Format
    {
        get => format;
        set { if (value != format && Formats.Any(item => item.Id == value)) { format = value; ResetFeedback(); } }
    }
    public bool IsCopying { get; private set; }
    public bool Copied { get; private set; }
    public bool CanChooseFormat => !disposed && !IsCopying && settingsReadable() && Formats.Count > 0;
    public bool CanCopy => CanChooseFormat && Formats.Any(item => item.Id == format);
    public string ButtonLabel => Copied ? "已复制" : IsCopying ? "正在导出" : "复制节点配置";
    public string Hint => Formats.Count == 0
        ? "当前协议暂无可导出的客户端格式。"
        : "导出所选协议的全部有效缓存节点，每个节点使用不同名称；固定节点不限制导出范围，不会重新扫描。配置含私钥，请勿公开分享。JSON 为节点配置片段，需合并到目标客户端配置中。";

    public NodeExportViewModel(INodeExportService service, Func<string> settingsJson, Func<string> protocol,
        Func<bool> settingsReadable, Action<string> reportError, ITextClipboard? clipboard = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.service = service; this.settingsJson = settingsJson; this.protocol = protocol;
        this.settingsReadable = settingsReadable; this.reportError = reportError;
        this.clipboard = clipboard ?? new TextClipboard(); this.delay = delay ?? Task.Delay;
        format = Formats.FirstOrDefault()?.Id ?? "";
    }

    public void RefreshFormats()
    {
        if (disposed) return;
        if (!Formats.Any(item => item.Id == format)) format = Formats.FirstOrDefault()?.Id ?? "";
        ResetFeedback();
    }

    public void ResetFeedback()
    {
        if (disposed) return;
        ++revision; feedback.Cancel(); feedback.Dispose(); feedback = new();
        Copied = false; Changed();
    }

    public async Task CopyAsync()
    {
        if (!CanCopy) return;
        ResetFeedback(); var current = revision; var token = feedback.Token;
        IsCopying = true; Changed();
        try
        {
            var snapshot = settingsJson();
            var text = await service.ExportAsync(snapshot, format);
            if (disposed || current != revision || settingsJson() != snapshot) return;
            if (string.IsNullOrWhiteSpace(text)) throw new IOException("导出结果为空，未修改剪贴板。");
            clipboard.SetText(text);
            Copied = true;
        }
        catch (Exception e)
        {
            if (!disposed && current == revision) reportError("节点配置复制失败：" + e.Message);
        }
        finally { IsCopying = false; if (!disposed) Changed(); }
        if (!Copied || disposed || current != revision) return;
        try { await delay(TimeSpan.FromSeconds(2), token); }
        catch (OperationCanceledException) { return; }
        if (!disposed && current == revision) { Copied = false; Changed(); }
    }

    public void Dispose() { if (disposed) return; disposed = true; ++revision; feedback.Cancel(); feedback.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(""));
}
