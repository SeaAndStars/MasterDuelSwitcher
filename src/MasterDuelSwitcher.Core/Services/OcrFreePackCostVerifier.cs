using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>结合文字和局部宝石图标判断卡包费用区域是否明确免费。</summary>
public interface IPackFeeVerifier
{
    /// <summary>只在文字明确免费且缺少收费证据时允许后续原始模板门禁继续判断。</summary>
    /// <param name="bgraPixels">费用区域逐行紧密排列的BGRA字节。</param>
    /// <param name="width">区域物理像素宽度。</param>
    /// <param name="height">区域物理像素高度。</param>
    /// <param name="gemDetected">限定费用区域中是否匹配宝石图标。</param>
    /// <returns>文字和图标同时确认明确免费时返回true。</returns>
    bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected);
}

/// <summary>从OCR文本建立明确免费证据并排除宝石费用和余额扣减。</summary>
public sealed class OcrFreePackCostVerifier : IPackFeeVerifier
{
    /// <summary>仅接受独立免费词或以费用次数免费短语结束的费用行，避免匹配卡包名称。</summary>
    private const string ExplicitFreePattern = @"(?:\d+次免费(?=$|[)）.,。;；!！])|(?:^|[(（:：.,。;；])免费(?=$|[)）.,。;；!！]))";
    /// <summary>收费文字、宝石数额和余额扣减箭头都优先于免费字样。</summary>
    private const string PaidEvidencePattern = @"(?:消费|消耗|花费|支付|扣除|需付|收费|有偿|所持宝石|余额(?:扣减|扣除)|\d+(?:颗|个)?宝石|宝石[:：]?\d+|\d+(?:→|⇒|⟶|➜|->|—|−|-|乛)\d+)";
    /// <summary>明确否定免费或免费次数失效的语句不能形成购买授权。</summary>
    private const string InvalidFreePattern = @"(?:(?:不(?:是)?|非|无|未|没有)(?:\d+次)?免费|免费(?:次数|体验)?(?:已)?(?:结束|用完|耗尽|失效))";
    /// <summary>注入的系统或可控费用区域文字读取器。</summary>
    private readonly IPackTextReader reader;
    /// <summary>记录费用门禁结论与OCR停止异常的诊断日志。</summary>
    private readonly ILogger<OcrFreePackCostVerifier> logger;

    /// <summary>配置文字读取器和费用判定诊断日志。</summary>
    /// <param name="reader">可注入的局部文字识别器。</param>
    /// <param name="logger">费用判定日志，省略时使用空日志。</param>
    public OcrFreePackCostVerifier(IPackTextReader reader, ILogger<OcrFreePackCostVerifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
        this.logger = logger ?? NullLogger<OcrFreePackCostVerifier>.Instance;
    }

    /// <summary>从局部费用区域的明确文字和图标证据判断是否免费。</summary>
    /// <param name="bgraPixels">费用区域逐行紧密排列的BGRA字节。</param>
    /// <param name="width">区域物理像素宽度。</param>
    /// <param name="height">区域物理像素高度。</param>
    /// <param name="gemDetected">限定费用区域中是否匹配宝石图标。</param>
    /// <returns>明确免费且缺少收费证据时返回true。</returns>
    public bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected)
    {
        if (gemDetected)
        {
            logger.LogDebug("卡包费用门禁拒绝：费用区域具有宝石图标。");
            return false;
        }
        string raw;
        try
        {
            raw = reader.Read(bgraPixels, width, height);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "卡包费用 OCR 识别失败，自动开包已停止。");
            throw new InvalidOperationException("费用文字识别失败，自动开包已停止；请检查 Windows 简体中文 OCR 组件。", exception);
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            logger.LogDebug("卡包费用门禁拒绝：未识别到明确费用文字。");
            return false;
        }
        string text = Normalize(raw);
        bool explicitFree = Regex.IsMatch(text, ExplicitFreePattern, RegexOptions.CultureInvariant);
        bool paid = Regex.IsMatch(text, PaidEvidencePattern, RegexOptions.CultureInvariant);
        bool invalidFree = Regex.IsMatch(text, InvalidFreePattern, RegexOptions.CultureInvariant);
        bool free = explicitFree && !paid && !invalidFree;
        logger.LogDebug("卡包费用文字门禁：明确免费 {ExplicitFree}，收费证据 {PaidEvidence}，免费失效 {InvalidFree}，通过 {Free}",
            explicitFree, paid, invalidFree, free);
        return free;
    }

    /// <summary>统一全角数字和标点，移除字符间空白，同时保留语句、行边界及余额变化箭头。</summary>
    /// <param name="text">原生OCR返回的费用区域文字。</param>
    /// <returns>保留语义分隔的紧密费用文字。</returns>
    private static string Normalize(string text)
    {
        string normalized = Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\r\n?|\n", ";", RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, @"\s", string.Empty, RegexOptions.CultureInvariant);
    }
}
