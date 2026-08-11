# hook-cli Specification

## Purpose
TBD - created by archiving change establish-baseline-specs. Update Purpose after archive.
## Requirements
### Requirement: CLI 種別ごとの hook 設定ファイル解決
`HookCliRegistrar` は CLI 種別 (`ClaudeCode` / `Codex` / `CopilotCli` / `Antigravity`) ごとに固定の設定ファイルパス (`~/.claude/settings.json`、`~/.codex/config.toml`、`~/.copilot/hooks/amm-hooks.json`、`~/.antigravity/hooks/amm-hooks.json`) を解決しなければならない (SHALL)。`homeDir` を注入することでテスト時にユーザープロファイル実体へ触れずに検証できる。

#### Scenario: 未登録・ファイル不在の判定
- **WHEN** 対象設定ファイルが存在しない状態で `IsRegistered` を呼ぶ
- **THEN** `false` が返り、例外は発生しない

### Requirement: Claude Code への hook 登録
`Register(ClaudeCode, ...)` は `hooks.Stop` / `hooks.Notification` に `notify` サブコマンド (timeout 10 秒)、`hooks.PermissionRequest` に `approve` サブコマンド (timeout 60 秒) を自己ガード形式 (`cmd /c if exist "<exe>" "<exe>" ...`) で登録し、既存の hooks 設定やユーザー自身の他アプリの hook は保全しなければならない (SHALL)。同一 exe パスでの再登録は冪等 (重複エントリを作らない) とする。

#### Scenario: 既存のユーザー hook との共存
- **WHEN** `hooks.Stop` に既にユーザー自身の hook エントリが存在する状態で `Register` を呼ぶ
- **THEN** ユーザーのエントリは残したまま amm のエントリが追加され、配列要素数が 2 になる

#### Scenario: 旧形式 (ガードなし) エントリの置換
- **WHEN** ガードなしの旧形式コマンド (`"<old-exe>" notify --source claude`) が既に登録されている状態で新しい exe パスで `Register` を呼ぶ
- **THEN** 旧エントリは amm のエントリとして認識され、自己ガード形式・新パスの単一エントリに置換される (重複しない)

#### Scenario: Unregister は amm エントリのみ除去
- **WHEN** ユーザー自身の hook と amm の hook が同居する `hooks.Stop` に対し `Unregister` を呼ぶ
- **THEN** amm のエントリのみが除去され、ユーザーのエントリと配列は残る。amm しか無かったイベントキー (`Notification` 等) はキーごと削除される

### Requirement: Codex への notify / TUI 通知登録
`Register(Codex, ...)` は `~/.codex/config.toml` のルートスコープ (最初のテーブルヘッダより前) に単一の `notify` キーを `['<exe>', 'notify', '--source', 'codex']` として設定しなければならない (SHALL)。既存の `notify` キーが amm 以外を指している場合は上書きせず `InvalidOperationException` を送出する。あわせて、ユーザーが `[tui]` に `notifications` を未設定の場合、`notifications = ["agent-turn-complete", "approval-requested"]` と `notification_method = "osc9"` を `# added by amm` マーカー付きで `[tui]` セクションへ追記しなければならない (SHALL)（wait-detection の「OSC 9 ターミナル通知による attention 検知」要件が消費する通知チャンネルを Codex 側で有効化するための設定値であり、これ以外の値を書き込んではならない (SHALL NOT)）。ユーザーが既に `notifications` を設定している場合は一切変更しない。

#### Scenario: 他プログラムの notify との衝突
- **WHEN** `notify = ['my-notifier.exe']` が既に設定されている状態で `Register(Codex, ...)` を呼ぶ
- **THEN** 例外が送出され設定ファイルは変更されない

#### Scenario: ユーザー設定済み notifications の尊重
- **WHEN** `[tui]` に `notifications = true` が既に設定されている状態で `Register` を呼ぶ
- **THEN** `notify` キーは登録されるが `[tui]` のユーザー設定 (`notifications` / 有無に関わらず `notification_method` も) は変更されない

#### Scenario: Unregister は amm マーカー行のみ除去
- **WHEN** amm 追記後にユーザーが `[tui]` セクションへ独自キー (例: `theme`) を追加してから `Unregister` を呼ぶ
- **THEN** `notify` 行と `# added by amm` マーカー付き行のみが除去され、`[tui]` セクション自体とユーザーの独自キーは保持される

### Requirement: Copilot CLI / Antigravity への専有 hook ファイル登録
`Register(CopilotCli/Antigravity, ...)` は `~/.copilot/hooks/amm-hooks.json` (または Antigravity 相当) を amm 専有ファイルとして常に全体書き直しし、`agentStop` → `notify --state idle --source <cli>` (timeout 10 秒)、`permissionRequest` → `approve --source <cli>` (timeout 60 秒) を設定しなければならない (SHALL)。`Unregister` はファイルごと削除する。

#### Scenario: 専有ファイルの削除による解除
- **WHEN** Copilot CLI 向けに登録済みの状態で `Unregister` を呼ぶ
- **THEN** `amm-hooks.json` ファイル自体が削除され、`IsRegistered` は `false` を返す

### Requirement: 登録パスの陳腐化検出と更新
`GetRegisteredCommand` は登録済みエントリの exe パスを返し、現在の exe パスと異なる場合は呼び出し側 (UI) が再登録を促せるようにしなければならない (SHALL)。`Register` を新しいパスで呼び直すと既存 (旧パスの) amm エントリは重複を残さず新しいパスに置き換わる。

#### Scenario: exe 移動後の再登録
- **WHEN** 旧パスで登録済みの状態から新しい exe パスで `Register` を再度呼ぶ
- **THEN** 全 hook イベント (Claude: Stop/Notification/PermissionRequest、Codex: notify) が新パスの単一エントリに置き換わり、旧パスへの参照は残らない

