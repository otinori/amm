using System.Text.Json.Nodes;

namespace Amm.Mcp;

/// <summary>
/// CLI hook が渡してくるペイロード (stdin JSON / argv JSON) を amm の状態語彙
/// (idle / attention / busy) に正規化する (UDR-amm-20260605T0523-7e1)。
/// CLI ごとのイベント名の違いをここで吸収し、amm GUI 側は語彙だけを見る。
///
/// 対応ペイロード:
/// - Claude Code hooks  : stdin JSON。hook_event_name = "Stop" | "Notification"、
///                        Notification は notification_type で細分。
/// - Copilot CLI hooks  : stdin JSON。Claude 互換の hook_event_name +
///                        notification_type (agent_idle / agent_completed /
///                        permission_prompt / elicitation_dialog / shell_*)。
/// - Codex CLI notify   : argv 末尾 JSON。type = "agent-turn-complete"。
/// </summary>
public static class NotifyPayloadMapper
{
    /// <summary>
    /// ペイロードから正規化済み状態を返す。null = 通知対象外イベント (無視)。
    /// ペイロード無し (null / 解析不能) は「hook が登録されている = 何かの区切り」
    /// とみなし idle を返す (Stop hook を payload なしで素朴に登録しても機能する)。
    /// </summary>
    public static string? MapState(JsonObject? payload)
    {
        if (payload == null) return "idle";

        // Codex CLI notify: {"type": "agent-turn-complete", ...}
        var codexType = payload["type"]?.GetValue<string>();
        if (string.Equals(codexType, "agent-turn-complete", StringComparison.OrdinalIgnoreCase))
            return "idle";

        var eventName = payload["hook_event_name"]?.GetValue<string>();
        if (string.Equals(eventName, "Stop", StringComparison.OrdinalIgnoreCase))
            return "idle";

        if (string.Equals(eventName, "Notification", StringComparison.OrdinalIgnoreCase))
        {
            var nt = payload["notification_type"]?.GetValue<string>();
            return nt?.ToLowerInvariant() switch
            {
                // 「入力待ちで放置されている」「エージェントが完了した」系
                "idle_prompt" or "agent_idle" or "agent_completed" => "idle",
                // 「人の判断を待っている」系 (現状は idle と同じ表示。将来色分け)
                "permission_prompt" or "elicitation_dialog" => "attention",
                // shell_completed 等、エージェント全体の状態を意味しないものは無視
                _ => null,
            };
        }

        // hook_event_name も type も無い JSON → 由来不明だが、AMM_NOTIFY_ID が
        // ある環境からの呼び出しなので idle 扱い (素朴な手動登録を許容)
        if (eventName == null && codexType == null) return "idle";
        return null;
    }
}
