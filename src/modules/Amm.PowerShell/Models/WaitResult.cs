namespace Amm.PowerShell.Models;

/// <summary>Wait-AmmIdle の戻り値。</summary>
public sealed class WaitResult
{
    /// <summary>"idle" | "attention" | "timeout"</summary>
    public string State { get; }
    public int ElapsedMs { get; }

    public WaitResult(string state, int elapsedMs)
    {
        State = state;
        ElapsedMs = elapsedMs;
    }

    public override string ToString() => $"WaitResult {{ State={State}, ElapsedMs={ElapsedMs} }}";
}
