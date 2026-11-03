using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

internal static class UpdateSelfTest
{
    public static void Run(Action<string, bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DeskBox-UpdateTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("test update payload");
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            var url = "https://github.com/AimoStein/DeskBox/releases/download/v1.5.0/DeskBox.exe";
            string Manifest(string version = "1.5.0", string? download = null,
                string? sha = null, long? size = null, int schema = 1) => JsonSerializer.Serialize(new
            {
                schema_version = schema, version, download_url = download ?? url,
                size = size ?? bytes.Length, sha256 = sha ?? digest,
            });
            void Test(string name, bool pass) => check("更新-" + name, pass, "");
            void Reject(string name, Action action)
            {
                try { action(); Test(name, false); }
                catch (InvalidDataException) { Test(name, true); }
            }

            var current = new Version(1, 4, 8, 0);
            var release = UpdateService.ParseUpdateManifest(Manifest(), current)!;
            Test("识别正式新版本及单文件资产", release.Version == new Version(1, 5, 0, 0) && release.Sha256 == digest);
            Test("同版本不提示（3段/4段版本号）", UpdateService.ParseUpdateManifest(Manifest(), new Version(1, 5, 0)) is null);
            Test("不降级", UpdateService.ParseUpdateManifest(Manifest(), new Version(1, 6, 0)) is null);
            Reject("拒绝预发布版本", () => UpdateService.ParseUpdateManifest(Manifest(version: "1.5.0-beta"), current));
            Reject("拒绝不支持的清单格式", () => UpdateService.ParseUpdateManifest(Manifest(schema: 2), current));
            Reject("拒绝外部下载地址", () => UpdateService.ParseUpdateManifest(Manifest(download: "https://example.com/DeskBox.exe"), current));
            Reject("拒绝非HTTPS地址", () => UpdateService.ParseUpdateManifest(Manifest(download: url.Replace("https:", "http:")), current));
            Reject("拒绝无校验值资产", () => UpdateService.ParseUpdateManifest(Manifest(sha: ""), current));
            Reject("拒绝畸形校验值", () => UpdateService.ParseUpdateManifest(Manifest(sha: new string('z', 64)), current));
            Reject("版本与下载地址必须一致", () => UpdateService.ParseUpdateManifest(Manifest(version: "1.6.0"), current));
            Reject("拒绝不合理的大小", () => UpdateService.ParseUpdateManifest(Manifest(size: 0), current));

            using var feed = new HttpClient(new PayloadHandler(Encoding.UTF8.GetBytes(Manifest())));
            var fetched = UpdateService.FetchReleaseAsync(feed, current, CancellationToken.None).GetAwaiter().GetResult();
            Test("直接读取更新清单（不使用API）", fetched == release);
            using var legacy = new HttpClient(new LegacyReleaseHandler("v1.4.4"));
            Test("缺少清单的旧版通过重定向比较版本", UpdateService.FetchReleaseAsync(legacy, current,
                CancellationToken.None).GetAwaiter().GetResult() is null);
            using var missing = new HttpClient(new LegacyReleaseHandler("v1.6.0"));
            Reject("较新的旧式发布无清单时明确报错", () => UpdateService.FetchReleaseAsync(missing, current,
                CancellationToken.None).GetAwaiter().GetResult());
            using var oversizedFeed = new HttpClient(new PayloadHandler(new byte[65537], omitLength: true));
            Reject("清单无ContentLength时仍限制大小", () => UpdateService.FetchReleaseAsync(oversizedFeed, current,
                CancellationToken.None).GetAwaiter().GetResult());

            using var client = new HttpClient(new PayloadHandler(bytes));
            var goodPath = Path.Combine(directory, "good.exe");
            UpdateService.DownloadAsync(client, release, goodPath, null, CancellationToken.None).GetAwaiter().GetResult();
            Test("下载并校验成功", File.ReadAllBytes(goodPath).SequenceEqual(bytes));
            Reject("校验失败拒绝安装", () => UpdateService.DownloadAsync(client, release with { Sha256 = new string('0', 64) },
                Path.Combine(directory, "corrupt.exe"), null, CancellationToken.None).GetAwaiter().GetResult());
            using var truncated = new HttpClient(new PayloadHandler(bytes[..^1], omitLength: true));
            Reject("检测不带ContentLength的截断下载", () => UpdateService.DownloadAsync(truncated, release,
                Path.Combine(directory, "truncated.exe"), null, CancellationToken.None).GetAwaiter().GetResult());
            using var extra = new HttpClient(new PayloadHandler(bytes.Concat(new byte[] { 0 }).ToArray(), omitLength: true));
            Reject("检测超出声明大小的下载", () => UpdateService.DownloadAsync(extra, release,
                Path.Combine(directory, "oversize.exe"), null, CancellationToken.None).GetAwaiter().GetResult());
            using var unavailable = new HttpClient(new PayloadHandler(bytes, status: HttpStatusCode.ServiceUnavailable));
            try
            {
                UpdateService.DownloadAsync(unavailable, release, Path.Combine(directory, "offline.exe"), null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Test("网络失败不生成更新文件", false);
            }
            catch (HttpRequestException) { Test("网络失败不生成更新文件", !File.Exists(Path.Combine(directory, "offline.exe"))); }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                UpdateService.DownloadAsync(client, release, Path.Combine(directory, "cancel.exe"), null,
                    cancelled.Token).GetAwaiter().GetResult();
                Test("取消下载", false);
            }
            catch (OperationCanceledException) { Test("取消下载", true); }
            Test("保留原始配置的更新默认值", JsonSerializer.Deserialize<Models.AppConfig>("{\"AutoStart\":true}") is
                { AutoStart: true, AutoCheckForUpdates: true });
        }
        catch (Exception ex) { check("更新-自检异常", false, ex.ToString()); }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class PayloadHandler(byte[] payload, bool omitLength = false,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host == "api.github.com") throw new InvalidOperationException("不应调用 GitHub API。");
            // 不可 seek 的流避免 HttpContent 自动推算大小，以覆盖分块传输场景。
            HttpContent content = omitLength ? new StreamContent(new NonSeekStream(payload)) : new ByteArrayContent(payload);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class LegacyReleaseHandler(string tag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri?.AbsoluteUri == UpdateService.ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.RequestUri?.AbsoluteUri != UpdateService.RepositoryUrl + "/releases/latest")
                throw new InvalidOperationException("兼容旧版不应访问 API 或其他地址。");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, UpdateService.RepositoryUrl + "/releases/tag/" + tag),
            });
        }
    }

    private sealed class NonSeekStream(byte[] payload) : MemoryStream(payload)
    {
        public override bool CanSeek => false;
    }
}
