using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

public sealed record AppRelease(Version Version, Uri DownloadUrl, long Size, string Sha256);

/// <summary>仅从本项目的正式 Release 获取单文件版本；所有失败均保留正在使用的程序。</summary>
public sealed class UpdateService : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/AimoStein/DeskBox";
    internal const string ManifestUrl = RepositoryUrl + "/releases/latest/download/update.json";
    private const long MaxDownloadSize = 512L * 1024 * 1024;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;

    public static Version CurrentVersion => NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version!);
    public AppRelease? AvailableRelease { get; private set; }
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "尚未检查更新。";
    public event Action? Changed;

    public UpdateService()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskBox/{CurrentVersion.ToString(3)}");
    }

    internal static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    internal static AppRelease? ParseUpdateManifest(string json, Version current)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        var release = document.RootElement;
        if (release.GetProperty("schema_version").GetInt32() != 1) throw new InvalidDataException("更新清单格式不受支持。");
        var versionText = release.GetProperty("version").GetString();
        if (!Version.TryParse(versionText, out var version)) throw new InvalidDataException("发布版本号无效。");
        var url = release.GetProperty("download_url").GetString();
        // 清单必须把版本和下载路径绑定，不能把某版本指向别的版本或仓库。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != $"/AimoStein/DeskBox/releases/download/v{versionText}/DeskBox.exe")
            throw new InvalidDataException("更新下载地址与本项目的发布版本不一致。");
        var size = release.GetProperty("size").GetInt64();
        var digest = release.GetProperty("sha256").GetString();
        if (size <= 0 || size > MaxDownloadSize) throw new InvalidDataException("更新包大小无效。");
        if (digest is null || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新清单中的 SHA-256 校验信息无效。");
        version = NormalizeVersion(version);
        return version > NormalizeVersion(current) ? new AppRelease(version, uri, size, digest) : null;
    }

    internal static async Task<AppRelease?> FetchReleaseAsync(HttpClient client, Version current, CancellationToken token)
    {
        using var response = await client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 兼容尚无清单的旧 Release：只读取 latest 的重定向目标，不抓网页内容，也不调用 API。
            using var latest = await client.GetAsync(RepositoryUrl + "/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (latest.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("还没有可用的正式发布版本。");
            EnsureAccessible(latest);
            var finalUrl = latest.RequestMessage?.RequestUri;
            const string prefix = "/AimoStein/DeskBox/releases/tag/";
            if (finalUrl is null || finalUrl.Scheme != "https" || finalUrl.Host != "github.com"
                || !finalUrl.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("无法识别最新正式版，请打开发布页查看。");
            var tag = Uri.UnescapeDataString(finalUrl.AbsolutePath[prefix.Length..]);
            if (tag.StartsWith('v')) tag = tag[1..];
            if (!Version.TryParse(tag, out var version)) throw new InvalidDataException("发布版本号无效。");
            if (NormalizeVersion(version) <= NormalizeVersion(current)) return null;
            throw new InvalidDataException($"发现新版本 {tag}，但该发布尚未提供 update.json，请从发布页手动下载。");
        }
        EnsureAccessible(response);
        // 即使服务器未给 Content-Length，也最多读取 64 KiB 的清单。
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[64 * 1024 + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (count == 0) return ParseUpdateManifest(Encoding.UTF8.GetString(buffer, 0, total), current);
            total += count;
        }
        throw new InvalidDataException("更新清单超出大小限制。");
    }

    private static void EnsureAccessible(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidDataException("GitHub 文件访问受限，请稍后重试，或打开发布页手动下载。");
        response.EnsureSuccessStatusCode();
    }

    public async Task CheckAsync()
    {
        if (IsBusy) return;
        BeginOperation(TimeSpan.FromSeconds(25));
        AvailableRelease = null;
        SetStatus("正在检查新版本…");
        try
        {
            AvailableRelease = await FetchReleaseAsync(_client, CurrentVersion, _operation!.Token);
            SetStatus(AvailableRelease is { } release
                ? $"发现新版本 {release.Version.ToString(3)}，点击更新后将下载并重启。"
                : $"未发现更新（当前版本 {CurrentVersion.ToString(3)}）。");
            Log.Info("更新检查：" + Status);
        }
        catch (Exception ex)
        {
            SetStatus(ex is OperationCanceledException ? "检查已取消或超时，可重新检查。" : "检查失败：" + ex.Message);
            Log.Warn("更新检查：" + ex.Message);
        }
        finally { EndOperation(); }
    }

    public async Task<bool> PrepareInstallAsync()
    {
        if (IsBusy || AvailableRelease is not { } release) return false;
        BeginOperation(TimeSpan.FromMinutes(10));
        string? staging = null;
        try
        {
            var target = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位程序。");
            // 普通 dotnet build 的 apphost 还依赖旁边的 DLL，不能仅替换 exe。
            if (File.Exists(Path.ChangeExtension(target, ".dll")) || Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("开发构建不支持在线替换，请使用发布的单文件绿色版。");
            staging = Path.Combine(Path.GetDirectoryName(target)!, ".deskbox-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging); // 提前确认程序目录可写，失败时不会退出应用。
            var download = Path.Combine(staging, "DeskBox.exe");
            var progress = new Progress<int>(percent => SetStatus($"正在下载更新… {percent}%（可取消）"));
            await DownloadAsync(_client, release, download, progress, _operation!.Token);
            SetStatus("校验通过，正在准备重启…");
            var script = Path.Combine(staging, "install.ps1");
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskBox.Services.InstallUpdate.ps1")!)
            using (var output = File.Create(script))
            {
                // Windows PowerShell 5.1 通过 BOM 识别中文脚本的 UTF-8 编码。
                await output.WriteAsync(Encoding.UTF8.GetPreamble(), _operation.Token);
                await resource.CopyToAsync(output, _operation.Token);
            }
            var ready = Path.Combine(staging, "ready");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Target", target, "-Source", download, "-Sha256", release.Sha256,
                "-ParentId", Environment.ProcessId.ToString(), "-Ready", ready }) start.ArgumentList.Add(arg);
            using var helper = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                while (!File.Exists(ready))
                {
                    if (helper.HasExited) throw new IOException("更新程序启动失败，请查看 deskbox-update.log。");
                    await Task.Delay(100, handshake.Token);
                }
            }
            catch
            {
                if (!helper.HasExited) helper.Kill();
                throw;
            }
            // 从此由助手接管暂存目录；调用方立即正常退出，助手只在进程退出后替换。
            staging = null;
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(ex is OperationCanceledException ? "更新已取消或超时，可重试。" : "更新失败：" + ex.Message);
            Log.Warn("更新下载：" + ex.Message);
            return false;
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, true); } catch { /* 保留现场，不影响程序运行。 */ }
            }
            EndOperation();
        }
    }

    internal static async Task DownloadAsync(HttpClient client, AppRelease release, string path,
        IProgress<int>? progress, CancellationToken token)
    {
        using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("下载大小与发布信息不一致。");
        var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var inputLifetime = input.ConfigureAwait(false);
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await using var outputLifetime = output.ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        var lastPercent = -1;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > release.Size || total > MaxDownloadSize) throw new InvalidDataException("更新包超出预期大小。");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            var percent = (int)(total * 100 / release.Size);
            if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
        }
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包校验失败，请重新下载。");
    }

    private void BeginOperation(TimeSpan timeout)
    {
        IsBusy = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation.CancelAfter(timeout);
    }

    private void EndOperation()
    {
        IsBusy = false;
        _operation?.Dispose();
        _operation = null;
        Changed?.Invoke();
    }

    private void SetStatus(string status) { Status = status; Changed?.Invoke(); }
    public void Cancel() => _operation?.Cancel();
    public void Dispose() { _lifetime.Cancel(); _client.Dispose(); _lifetime.Dispose(); }
}
