using System.Management.Automation;
using System.Text.Json.Nodes;
using Amm.PowerShell.Models;
using Amm.PowerShell.Pipe;

namespace Amm.PowerShell.Commands;

/// <summary>
/// 指定セッションが入力待ち (idle) になるまで待機する。
/// パイプライン: Open-AmmWindow ... | Wait-AmmIdle が使える。
/// nickname 指定時は Get-AmmSession 相当の解決を内部で行う。
/// </summary>
[Cmdlet(VerbsLifecycle.Wait, "AmmIdle", DefaultParameterSetName = "BySessionId")]
[OutputType(typeof(WaitResult))]
public sealed class WaitAmmIdleCmdlet : PSCmdlet
{
    [Parameter(ParameterSetName = "BySessionId", Mandatory = true, Position = 0,
               ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    public string SessionId { get; set; } = "";

    [Parameter(ParameterSetName = "ByNickname", Mandatory = true, Position = 0)]
    public string Nickname { get; set; } = "";

    [Parameter]
    public string TargetState { get; set; } = "idle";

    [Parameter]
    public int TimeoutMs { get; set; } = 300_000;

    [Parameter]
    public string? PipeName { get; set; }

    [Parameter]
    public int ConnectTimeoutMs { get; set; } = 5000;

    protected override void ProcessRecord()
    {
        try
        {
            var resolvedSessionId = ParameterSetName == "ByNickname"
                ? ResolveNickname(Nickname)
                : SessionId;

            if (string.IsNullOrEmpty(resolvedSessionId)) return; // ThrowTerminatingError 済み

            using var client = new AmmPipeClient(PipeName, ConnectTimeoutMs);
            // readTimeoutMs=0: サーバ側が目標状態到達まで応答を保留するため無制限待機。
            // サーバ側タイムアウトは TimeoutMs パラメータで制御。
            var result = client.GetResult("amm.waitState", new
            {
                session_id = resolvedSessionId,
                target_state = TargetState,
                timeout_ms = TimeoutMs,
            }, readTimeoutMs: 0);

            if (result == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("amm-mcp: no result returned from amm.waitState"),
                    "WaitAmmIdleNoResult", ErrorCategory.InvalidResult, resolvedSessionId));
                return;
            }

            var state = result!["state"]?.GetValue<string>();
            if (state == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("amm-mcp: state missing in waitState response"),
                    "WaitAmmIdleFailed", ErrorCategory.InvalidResult, resolvedSessionId));
                return;
            }

            var elapsedMs = result["elapsed_ms"]?.GetValue<int>() ?? 0;
            WriteObject(new WaitResult(state, elapsedMs));
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            var target = ParameterSetName == "ByNickname" ? Nickname : SessionId;
            WriteError(new ErrorRecord(ex, "WaitAmmIdleFailed", ErrorCategory.ConnectionError, target));
        }
    }

    /// <summary>
    /// nickname を list_participants で解決して session_id を返す。
    /// 見つからない場合は ThrowTerminatingError して null を返す。
    /// </summary>
    private string? ResolveNickname(string nickname)
    {
        using var listClient = new AmmPipeClient(PipeName, ConnectTimeoutMs);
        listClient.SendRequest("initialize", new
        {
            protocol_version = "2024-11-05",
            capabilities = new { },
            client_info = new { name = "Amm.PowerShell", version = "0.1.0" },
        });
        var listResult = listClient.GetResult("tools/call", new
        {
            name = "list_participants",
            arguments = new { },
        });

        var participants = listResult?["structuredContent"]?["participants"] as JsonArray;
        var match = participants?
            .OfType<JsonObject>()
            .FirstOrDefault(p => string.Equals(
                p["nickname"]?.GetValue<string>(), nickname,
                StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException($"amm: nickname '{nickname}' が見つかりません"),
                "WaitAmmIdleNicknameNotFound", ErrorCategory.ObjectNotFound, nickname));
            return null;
        }

        var sessionId = match["session_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sessionId))
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException($"amm: '{nickname}' に session_id がありません"),
                "WaitAmmIdleNoSessionId", ErrorCategory.InvalidResult, nickname));
            return null;
        }

        return sessionId;
    }
}
