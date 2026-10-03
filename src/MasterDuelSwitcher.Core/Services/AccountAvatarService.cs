using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml;
using System.Xml.Linq;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>取得账号头像的有效本地缓存路径，失败时保留界面首字头像。</summary>
public interface IAccountAvatarService
{
    /// <summary>优先读取本地 Steam 头像，再获取公开社区头像并保存到独立系统缓存。</summary>
    Task<string?> GetAvatarPathAsync(string steamPath, string steamId, CancellationToken cancellationToken = default);
}

/// <summary>协调本地头像、公开头像下载及有大小限制的原子缓存。</summary>
public sealed class AccountAvatarService : IAccountAvatarService
{
    /// <summary>头像正文最大为两 MiB，禁止无界分配与缓存。</summary>
    private const int MaximumImageBytes = 2 * 1024 * 1024;
    /// <summary>公开资料 XML 最多读取二百五十六 KiB。</summary>
    private const int MaximumProfileBytes = 256 * 1024;
    /// <summary>进程级 HTTP 客户端禁用重定向，不继承 Steam 登录状态。</summary>
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    /// <summary>可接受的本地头像扩展名，按 PNG 优先查找。</summary>
    private static readonly string[] Extensions = [".png", ".jpg"];
    /// <summary>当前 Steam 使用的专用头像 CDN 主机名。</summary>
    private static readonly string[] AvatarHosts = ["avatars.steamstatic.com", "avatars.fastly.steamstatic.com", "avatars.akamai.steamstatic.com"];
    /// <summary>官方文档中的历史 CDN，只允许其公开头像目录。</summary>
    private static readonly string[] LegacyHosts = ["steamcdn-a.akamaihd.net", "cdn.akamai.steamstatic.com", "media.steampowered.com"];
    /// <summary>PNG 文件签名及首个 IHDR 块头。</summary>
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];
    /// <summary>PNG 文件完整的 IEND 结束块。</summary>
    private static readonly byte[] PngEnd = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
    /// <summary>JPEG 文件的起始标记。</summary>
    private static readonly byte[] JpegHeader = [255, 216, 255];
    /// <summary>JPEG 文件的结束标记。</summary>
    private static readonly byte[] JpegEnd = [255, 217];
    /// <summary>独立于 Steam 安装的系统头像缓存目录。</summary>
    private readonly string _avatarDirectory;
    /// <summary>默认或测试注入的 HTTP 传输，生命周期由提供方管理。</summary>
    private readonly HttpClient _httpClient;
    /// <summary>只输出固定短状态，不记录资料正文、URL 或账号。</summary>
    private readonly ILogger<AccountAvatarService> _logger;
    /// <summary>最多同时执行四项头像读取与下载，不暴露可提前销毁的等待句柄。</summary>
    private readonly SemaphoreSlim _gate = new(4, 4);

    /// <summary>注入独立状态目录、可测试 HTTP 边界和不记录账号正文的日志。</summary>
    public AccountAvatarService(string stateDirectory, HttpClient? httpClient = null, ILogger<AccountAvatarService>? logger = null)
    {
        _avatarDirectory = Path.GetFullPath(Path.Combine(stateDirectory, "avatars"));
        _httpClient = httpClient ?? DefaultClient;
        _logger = logger ?? NullLogger<AccountAvatarService>.Instance;
    }

    /// <summary>以八秒总预算取得头像；读取、传输、取消或缓存失败均返回空结果。</summary>
    public async Task<string?> GetAvatarPathAsync(string steamPath, string steamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(steamId) || steamId.Length != 17 || !steamId.All(char.IsAsciiDigit) || cancellationToken.IsCancellationRequested) return null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
            try { return await GetCoreAsync(steamPath, steamId, budget.Token).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        catch (Exception)
        {
            _logger.LogDebug("账号头像暂未就绪，保留首字头像。");
            return null;
        }
    }

    /// <summary>依次复用系统缓存、复制 Steam 本地文件和读取公开资料头像。</summary>
    private async Task<string?> GetCoreAsync(string steamPath, string steamId, CancellationToken cancellationToken)
    {
        if (CacheUsesReparseAncestor()) return null;
        foreach (var extension in Extensions)
        {
            var path = Path.Combine(_avatarDirectory, steamId + extension);
            if (await ReadImageFileAsync(path, cancellationToken).ConfigureAwait(false) is not null) return path;
        }
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            foreach (var extension in Extensions)
            {
                var path = Path.Combine(steamPath, "config", "avatarcache", steamId + extension);
                var image = await ReadImageFileAsync(path, cancellationToken).ConfigureAwait(false);
                if (image is not null) return await SaveAsync(steamId, image, ImageExtension(image)!, cancellationToken).ConfigureAwait(false);
            }
        }
        var profile = await DownloadAsync(new Uri("https://steamcommunity.com/profiles/" + steamId + "/?xml=1"), MaximumProfileBytes, cancellationToken).ConfigureAwait(false);
        if (profile is null) return null;
        using var xmlStream = new MemoryStream(profile, false);
        using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumProfileBytes });
        var document = XDocument.Load(reader);
        if (document.Root!.Name != "profile") return null;
        if (!Uri.TryCreate(document.Root.Element("avatarFull")?.Value, UriKind.Absolute, out var avatar) || !AllowedAvatar(avatar)) return null;
        var downloaded = await DownloadAsync(avatar, MaximumImageBytes, cancellationToken).ConfigureAwait(false);
        if (downloaded is null) return null;
        var imageExtension = ImageExtension(downloaded);
        return imageExtension is null ? null : await SaveAsync(steamId, downloaded, imageExtension, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拒绝系统缓存目录及已有祖先上的重解析点，避免写入目录外部。</summary>
    private bool CacheUsesReparseAncestor()
    {
        for (var directory = new DirectoryInfo(_avatarDirectory); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    /// <summary>读取有大小和图片头尾限制的本地文件，损坏文件视为缓存未命中。</summary>
    private static async Task<byte[]?> ReadImageFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        var image = await ReadBoundedAsync(stream, MaximumImageBytes, cancellationToken).ConfigureAwait(false);
        return image is not null && ImageExtension(image) is not null ? image : null;
    }

    /// <summary>只接受成功 HTTP 响应，并同时限制声明长度和真实流式长度。</summary>
    private async Task<byte[]?> DownloadAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>以固定八 KiB 缓冲读取最多指定字节，未知长度也不会无界分配。</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > maximumBytes) return null;
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    /// <summary>只允许标准 HTTPS 官方头像主机，历史 CDN 另外限制公开头像路径。</summary>
    private static bool AllowedAvatar(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        if (AvatarHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return true;
        return LegacyHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/steamcommunity/public/images/avatars/", StringComparison.Ordinal);
    }

    /// <summary>校验 PNG 或 JPEG 起止标识并返回真实扩展名，完整像素解码由 WPF 承担。</summary>
    private static string? ImageExtension(byte[] image)
    {
        if (image.Length >= 33 && image.AsSpan(0, PngHeader.Length).SequenceEqual(PngHeader) && image.AsSpan(image.Length - PngEnd.Length).SequenceEqual(PngEnd)) return ".png";
        if (image.Length >= 5 && image.AsSpan(0, JpegHeader.Length).SequenceEqual(JpegHeader) && image.AsSpan(image.Length - JpegEnd.Length).SequenceEqual(JpegEnd)) return ".jpg";
        return null;
    }

    /// <summary>将有效头像写入自有临时文件，再原子替换指定 ID 缓存并清理临时文件。</summary>
    private async Task<string> SaveAsync(string steamId, byte[] image, string extension, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_avatarDirectory);
        if (CacheUsesReparseAncestor()) throw new IOException("头像缓存目录已发生变化。");
        var destination = Path.Combine(_avatarDirectory, steamId + extension);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(image, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { File.Delete(temporary); }
    }
}
