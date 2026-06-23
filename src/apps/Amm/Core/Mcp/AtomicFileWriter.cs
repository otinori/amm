using System.Text;

namespace Amm.Core.Mcp;

/// <summary>
/// 設定ファイルを安全に上書きするユーティリティ。
///
/// 旧実装は <c>File.WriteAllText(tmp)</c> → <c>File.Move(tmp, path, overwrite:true)</c>
/// だったが、(1) tmp 書き込み後にディスクへ flush していないため電源断/クラッシュ時に
/// tmp の内容がディスク未到達のまま rename だけ成立し既存設定が空/部分内容で失われる窓が
/// あり、(2) <c>~/.claude.json</c> のように MCP 以外の設定も同居する大きなファイルでは
/// 喪失の影響が大きい。本実装は tmp を <c>Flush(flushToDisk:true)</c> でディスクへ確定させ、
/// 既存ファイルがある場合は <c>File.Replace</c> でトランザクション的に置換する。
/// </summary>
internal static class AtomicFileWriter
{
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // tmp は予測不能なランダム名にする。固定名 (旧 *.amm-tmp) は (1) 並行書込で
        // 衝突し、(2) 途中失敗時に機密内容のまま予測可能な名前で残留しうる。対象と
        // 同一ディレクトリに作り、置換は同一ボリューム内のアトミック操作で行う。
        var dirForTmp = string.IsNullOrEmpty(dir) ? "." : dir;
        var tmp = Path.Combine(dirForTmp, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // File.Replace は既存ファイルを置換しつつ、失敗時も元ファイルを保全する。
                // ignoreMetadataErrors=true で ACL 等のメタデータ複製失敗を致命化しない。
                File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            // 成功時 (Replace/Move) は tmp は消えているが、書込/置換の途中で例外が出た
            // 場合に機密内容の一時ファイルを残さないよう best-effort で削除する。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }
}
