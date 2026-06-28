using System.Text;
using System.Text.Json;

namespace Amm.Core;

/// <summary>
/// 1コマンド分のチャット（送信テキスト＋応答末尾）を JSON ファイルへ書き出す。
/// TerminalChildForm が生成・保持し、コマンド送信時に生成、
/// WaitingForInput 遷移時に Complete() を呼ぶ。
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
        new() { WriteIndented = true };

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
        try
        {
            Directory.CreateDirectory(_saveDir);

            var respondedAt = DateTime.UtcNow;
            var tail = _buf.Length <= _tailChars
                ? _buf.ToString()
                : _buf.ToString(_buf.Length - _tailChars, _tailChars);

            // ファイル名: yyyyMMddTHHmmss-<4桁16進乱数>.json
            var rand = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..4].ToLowerInvariant();
            var stem = $"{_sentAt.ToLocalTime():yyyyMMddTHHmmss}-{rand}";
            var path = Path.Combine(_saveDir, stem + ".json");

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
        }
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
