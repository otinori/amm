using System.Management.Automation;

namespace Amm.PowerShell.Commands;

/// <summary>
/// amm との接続セッションを終了する (現在は各 cmdlet が使い捨て接続のため no-op)。
/// 将来の永続接続モードに向けた予約。
/// </summary>
[Cmdlet(VerbsCommunications.Disconnect, "Amm")]
public sealed class DisconnectAmmCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteVerbose("Disconnect-Amm: 現在の実装では各 cmdlet が接続を個別に管理するため操作不要です。");
    }
}
