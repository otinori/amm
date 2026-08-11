# req-20260622-command-import-export — コマンド設定のインポート・エクスポート

> 対象ブランチ: `claude/command-features-spec-20260622`  
> ステータス: Draft

## 1. 概要

`SessionProfile`（コマンド設定）を複数選択して外部ファイルへエクスポートし、
別の AMM 環境やプロファイルにインポートする機能。

## 2. ファイルフォーマット

エクスポートファイルの拡張子は `.ammprofiles`。内容は既存の `.amm` JSON と互換性を持たせる:

```json
{
  "version": 1,
  "profiles": [
    { /* SessionProfile オブジェクト */ },
    { /* SessionProfile オブジェクト */ }
  ]
}
```

- `profiles` 配列は `.amm` ファイルの `profiles` キーと同じ `SessionProfile[]` 形式とする
- インポート時に `.amm` ファイルを直接指定しても同じ手順で処理できる（互換）

## 3. エクスポート

### R-E1 エントリポイント

`CommandTemplateDialog` の一覧表示エリアに **「エクスポート」** ボタンを追加する。

### R-E2 選択ダイアログ

- `ExportProfilesDialog` を新設する
- `CheckedListBox` で現在ロード中の全 `SessionProfile` を一覧表示する
- 各行の表示: `{Nickname}  ({CommandType})  —  {Command}`
- 「すべて選択」「すべて解除」ボタンを用意する
- OK で選択されたプロファイルのみをエクスポートする

### R-E3 保存ダイアログ

- `SaveFileDialog` を表示する
  - Filter: `AMM Profiles (*.ammprofiles)|*.ammprofiles|AMM File (*.amm)|*.amm|All files (*.*)|*.*`
  - InitialFileName: `profiles-export-{yyyy-MM-dd}.ammprofiles`
- 選択したプロファイルを JSON シリアライズして `AtomicFileWriter` 経由で保存する

### R-E4 機密フィールドの扱い

- `SessionProfile` の全フィールドをそのままエクスポートする（パスワード等の専用フィールドは現時点で存在しない）
- 将来的に機密フィールドが追加された場合は別途判断する

## 4. インポート

### R-I1 エントリポイント

`CommandTemplateDialog` の一覧表示エリアに **「インポート」** ボタンを追加する。

### R-I2 ファイル選択

- `OpenFileDialog` を表示する
  - Filter: `AMM Profiles (*.ammprofiles)|*.ammprofiles|AMM File (*.amm)|*.amm|All files (*.*)|*.*`
- ファイルを JSON デシリアライズして `profiles` 配列を取得する
- パース失敗時はエラーダイアログを表示して中断する

### R-I3 インポート選択ダイアログ

- `ImportProfilesDialog` を新設する
- `CheckedListBox` でファイル内の全 `SessionProfile` を一覧表示する
  - 各行の表示: `{Nickname}  ({CommandType})  —  {Command}`
  - 現在のプロファイルと Nickname が一致するエントリには `⚠ 重複` マークを付ける
- 「すべて選択」「すべて解除」ボタンを用意する
- 重複時の処理方針をラジオボタンで選択させる:
  - `スキップ`（既定）: 同名プロファイルは追加しない
  - `リネーム`: インポート側の Nickname に連番サフィックス `_2`, `_3`, … を付加して追加
  - `上書き`: 現在のプロファイルを置換する

### R-I4 インポート実行

1. OK 押下で選択されたプロファイルのみを処理する
2. 各プロファイルについて R-I3 の方針を適用する
3. 処理後、`AppSettings.Save()`（`AtomicFileWriter` 経由）を呼ぶ
4. `CommandTemplateDialog` の一覧を再読み込みして反映する

### R-I5 プロファイル検証

- `Nickname` が空または重複しない範囲で追加する（リネームまたはスキップの方針に従う）
- 未知フィールドは `JsonSerializer` の `PropertyNameCaseInsensitive` + 未知フィールド無視設定で読み飛ばす（将来バージョンとの互換）

## 5. UI レイアウト

```
┌─ コマンド設定 ─────────────────────────────────────┐
│  [一覧 CheckedListBox                          ]    │
│                                                     │
│  [追加] [編集] [削除]        [インポート] [エクスポート] │
└─────────────────────────────────────────────────────┘
```

既存の「追加 / 編集 / 削除」ボタン行の右端に「インポート」「エクスポート」を並べる。

## 6. 実装方針

### 6.1 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `src/AMMOperator/CommandTemplateDialog.cs` | 「インポート」「エクスポート」ボタン追加 + ハンドラ実装 |
| `src/AMMOperator/ExportProfilesDialog.cs` | **新規** エクスポート選択ダイアログ |
| `src/AMMOperator/ImportProfilesDialog.cs` | **新規** インポート選択 + 重複解決ダイアログ |
| `src/AMMOperator/AppSettings.cs` | `ImportProfiles()` / `ExportProfiles()` ヘルパーメソッド追加 |

### 6.2 `AppSettings` ヘルパー

```csharp
// エクスポート
public void ExportProfiles(IEnumerable<SessionProfile> selected, string filePath)
{
    var container = new ProfilesExportFile
    {
        Version = 1,
        Profiles = selected.ToList()
    };
    var json = JsonSerializer.Serialize(container, _jsonOptions);
    AtomicFileWriter.WriteAllText(filePath, json);
}

// インポート: ファイルからロード
public List<SessionProfile> LoadProfilesFromFile(string filePath)
{
    var json = File.ReadAllText(filePath);
    // .amm 互換: "profiles" キーが最上位に来る両形式を処理
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var arr = root.TryGetProperty("profiles", out var p) ? p : root;
    return JsonSerializer.Deserialize<List<SessionProfile>>(arr.GetRawText(), _jsonOptions)
        ?? new List<SessionProfile>();
}
```

### 6.3 `ProfilesExportFile` DTO

```csharp
public class ProfilesExportFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("profiles")]
    public List<SessionProfile> Profiles { get; set; } = new();
}
```

## 7. 除外事項

- **暗号化エクスポート**: 現時点では機密フィールドなし。将来必要になったら別設計。
- **クリップボード経由のインポート**: ファイル選択のみ対応。
- **差分マージ / バックアップ**: インポート前のプロファイル自動バックアップは実装しない（ユーザーが事前に手動エクスポートで代替）。

## 8. バックログ参照

`tasks/backlog.md` — 「コマンド設定インポート・エクスポート」エントリ参照。
