using System.Management.Automation;
using System.Text.Json.Nodes;
using Amm.PowerShell.Pipe;

namespace Amm.PowerShell.Commands;

/// <summary>
/// amm の MDI ウィンドウへメッセージを送信する。
/// パイプライン: Get-AmmSession | Send-AmmMessage -Message "..." が使える。
/// </summary>
[Cmdlet(VerbsCommunications.Send, "AmmMessage")]
public sealed class SendAmmMessageCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
               ValueFromPipelineByPropertyName = true)]
    [Alias("Title")]
    public string Nickname { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1)]
    public string Message { get; set; } = "";

    [Parameter]
    public string Mode { get; set; } = "first";

    [Parameter]
    public string? PipeName { get; set; }

    [Parameter]
    public int ConnectTimeoutMs { get; set; } = 5000;

    protected override void ProcessRecord()
    {
        try
        {
            using var client = new AmmPipeClient(PipeName, ConnectTimeoutMs);
            client.SendRequest("initialize", new
            {
                protocol_version = "2024-11-05",
                capabilities = new { },
                client_info = new { name = "Amm.PowerShell", version = "0.1.0" },
            });

            var result = client.GetResult("tools/call", new
            {
                name = "send_message",
                arguments = new
                {
                    recipient = Nickname,
                    message = Message,
                    mode = Mode,
                },
            });

            int? delivered = result?["structuredContent"]?["delivered_count"]?.GetValue<int>();
            int? queued   = result?["structuredContent"]?["queued_count"]?.GetValue<int>();

            if (delivered == null && result?["content"] is JsonArray contentArr)
            {
                var text = (contentArr[0] as JsonObject)?["text"]?.GetValue<string>();
                if (text != null && JsonNode.Parse(text) is JsonObject parsed)
                {
                    delivered = parsed["delivered_count"]?.GetValue<int>();
                    queued    = parsed["queued_count"]?.GetValue<int>();
                }
            }

            WriteVerbose($"Send-AmmMessage: delivered={delivered ?? 0}, queued={queued ?? 0}");
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            WriteError(new ErrorRecord(ex, "SendAmmMessageFailed", ErrorCategory.ConnectionError, Nickname));
        }
    }
}
