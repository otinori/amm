## MODIFIED Requirements

### Requirement: Codex への notify / TUI 通知登録
`Register(Codex, ...)` は `~/.codex/config.toml` のルートスコープ (最初のテーブルヘッダより前) に単一の `notify` キーを `['<exe>', 'notify', '--source', 'codex']` として設定しなければならない (SHALL)。既存の `notify` キーが amm 以外を指している場合は上書きせず `InvalidOperationException` を送出する。あわせて、ユーザーが `[tui]` に `notifications` を未設定の場合、`notifications = ["agent-turn-complete", "approval-requested"]` と `notification_method = "osc9"` を `# added by amm` マーカー付きで `[tui]` セクションへ追記しなければならない (SHALL)（wait-detection の「OSC 9 ターミナル通知による attention 検知」要件が消費する通知チャンネルを Codex 側で有効化するための設定値であり、これ以外の値を書き込んではならない (SHALL NOT)）。ユーザーが既に `notifications` を設定している場合は一切変更しない。

#### Scenario: 他プログラムの notify との衝突
- **WHEN** `notify = ['my-notifier.exe']` が既に設定されている状態で `Register(Codex, ...)` を呼ぶ
- **THEN** 例外が送出され設定ファイルは変更されない

#### Scenario: ユーザー設定済み notifications の尊重
- **WHEN** `[tui]` に `notifications = true` が既に設定されている状態で `Register` を呼ぶ
- **THEN** `notify` キーは登録されるが `[tui]` のユーザー設定 (`notifications` / 有無に関わらず `notification_method` も) は変更されない

#### Scenario: 未設定時に書き込まれる具体的な値
- **WHEN** `[tui]` に `notifications` が未設定の状態で `Register(Codex, ...)` を呼ぶ
- **THEN** `[tui]` セクションに `notifications = ["agent-turn-complete", "approval-requested"]` と `notification_method = "osc9"` が `# added by amm` マーカー付きで追記される

#### Scenario: Unregister は amm マーカー行のみ除去
- **WHEN** amm 追記後にユーザーが `[tui]` セクションへ独自キー (例: `theme`) を追加してから `Unregister` を呼ぶ
- **THEN** `notify` 行と `# added by amm` マーカー付き行のみが除去され、`[tui]` セクション自体とユーザーの独自キーは保持される
