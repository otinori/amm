using System.Management.Automation;
using Amm.PowerShell.Models;
using Amm.PowerShell.Pipe;

namespace Amm.PowerShell.Commands;

/// <summary>
/// amm に新しい MDI ターミナルウィンドウを開き、AmmSession オブジェクトを返す。
/// 返された session_id は Close-AmmWindow / Wait-AmmIdle / Send-AmmMessage に渡せる。
///
/// ByCommand: -Command でコマンドを直接指定 (一時プロファイル)。
/// ByProfile: -ProfileName で profiles.amm の既存プロファイルを名前指定して起動。
///            nickname / waitPatterns 等の設定を自動継承するため、Wait-AmmIdle も
///            そのまま使える。-WorkingDirectory を付けるとそのインスタンスだけ上書き。
/// </summary>
[Cmdlet(VerbsCommon.Open, "AmmWindow", DefaultParameterSetName = "ByCommand")]
[OutputType(typeof(AmmSession))]
public sealed class OpenAmmWindowCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "ByCommand")]
    public string Command { get; set; } = "";

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "ByProfile")]
    public string ProfileName { get; set; } = "";

    [Parameter(ParameterSetName = "ByCommand")]
    public string[] Args { get; set; } = [];

    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string? WorkingDirectory { get; set; }

    [Parameter]
    public string? PipeName { get; set; }

    [Parameter]
    public int ConnectTimeoutMs { get; set; } = 5000;

    protected override void ProcessRecord()
    {
        try
        {
            using var client = new AmmPipeClient(PipeName, ConnectTimeoutMs);

            string? command     = ParameterSetName == "ByCommand" ? Command     : null;
            string? profileName = ParameterSetName == "ByProfile" ? ProfileName : null;

            var result = client.GetResult("amm.openWindow", new
            {
                command,
                profile_name = profileName,
                args = Args,
                title = Title,
                working_directory = WorkingDirectory,
            });

            if (result == null)
                ThrowError("amm-mcp: no result returned from amm.openWindow");

            var error = result!["error"]?.GetValue<string>();
            if (error != null)
                ThrowError($"amm: {error}");

            var sessionId = result["session_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(sessionId))
                ThrowError("amm-mcp: session_id not returned");

            var displayTitle = Title ?? profileName ?? Command;
            WriteObject(new AmmSession(sessionId!, displayTitle));
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            var target = ParameterSetName == "ByProfile" ? ProfileName : Command;
            WriteError(new ErrorRecord(ex, "OpenAmmWindowFailed", ErrorCategory.ConnectionError, target));
        }
    }

    private void ThrowError(string message) =>
        ThrowTerminatingError(new ErrorRecord(
            new InvalidOperationException(message), "OpenAmmWindowFailed",
            ErrorCategory.InvalidResult, ParameterSetName == "ByProfile" ? ProfileName : Command));
}
