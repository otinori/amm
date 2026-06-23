using System.Management.Automation;
using Amm.PowerShell.Pipe;

namespace Amm.PowerShell.Commands;

/// <summary>
/// amm.exe の Named Pipe への接続を検証する。
/// 各 cmdlet は暗黙的に自動接続するので必須ではないが、事前に接続確認したい場合に使う。
/// </summary>
[Cmdlet(VerbsCommunications.Connect, "Amm")]
public sealed class ConnectAmmCmdlet : PSCmdlet
{
    [Parameter]
    public string? PipeName { get; set; }

    [Parameter]
    public int ConnectTimeoutMs { get; set; } = 5000;

    protected override void ProcessRecord()
    {
        try
        {
            using var client = new AmmPipeClient(PipeName, ConnectTimeoutMs);
            WriteVerbose($"amm に接続しました (pipe={AmmPipeClient.DefaultPipeName})。");
        }
        catch (TimeoutException)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"amm GUI に接続できませんでした。amm を起動してから再試行してください。"),
                "AmmNotRunning", ErrorCategory.ConnectionError, null));
        }
    }
}
