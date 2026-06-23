namespace Amm.Core;

public sealed class InputHistory
{
    public const int DefaultMaxEntries = 500;

    private readonly List<string> _entries = [];
    private int _maxEntries;
    private int _cursor;

    public InputHistory(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _cursor = 0;
    }

    public int MaxEntries => _maxEntries;
    public int Count => _entries.Count;

    /// <summary>
    /// 上限値を変更する。新上限が現在の件数より小さい場合は古い順 (先頭) から
    /// 切り詰める。layout.json から読み込んだ HistoryMaxEntries を反映する用。
    /// </summary>
    public void SetMaxEntries(int maxEntries)
    {
        _maxEntries = Math.Max(1, maxEntries);
        while (_entries.Count > _maxEntries) _entries.RemoveAt(0);
        if (_cursor > _entries.Count) _cursor = _entries.Count;
    }

    /// <summary>
    /// 永続化ファイルから古→新順のリストで一括復元する。既存エントリは破棄。
    /// 上限超過分は古い側を切り捨てる。空白/null は除外。
    /// </summary>
    public void LoadFromOldestFirst(IEnumerable<string> entries)
    {
        _entries.Clear();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            _entries.Add(e);
        }
        while (_entries.Count > _maxEntries) _entries.RemoveAt(0);
        _cursor = _entries.Count;
    }

    /// <summary>
    /// 古→新順の永続化用スナップショット。
    /// </summary>
    public IReadOnlyList<string> SnapshotOldestFirst()
    {
        return _entries.ToArray();
    }

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // 完全重複排除: 既存に同じテキストがあれば全て除去し、新規を末尾に追加する
        // (連続でない重複も整理し「最新 1 件だけ残す」挙動)
        _entries.RemoveAll(e => e == text);

        _entries.Add(text);
        if (_entries.Count > _maxEntries)
            _entries.RemoveAt(0);

        _cursor = _entries.Count; // Reset cursor past the end
    }

    public string? NavigateUp()
    {
        if (_entries.Count == 0) return null;
        if (_cursor > 0) _cursor--;
        return _entries[_cursor];
    }

    public string? NavigateDown()
    {
        if (_entries.Count == 0) return null;
        if (_cursor < _entries.Count - 1)
        {
            _cursor++;
            return _entries[_cursor];
        }
        // Past the end = clear
        _cursor = _entries.Count;
        return "";
    }

    public void ResetCursor() => _cursor = _entries.Count;

    /// <summary>
    /// 直近 count 件を新しい順に返す (UI 一覧表示用)。count <= 0 なら空配列。
    /// </summary>
    public IReadOnlyList<string> GetRecent(int count)
    {
        if (count <= 0 || _entries.Count == 0) return Array.Empty<string>();
        var n = Math.Min(count, _entries.Count);
        var result = new string[n];
        for (int i = 0; i < n; i++)
            result[i] = _entries[_entries.Count - 1 - i];
        return result;
    }
}
