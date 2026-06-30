using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amm.Core;

/// <summary>
/// MDI・日付単位の統計情報 (指示回数 / AI 動作時間 / 人間の応答時間) を集計し
/// JSON ファイルへ書き出す。チャット記録 (ChatRecorder) とは別スイッチ・別実装で
/// 完全に独立しており、片方だけ ON にしても動作する。
/// 保存先: &lt;workDir&gt;\.amm\stats\&lt;yyyyMMdd&gt;\&lt;mdi名 (サニタイズ済み)&gt;.json
/// 1 ファイル = その日その MDI の累計 1 レコード (チャット記録のように交換ごとに
/// ファイルを増やすのではなく、既存ファイルを読み込んで加算し上書きする)。
/// UI スレッドから呼ぶこと (TerminalChildForm の完了デバウンスタイマ経由)。
/// </summary>
internal static class ChatStatsStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    public static string DirFor(string workDir, DateTime localDate) =>
        Path.Combine(workDir, ".amm", "stats", localDate.ToString("yyyyMMdd"));

    /// <summary>
    /// 1 交換分 (送信〜応答完了) を当日分の集計ファイルに加算する。
    /// humanMs は直前の交換の応答完了からこの送信までの経過時間 (= 人間の応答時間)。
    /// その MDI でのこの日最初の送信など、直前の応答完了時刻が無い場合は null。
    /// </summary>
    public static void RecordExchange(string workDir, string profileName, string mdiName,
        DateTime sentAtUtc, long aiMs, long? humanMs)
    {
        var localDate = sentAtUtc.ToLocalTime().Date;
        var dir = DirFor(workDir, localDate);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNameFor(mdiName));

        var record = Load(path) ?? new ChatStatsRecord
        {
            Date    = localDate.ToString("yyyy-MM-dd"),
            Profile = profileName,
            MdiName = mdiName,
        };

        record.InstructionCount++;
        record.AiTotalMs += aiMs;
        record.AiAvgMs = record.AiTotalMs / record.InstructionCount;
        if (humanMs is long h && h >= 0)
        {
            record.HumanSampleCount++;
            record.HumanTotalMs += h;
            record.HumanAvgMs = record.HumanTotalMs / record.HumanSampleCount;
        }
        record.UpdatedAt = DateTime.UtcNow;

        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions), Encoding.UTF8);
    }

    public static ChatStatsRecord? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ChatStatsRecord>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>指定 workDir・日付の stats フォルダ内の全レコードを列挙する (表示用)。</summary>
    public static List<ChatStatsRecord> LoadAll(string workDir, DateTime localDate)
    {
        var dir = DirFor(workDir, localDate);
        var result = new List<ChatStatsRecord>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var rec = Load(file);
            if (rec != null) result.Add(rec);
        }
        return result;
    }

    private static string FileNameFor(string mdiName) => SanitizeFileName(mdiName) + ".json";

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}

internal sealed class ChatStatsRecord
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    [JsonPropertyName("mdi_name")]
    public string MdiName { get; set; } = "";

    [JsonPropertyName("instruction_count")]
    public int InstructionCount { get; set; }

    [JsonPropertyName("ai_total_ms")]
    public long AiTotalMs { get; set; }

    [JsonPropertyName("ai_avg_ms")]
    public long AiAvgMs { get; set; }

    [JsonPropertyName("human_sample_count")]
    public int HumanSampleCount { get; set; }

    [JsonPropertyName("human_total_ms")]
    public long HumanTotalMs { get; set; }

    [JsonPropertyName("human_avg_ms")]
    public long HumanAvgMs { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
