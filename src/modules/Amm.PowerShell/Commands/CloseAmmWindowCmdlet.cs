using System.Management.Automation;
using Amm.PowerShell.Models;
using Amm.PowerShell.Pipe;

namespace Amm.PowerShell.Commands;

/// <summary>
/// 指定した MDI ウィンドウを閉じる。
/// パイプライン: Get-AmmSession | Close-AmmWindow や Open-AmmWindow | Close-AmmWindow が動く。
/// </summary>
[Cmdlet(VerbsCommon.Close, "AmmWindow", SupportsShouldProcess = true)]
public sealed class CloseAmmWindowCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
               ValueFromPipelineByPropertyName = true)]
    public string SessionId { get; set; } = "";

    [Parameter]
    public SwitchParameter Force { get; set; }

    [Parameter]
    public string? PipeName { get; set; }

    [Parameter]
    public int ConnectTimeoutMs { get; set; } = 5000;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(SessionId, "Close-AmmWindow")) return;
        try
        {
            using var client = new AmmPipeClient(PipeName, ConnectTimeoutMs);
            var result = client.GetResult("amm.closeWindow", new
            {
                session_id = SessionId,
                force = Force.IsPresent,
            });

            if (result?["error"]?.GetValue<string>() is string err)
                WriteWarning($"Close-AmmWindow: {err}");
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            WriteError(new ErrorRecord(ex, "CloseAmmWindowFailed", ErrorCategory.ConnectionError, SessionId));
        }
    }
}
