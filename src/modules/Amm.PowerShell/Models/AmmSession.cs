namespace Amm.PowerShell.Models;

/// <summary>
/// Open-AmmWindow が返す MDI セッション情報。
/// パイプラインで Close-AmmWindow / Wait-AmmIdle に渡せる。
/// </summary>
public sealed class AmmSession
{
    public string SessionId { get; }
    public string Title { get; }

    public AmmSession(string sessionId, string title)
    {
        SessionId = sessionId;
        Title = title;
    }

    public override string ToString() => $"AmmSession {{ SessionId={SessionId}, Title={Title} }}";
}
