using System.Management.Automation;
using System.Text.Json.Nodes;
using Amm.PowerShell.Models;
using Amm.PowerShell.Pipe;

namespace Amm.PowerShell.Commands;

/// <summary>
/// 起動中の amm セッション一覧を取得する。
/// Get-AmmSession | Where-Object Title -like "Agent-*" | Close-AmmWindow のようにパイプラインで使える。
/// </summary>
[Cmdlet(VerbsCommon.Get, "AmmSession")]
[OutputType(typeof(AmmSession))]
public sealed class GetAmmSessionCmdlet : PSCmdlet
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
            var resp = client.SendRequest("initialize", new
            {
                protocol_version = "2024-11-05",
                capabilities = new { },
                client_info = new { name = "Amm.PowerShell", version = "0.1.0" },
            });
            // initialize 応答は読み捨て、次に list_participants を呼ぶ
            var result = client.GetResult("tools/call", new
            {
                name = "list_participants",
                arguments = new { },
            });

            var participants = result?["structuredContent"]?["participants"] as JsonArray;

            // structuredContent がなければ text を JSON パース
            if (participants == null && result?["content"] is JsonArray contentArr)
            {
                var text = (contentArr[0] as JsonObject)?["text"]?.GetValue<string>();
                if (text != null && JsonNode.Parse(text) is JsonObject parsed)
                    participants = parsed["participants"] as JsonArray;
            }

            if (participants == null) return;

            foreach (var item in participants)
            {
                if (item is not JsonObject p) continue;
                var nickname = p["nickname"]?.GetValue<string>() ?? "";
                var instance = p["instance"]?.GetValue<int>() ?? 1;
                var title = instance == 1 ? nickname : $"{nickname} ({instance})";
                var sessionId = p["session_id"]?.GetValue<string>() ?? "";
                WriteObject(new AmmSession(sessionId, title));
            }
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            WriteError(new ErrorRecord(ex, "GetAmmSessionFailed", ErrorCategory.ConnectionError, null));
        }
    }
}
