using System.Text.Json.Nodes;
using Amm.Mcp;

namespace Amm.Tests;

/// <summary>
/// NotifyPayloadMapper (CLI hook ペイロード → amm 状態語彙の正規化,
/// UDR-amm-20260605T0523-7e1) の検証。
/// </summary>
public class NotifyPayloadMapperTests
{
    private static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void NullPayload_DefaultsToIdle()
    {
        Assert.Equal("idle", NotifyPayloadMapper.MapState(null));
    }

    [Fact]
    public void ClaudeStop_MapsToIdle()
    {
        var p = J("""{"session_id":"abc","hook_event_name":"Stop","stop_hook_active":false}""");
        Assert.Equal("idle", NotifyPayloadMapper.MapState(p));
    }

    [Theory]
    [InlineData("idle_prompt", "idle")]
    [InlineData("agent_idle", "idle")]
    [InlineData("agent_completed", "idle")]
    [InlineData("permission_prompt", "attention")]
    [InlineData("elicitation_dialog", "attention")]
    public void Notification_MapsByType(string type, string expected)
    {
        var p = J($$"""{"hook_event_name":"Notification","notification_type":"{{type}}","message":"x"}""");
        Assert.Equal(expected, NotifyPayloadMapper.MapState(p));
    }

    [Theory]
    [InlineData("shell_completed")]
    [InlineData("auth_success")]
    [InlineData("something_future")]
    public void Notification_IrrelevantTypes_AreIgnored(string type)
    {
        var p = J($$"""{"hook_event_name":"Notification","notification_type":"{{type}}"}""");
        Assert.Null(NotifyPayloadMapper.MapState(p));
    }

    [Fact]
    public void CodexAgentTurnComplete_MapsToIdle()
    {
        var p = J("""{"type":"agent-turn-complete","turn-id":"1","input-messages":["x"]}""");
        Assert.Equal("idle", NotifyPayloadMapper.MapState(p));
    }

    [Fact]
    public void UnknownEventName_IsIgnored()
    {
        var p = J("""{"hook_event_name":"PreToolUse","tool_name":"Bash"}""");
        Assert.Null(NotifyPayloadMapper.MapState(p));
    }

    [Fact]
    public void PayloadWithoutEventHints_DefaultsToIdle()
    {
        var p = J("""{"some":"thing"}""");
        Assert.Equal("idle", NotifyPayloadMapper.MapState(p));
    }
}
