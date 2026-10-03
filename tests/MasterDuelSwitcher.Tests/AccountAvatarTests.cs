using System.Net;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用隔离临时目录和自有 HTTP 传输验证头像缓存，不修改真实账号。</summary>
public sealed class AccountAvatarTests
{
    /// <summary>Steam 本地头像复制到系统缓存，后续读取复用缓存且不访问网络。</summary>
    [Fact]
    public async Task LocalPngCopiesAtomicallyAndCacheSurvivesMissingSteamFile()
    {
        var fixture = new Fixture();
        try
        {
            var avatar = Path.Combine(fixture.Steam, "config", "avatarcache", Fixture.SteamId + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(avatar)!);
            await File.WriteAllBytesAsync(avatar, Fixture.Png);
            var service = new AccountAvatarService(fixture.State, fixture.Client);
            var cached = await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId);
            Assert.NotNull(cached);
            Assert.StartsWith(Path.Combine(fixture.State, "avatars") + Path.DirectorySeparatorChar, cached, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Fixture.Png, await File.ReadAllBytesAsync(cached));
            File.Delete(avatar);
            Assert.Equal(cached, await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
            Assert.Empty(fixture.Handler.Requests);
            Assert.Empty(Directory.GetFiles(Path.Combine(fixture.State, "avatars"), "*.tmp"));
        }
        finally { fixture.Dispose(); }
    }

    /// <summary>已有有效系统缓存优先于 Steam 文件，默认客户端路径不发生网络访问。</summary>
    [Fact]
    public async Task ExistingCacheWorksWithDefaultClientAndMissingSteamPath()
    {
        using var fixture = new Fixture();
        var cached = await fixture.WriteCacheAsync(".png", Fixture.Png);
        var service = new AccountAvatarService(fixture.State);
        Assert.Equal(cached, await service.GetAvatarPathAsync("", Fixture.SteamId));
    }

    /// <summary>无效 PNG 后继续读取本地 JPEG，并按照真实图片头保存扩展名。</summary>
    [Fact]
    public async Task LocalJpegIsUsedAfterInvalidPng()
    {
        using var fixture = new Fixture();
        await fixture.WriteSteamAsync(".png", [1, 2, 3]);
        await fixture.WriteSteamAsync(".jpg", Fixture.Jpeg);
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        var path = await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId);
        Assert.NotNull(path);
        Assert.EndsWith(".jpg", path, StringComparison.Ordinal);
        Assert.Equal(Fixture.Jpeg, await File.ReadAllBytesAsync(path));
        Assert.Empty(fixture.Handler.Requests);
    }

    /// <summary>公开社区 XML 只请求官方文档的资料地址，并下载白名单头像。</summary>
    [Theory]
    [InlineData("https://avatars.steamstatic.com/fixture_full.jpg")]
    [InlineData("https://avatars.fastly.steamstatic.com/fixture_full.jpg")]
    [InlineData("https://avatars.akamai.steamstatic.com/fixture_full.jpg")]
    [InlineData("https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/aa/fixture_full.jpg")]
    [InlineData("https://cdn.akamai.steamstatic.com/steamcommunity/public/images/avatars/aa/fixture_full.jpg")]
    [InlineData("https://media.steampowered.com/steamcommunity/public/images/avatars/aa/fixture_full.jpg")]
    public async Task PublicProfileDownloadsOnlyAllowedAvatarHosts(string avatarUrl)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (request, _) => Task.FromResult(Fixture.Response(request.RequestUri!.Host == "steamcommunity.com" ? Fixture.Profile(avatarUrl) : Fixture.Png));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        var path = await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId);
        Assert.NotNull(path);
        Assert.Equal(Fixture.Png, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { "https://steamcommunity.com/profiles/" + Fixture.SteamId + "/?xml=1", avatarUrl }, fixture.Handler.Requests.Select(uri => uri.AbsoluteUri));
    }

    /// <summary>非法标识在任何文件或网络动作前返回空结果。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7656119800000000")]
    [InlineData("765611980000000011")]
    [InlineData("7656119800000000A")]
    [InlineData("７６５６１１９８００００００００１")]
    [InlineData("../76561198000000")]
    public async Task InvalidIdHasNoSideEffects(string? steamId)
    {
        using var fixture = new Fixture();
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, steamId!));
        Assert.Empty(fixture.Handler.Requests);
        Assert.False(Directory.Exists(fixture.State));
    }

    /// <summary>调用前取消保持缓存与传输均未触碰。</summary>
    [Fact]
    public async Task PreCanceledCallDoesNotTouchFilesOrNetwork()
    {
        using var fixture = new Fixture();
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId, new CancellationToken(true)));
        Assert.Empty(fixture.Handler.Requests);
        Assert.False(Directory.Exists(fixture.State));
    }

    /// <summary>缺失头像、无效根、破损 XML 与 DTD 均保持首字头像，不请求外部实体。</summary>
    [Theory]
    [InlineData("<profile />")]
    [InlineData("<other><avatarFull>https://avatars.steamstatic.com/x.jpg</avatarFull></other>")]
    [InlineData("<profile>")]
    [InlineData("<!DOCTYPE profile [<!ENTITY avatar SYSTEM 'https://outside.example/private'>]><profile><avatarFull>&avatar;</avatarFull></profile>")]
    [InlineData("<profile><avatarFull>invalid-uri</avatarFull></profile>")]
    public async Task InvalidProfileDoesNotIssueAvatarRequests(string xml)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Fixture.Response(Encoding.UTF8.GetBytes(xml)));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Single(fixture.Handler.Requests);
        Assert.False(Directory.Exists(fixture.State));
    }

    /// <summary>非 HTTPS、凭据、特殊端口及伪造域名都在头像下载前拒绝。</summary>
    [Theory]
    [InlineData("http://avatars.steamstatic.com/fixture.jpg")]
    [InlineData("https://avatars.steamstatic.com:444/fixture.jpg")]
    [InlineData("https://user:password@avatars.steamstatic.com/fixture.jpg")]
    [InlineData("https://avatars.steamstatic.com.outside.example/fixture.jpg")]
    [InlineData("https://outside.example/fixture.jpg")]
    [InlineData("https://steamcdn-a.akamaihd.net/other/fixture.jpg")]
    public async Task InvalidAvatarOriginIsNotDownloaded(string avatarUrl)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Fixture.Response(Fixture.Profile(avatarUrl)));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Single(fixture.Handler.Requests);
    }

    /// <summary>资料与头像 HTTP 非成功状态返回空结果，不保存响应正文。</summary>
    [Theory]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.Redirect)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task UnsuccessfulHttpStatusDoesNotCreateCache(bool avatarStage, HttpStatusCode status)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (request, _) => Task.FromResult(avatarStage && request.RequestUri!.Host == "steamcommunity.com"
            ? Fixture.Response(Fixture.Profile(Fixture.AvatarUrl)) : new HttpResponseMessage(status));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync("", Fixture.SteamId));
        Assert.Equal(avatarStage ? 2 : 1, fixture.Handler.Requests.Count);
        Assert.False(Directory.Exists(fixture.State));
    }

    /// <summary>有长度头和无长度头的超限响应都在有限读取后返回空结果。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedHttpBodyIsBounded(bool avatarStage, bool unknownLength)
    {
        using var fixture = new Fixture();
        var oversized = new byte[(avatarStage ? 2 * 1024 * 1024 : 256 * 1024) + 1];
        fixture.Handler.Reply = (request, _) => Task.FromResult(avatarStage && request.RequestUri!.Host == "steamcommunity.com"
            ? Fixture.Response(Fixture.Profile(Fixture.AvatarUrl)) : Fixture.Response(oversized, unknownLength));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.False(Directory.Exists(fixture.State));
    }

    /// <summary>损坏或超限的系统缓存允许后续下载重试并原子替换。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCacheIsReplacedByNextValidDownload(bool oversized)
    {
        using var fixture = new Fixture();
        var original = await fixture.WriteCacheAsync(".png", oversized ? new byte[2 * 1024 * 1024 + 1] : [1, 2, 3]);
        fixture.Handler.SetValidDownload();
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        var path = await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId);
        Assert.Equal(original, path);
        Assert.Equal(Fixture.Png, await File.ReadAllBytesAsync(path!));
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.State, "avatars"), "*.tmp"));
    }

    /// <summary>头像响应的长度、PNG 起止标识和 JPEG 起止标识均必须有效。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InvalidImageHeadersAndTrailersAreNotCached(int corruption)
    {
        using var fixture = new Fixture();
        var image = corruption switch { 0 => Array.Empty<byte>(), 1 or 2 => Fixture.Png.ToArray(), _ => Fixture.Jpeg.ToArray() };
        if (corruption == 1) image[0] = 0;
        if (corruption == 2) image[^1] = 0;
        if (corruption == 3) image[0] = 0;
        if (corruption == 4) image[^1] = 0;
        fixture.Handler.Reply = (request, _) => Task.FromResult(Fixture.Response(request.RequestUri!.Host == "steamcommunity.com" ? Fixture.Profile(Fixture.AvatarUrl) : image));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.False(Directory.Exists(fixture.State));
    }

    /// <summary>网络异常不负缓存失败，下一次调用能够重新取得头像。</summary>
    [Fact]
    public async Task NetworkFailureCanRetryWithoutNegativeCache()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture-network-error"));
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        fixture.Handler.SetValidDownload();
        Assert.NotNull(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Equal(3, fixture.Handler.Requests.Count);
    }

    /// <summary>传输进行中取消返回空结果并释放并发槽，后续调用可正常完成。</summary>
    [Fact]
    public async Task CancellationDuringDownloadReleasesGateAndAllowsRetry()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = async (_, token) => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); };
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        var pending = service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        Assert.Null(await pending);
        fixture.Handler.SetValidDownload();
        Assert.NotNull(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
    }

    /// <summary>实际 HTTP 同时只进入四项，取消等待槽的调用不会错误释放额外槽。</summary>
    [Fact]
    public async Task ParallelDownloadsAreLimitedAndQueuedCancellationDoesNotReleaseSlot()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var fourEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        fixture.Handler.Reply = async (_, token) =>
        {
            if (Interlocked.Increment(ref entered) == 4) fourEntered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        var downloads = Enumerable.Range(1, 6).Select(index => service.GetAvatarPathAsync(fixture.Steam, "7656119800000000" + index)).ToArray();
        try
        {
            await fourEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(4, fixture.Handler.Requests.Count);
            var queued = service.GetAvatarPathAsync(fixture.Steam, "76561198000000007", cancellation.Token);
            cancellation.Cancel();
            Assert.Null(await queued);
            Assert.Equal(4, fixture.Handler.Requests.Count);
        }
        finally { release.TrySetResult(true); await Task.WhenAll(downloads); }
        Assert.Equal(6, fixture.Handler.Requests.Count);
        Assert.All(downloads, task => Assert.Null(task.Result));
    }

    /// <summary>日志只给出固定短状态，不包含异常正文、账号标识或请求地址。</summary>
    [Fact]
    public async Task FailureLoggingDoesNotContainProfileOrExceptionData()
    {
        using var fixture = new Fixture();
        var logger = new CaptureLogger();
        fixture.Handler.Reply = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("TOKEN " + Fixture.SteamId));
        var service = new AccountAvatarService(fixture.State, fixture.Client, logger);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        var record = Assert.Single(logger.Records);
        Assert.DoesNotContain(Fixture.SteamId, record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("https", record.Message, StringComparison.Ordinal);
        Assert.Null(record.Exception);
    }

    /// <summary>已有 JPEG 缓存按真实文件头复用，超限本地 PNG 不阻挡有效 JPEG。</summary>
    [Fact]
    public async Task CachedJpegIsReusedAndOversizedLocalPngFallsThrough()
    {
        using var fixture = new Fixture();
        await fixture.WriteSteamAsync(".png", new byte[2 * 1024 * 1024 + 1]);
        await fixture.WriteSteamAsync(".jpg", Fixture.Jpeg);
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        var cached = await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId);
        Assert.NotNull(cached);
        File.Delete(Path.Combine(fixture.Steam, "config", "avatarcache", Fixture.SteamId + ".jpg"));
        Assert.Equal(cached, await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Empty(fixture.Handler.Requests);
    }

    /// <summary>头像缓存文件位置被目录占用时保全目录，清除自己的原子写临时文件。</summary>
    [Fact]
    public async Task AtomicMoveFailurePreservesOccupiedPathAndCleansTemporaryFile()
    {
        using var fixture = new Fixture();
        await fixture.WriteSteamAsync(".png", Fixture.Png);
        var occupied = Path.Combine(fixture.State, "avatars", Fixture.SteamId + ".png");
        Directory.CreateDirectory(occupied);
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.True(Directory.Exists(occupied));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.State, "avatars"), "*.tmp"));
    }

    /// <summary>系统头像目录是重解析点时返回空结果，不写入链接外部目录。</summary>
    [Fact]
    public async Task ReparseAvatarDirectoryIsNotReadOrWritten()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.State, "outside");
        Directory.CreateDirectory(outside);
        JunctionOperations.Create(Path.Combine(fixture.State, "avatars"), outside);
        await fixture.WriteSteamAsync(".png", Fixture.Png);
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Empty(fixture.Handler.Requests);
    }

    /// <summary>下载期间缓存目录被换成真实 junction 时，安装前再次校验并保全外部目录。</summary>
    [Fact]
    public async Task ReparseDirectoryIntroducedDuringDownloadIsRejectedBeforeAtomicWrite()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Steam, "outside");
        fixture.Handler.Reply = (request, _) =>
        {
            if (request.RequestUri!.Host == "steamcommunity.com") return Task.FromResult(Fixture.Response(Fixture.Profile(Fixture.AvatarUrl)));
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(fixture.State);
            JunctionOperations.Create(Path.Combine(fixture.State, "avatars"), outside);
            return Task.FromResult(Fixture.Response(Fixture.Png));
        };
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    /// <summary>系统状态祖先目录是 junction 时同样拒绝缓存读写，不跟随外部目录。</summary>
    [Fact]
    public async Task ReparseStateAncestorDoesNotEscapeCacheBoundary()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Steam, "outside");
        Directory.CreateDirectory(outside);
        JunctionOperations.Create(fixture.State, outside);
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Empty(fixture.Handler.Requests);
    }

    /// <summary>调用方已释放 HTTP 客户端时头像失败被收敛为空结果。</summary>
    [Fact]
    public async Task DisposedInjectedClientReturnsNull()
    {
        using var fixture = new Fixture();
        var service = new AccountAvatarService(fixture.State, fixture.Client);
        fixture.Client.Dispose();
        Assert.Null(await service.GetAvatarPathAsync(fixture.Steam, Fixture.SteamId));
        Assert.Empty(fixture.Handler.Requests);
    }

    /// <summary>每个测试只拥有自己的临时目录和 HTTP 客户端。</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>合成的有效格式 Steam 标识。</summary>
        internal const string SteamId = "76561198000000001";
        /// <summary>合成的官方头像 CDN 路径，不连接真实账号。</summary>
        internal const string AvatarUrl = "https://avatars.steamstatic.com/fixture_full.jpg";
        /// <summary>完整一像素 PNG，供真实文件复制和网络响应使用。</summary>
        internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jxYsAAAAASUVORK5CYII=");
        /// <summary>完整一像素 JPEG，供真实本地文件与响应复用。</summary>
        internal static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDxyiiiv3E8w//Z");
        /// <summary>仅属于本测试的根目录。</summary>
        private readonly string _root = Path.Combine(Path.GetTempPath(), "md-avatar-tests-" + Guid.NewGuid().ToString("N"));
        /// <summary>隔离 Steam 安装路径。</summary>
        internal string Steam => Path.Combine(_root, "steam");
        /// <summary>隔离系统状态路径。</summary>
        internal string State => Path.Combine(_root, "state");
        /// <summary>明确记录所有网络请求的传输。</summary>
        internal StubHandler Handler { get; } = new();
        /// <summary>只连接注入传输的客户端。</summary>
        internal HttpClient Client { get; }
        /// <summary>创建自有客户端，不进行网络访问。</summary>
        internal Fixture() => Client = new HttpClient(Handler);
        /// <summary>写入隔离 Steam 本地头像并返回其完整路径。</summary>
        internal async Task<string> WriteSteamAsync(string extension, byte[] bytes)
        {
            var path = Path.Combine(Steam, "config", "avatarcache", SteamId + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        /// <summary>写入隔离系统缓存并返回其完整路径。</summary>
        internal async Task<string> WriteCacheAsync(string extension, byte[] bytes)
        {
            var path = Path.Combine(State, "avatars", SteamId + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        /// <summary>生成只含公开头像字段的最小社区 XML。</summary>
        internal static byte[] Profile(string url) => Encoding.UTF8.GetBytes("<profile><avatarFull><![CDATA[" + url + "]]></avatarFull></profile>");
        /// <summary>创建明确长度或未知长度的隔离 HTTP 正文。</summary>
        internal static HttpResponseMessage Response(byte[] bytes, bool unknownLength = false) => new(HttpStatusCode.OK) { Content = unknownLength ? new UnlengthContent(bytes) : new ByteArrayContent(bytes) };
        /// <summary>关闭自有传输并删除已核对位置的临时目录。</summary>
        public void Dispose()
        {
            Client.Dispose();
            ResourceFailureTests.DeleteTree(_root);
        }
    }

    /// <summary>默认拒绝意外 HTTP 请求，供测试明确指定响应。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>实际请求 URI 的完整序列。</summary>
        internal ConcurrentQueue<Uri> Requests { get; } = new();
        /// <summary>测试选择的完整异步响应行为。</summary>
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        /// <summary>设置公开资料和有效 PNG 响应。</summary>
        internal void SetValidDownload() => Reply = (request, _) => Task.FromResult(Fixture.Response(request.RequestUri!.Host == "steamcommunity.com" ? Fixture.Profile(Fixture.AvatarUrl) : Fixture.Png));
        /// <summary>记录请求并返回合成的未找到响应。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Reply(request, cancellationToken);
        }
    }

    /// <summary>模拟服务器不提供 Content-Length 时的流式响应。</summary>
    private sealed class UnlengthContent(byte[] bytes) : HttpContent
    {
        /// <summary>将本测试拥有的有限正文写入传输流。</summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        /// <summary>明确声明正文长度未知，触发真实流式大小限制。</summary>
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    /// <summary>完整记录被测日志文本与异常参数，验证敏感正文没有进入日志。</summary>
    private sealed class CaptureLogger : ILogger<AccountAvatarService>
    {
        /// <summary>实际输出的文本与异常。</summary>
        internal List<(string Message, Exception? Exception)> Records { get; } = [];
        /// <summary>本测试不使用日志作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>启用全部级别以捕获实际日志。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存格式化结果和独立异常参数。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Records.Add((formatter(state, exception), exception));
    }
}
