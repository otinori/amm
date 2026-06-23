using System.Collections.Concurrent;

namespace Amm.Core.Mcp;

/// <summary>
/// nickname 単位のメッセージキュー (UDR-amm-20260427T0225-7a3)。1 nickname あたり
/// 上限件数を超えると古い順から drop する。アプリ終了でクリア (永続化なし)。
/// </summary>
public sealed class MessageQueue
{
    private readonly int _maxPerNickname;
    private readonly ConcurrentDictionary<string, Queue<string>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public MessageQueue(int maxPerNickname = 100)
    {
        _maxPerNickname = maxPerNickname;
    }

    public void Enqueue(string nickname, string message)
    {
        lock (_lock)
        {
            var q = _queues.GetOrAdd(nickname, _ => new Queue<string>());
            q.Enqueue(message);
            while (q.Count > _maxPerNickname)
                q.Dequeue();
        }
    }

    /// <summary>該当 nickname の全メッセージを取り出してクリアする。</summary>
    public string[] DequeueAll(string nickname)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(nickname, out var q) || q.Count == 0) return [];
            var arr = q.ToArray();
            q.Clear();
            return arr;
        }
    }

    /// <summary>取り出さずに参照する (peek_queue 用)。</summary>
    public string[] Peek(string nickname)
    {
        lock (_lock)
        {
            return _queues.TryGetValue(nickname, out var q) ? q.ToArray() : [];
        }
    }

    /// <summary>現在キューに入っている全 nickname と件数のスナップショット。</summary>
    public Dictionary<string, string[]> Snapshot()
    {
        lock (_lock)
        {
            return _queues.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
