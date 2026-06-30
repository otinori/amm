using System.Text;
using System.Text.Json;

namespace Amm.Core;

/// <summary>
/// 1コマンド分のチャット（送信テキスト＋応答末尾）を JSON ファイルへ書き出す。
/// TerminalChildForm が生成・保持し、コマンド送信時に生成、出力が一定時間
/// 途絶えた時点 (ChatRecordQuietMs 経過) で Complete() を呼ぶ。
/// 保存先は &lt;saveDir&gt;/logs/yyyyMMdd/ (yyyyMMdd は会話の送信時刻のローカル日付)。
/// 同フォルダに 1 日 1 ファイルの index.json も追記し、その日送信したプロンプトの
/// 1 行目・送信時刻・対応する会話 JSON ファイル名の一覧を保持する。
/// スレッドセーフではない。Feed は ConPTY スレッドから呼ばれるが、
/// Complete は BeginInvoke 後の UI スレッドから呼ぶこと。
/// </summary>
internal sealed class ChatRecorder
{
    private readonly string _saveDir;
    private readonly int _tailChars;
    private readonly string _profileName;
    private readonly string _mdiName;
    private readonly string _command;
    private readonly DateTime _sentAt;
    // 応答テキストのローリングバッファ。2× tailChars を超えたら古い分を捨てる。
    private readonly StringBuilder _buf = new();

    private static readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    public ChatRecorder(string saveDir, int tailChars,
        string profileName, string mdiName, string command)
    {
        _saveDir  = saveDir;
        _tailChars = tailChars > 0 ? tailChars : 2000;
        _profileName = profileName;
        _mdiName  = mdiName;
        _command  = command;
        _sentAt   = DateTime.UtcNow;
    }

    /// <summary>ConPTY スレッドから呼ぶ。生の ANSI データを受け取り末尾を保持する。</summary>
    public void Feed(string ansiData)
    {
        var text = AnsiStripper.Strip(ansiData);
        _buf.Append(text);
        // バッファが上限の 2 倍を超えたら末尾 tailChars だけ残す
        if (_buf.Length > _tailChars * 2)
        {
            var keep = _buf.ToString(_buf.Length - _tailChars, _tailChars);
            _buf.Clear();
            _buf.Append(keep);
        }
    }

    /// <summary>UI スレッドから呼ぶ。JSON ファイルを書き出す。</summary>
    public void Complete()
    {
        string logDir;
        string fileName;
        try
        {
            var localSentAt = _sentAt.ToLocalTime();
            logDir = Path.Combine(_saveDir, "logs", localSentAt.ToString("yyyyMMdd"));
            Directory.CreateDirectory(logDir);

            var respondedAt = DateTime.UtcNow;
            var tail = _buf.Length <= _tailChars
                ? _buf.ToString()
                : _buf.ToString(_buf.Length - _tailChars, _tailChars);

            // ファイル名: <コマンド名(プロファイル名・サニタイズ済み)>-yyyyMMddTHHmmss-<4桁16進乱数>.json
            // 先頭にコマンド名を置くことで、フォルダ内を見ただけでどのコマンドの
            // やりとりか判別できるようにする。
            var rand = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..4].ToLowerInvariant();
            var stem = $"{SanitizeFileNamePart(_profileName)}-{localSentAt:yyyyMMddTHHmmss}-{rand}";
            fileName = stem + ".json";
            var path = Path.Combine(logDir, fileName);

            var record = new ChatRecord
            {
                Id           = stem,
                Profile      = _profileName,
                MdiName      = _mdiName,
                SentAt       = _sentAt,
                RespondedAt  = respondedAt,
                DurationMs   = (long)(respondedAt - _sentAt).TotalMilliseconds,
                Command      = _command,
                ResponseTail = tail,
            };

            File.WriteAllText(path,
                JsonSerializer.Serialize(record, _jsonOptions),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLogger.Error("ChatRecorder.Complete failed", ex);
            return;
        }

        AppendDailyIndex(logDir, fileName);
    }

    /// <summary>
    /// 同フォルダの index.json に「送信時刻・プロンプト1行目・対応する会話 JSON
    /// ファイル名」を1件追記する。1日のやりとりが少数である前提の素朴な
    /// 読み込み→追記→書き戻し。会話 JSON 本体の書き出しとは独立した失敗として扱う
    /// (index 更新が失敗しても会話 JSON 自体は既に書き出し済み)。
    /// </summary>
    private void AppendDailyIndex(string logDir, string fileName)
    {
        try
        {
            var indexPath = Path.Combine(logDir, "index.json");
            List<ChatDailyIndexEntry> entries = [];
            if (File.Exists(indexPath))
            {
                try
                {
                    entries = JsonSerializer.Deserialize<List<ChatDailyIndexEntry>>(
                        File.ReadAllText(indexPath, Encoding.UTF8)) ?? [];
                }
                catch (JsonException)
                {
                    // 壊れた index.json は諦めて新規作成 (会話 JSON 自体は無事)。
                    entries = [];
                }
            }

            entries.Add(new ChatDailyIndexEntry
            {
                SentAt  = _sentAt,
                Command = FirstLine(_command),
                File    = fileName,
            });

            File.WriteAllText(indexPath,
                JsonSerializer.Serialize(entries, _jsonOptions),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLogger.Error("ChatRecorder.AppendDailyIndex failed", ex);
        }
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOfAny(['\r', '\n']);
        return idx >= 0 ? s[..idx] : s;
    }

    private static string SanitizeFileNamePart(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}

/// <summary>JSON スキーマ。follow_up_questions は将来の複数質問管理用の受け口。</summary>
internal sealed class ChatRecord
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("mdi_name")]
    public string MdiName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("sent_at")]
    public DateTime SentAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("responded_at")]
    public DateTime RespondedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("response_tail")]
    public string ResponseTail { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("follow_up_questions")]
    public string[] FollowUpQuestions { get; set; } = [];
}

/// <summary>logs/yyyyMMdd/index.json の1エントリ。日次でその日に送信した
/// プロンプト一覧を一覧性高く確認するための索引 (会話本体は同フォルダの
/// 個別 JSON ファイル側にある)。</summary>
internal sealed class ChatDailyIndexEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("sent_at")]
    public DateTime SentAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("file")]
    public string File { get; set; } = "";
}
