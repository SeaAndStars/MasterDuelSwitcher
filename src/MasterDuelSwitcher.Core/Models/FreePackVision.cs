namespace MasterDuelSwitcher.Core.Models;

/// <summary>目标游戏客户区的一帧物理像素，字节排列为逐行紧密 BGRA。</summary>
/// <param name="WindowHandle">捕获时核验的目标窗口句柄。</param>
/// <param name="Width">客户区物理像素宽度。</param>
/// <param name="Height">客户区物理像素高度。</param>
/// <param name="ScreenX">客户区左上角的屏幕物理横坐标。</param>
/// <param name="ScreenY">客户区左上角的屏幕物理纵坐标。</param>
/// <param name="Pixels">由调用方持有的紧密 BGRA 像素数组。</param>
/// <param name="CapturedAtUtc">帧捕获的协调世界时。</param>
public sealed record GameFrame(nint WindowHandle, int Width, int Height, int ScreenX, int ScreenY,
    byte[] Pixels, DateTimeOffset CapturedAtUtc);

/// <summary>相对于游戏客户区左上角的物理像素坐标。</summary>
/// <param name="X">客户区内的物理横坐标。</param>
/// <param name="Y">客户区内的物理纵坐标。</param>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>经过视觉组合验证的卡包界面状态。</summary>
public enum PackScreen
{
    /// <summary>当前画面尚未满足任一已知界面的识别条件。</summary>
    Unknown,
    /// <summary>具有卡包详情锚点及下一包导航的详情界面。</summary>
    PackDetails,
    /// <summary>同时识别购买弹窗、免费文字及购买按钮的免费确认界面。</summary>
    FreePurchaseDialog,
    /// <summary>购买弹窗已经识别，但免费文字组合尚未通过验证。</summary>
    UnverifiedPurchaseDialog,
    /// <summary>具有打开或跳过按钮的卡包开封动画界面。</summary>
    Opening,
    /// <summary>具有开封结果标题及确认按钮的结果界面。</summary>
    Results
}

/// <summary>一次视觉识别得到的界面、可点击目标及诊断信息。</summary>
/// <param name="Screen">通过组合锚点验证的界面状态。</param>
/// <param name="PrimaryTarget">当前状态的主要动作坐标；未通过验证时为空。</param>
/// <param name="NextTarget">详情页右侧下一包双箭头的客户区坐标。</param>
/// <param name="FreeOffer">入口或弹窗已经满足免费文字和按钮组合条件。</param>
/// <param name="Fingerprint">仅详情页提供的稳定卡包图像指纹，其他状态为空。</param>
/// <param name="Confidence">当前组合识别的最低归一化匹配分数。</param>
public sealed record PackObservation(PackScreen Screen, PixelPoint? PrimaryTarget, PixelPoint? NextTarget,
    bool FreeOffer, string Fingerprint, double Confidence);
