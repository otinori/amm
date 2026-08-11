## Context

.NET版(`Forms/MdiParentForm.cs`内`EditorBridge`クラス、`Forms/TerminalChildForm.cs`、`Core/Win32SystemMenu.cs`)の実装を読み、以下の挙動を移植元として確定した:

- `EditorBridge.CreateAndLaunch`: `%LOCALAPPDATA%\amm\editor\prompt-<profile>[(n)]-<shortid>.md` を作成し、入力欄の内容があればそれを初期本文に、なければ使い方コメントのみの本文にして、設定に応じたエディタ(Notepad/カスタムexe/既定の関連付けアプリ)を起動する
- `FileSystemWatcher`(`NotifyFilters.LastWrite | NotifyFilters.Size`) + 500msデバウンスタイマーで保存を検知し、内容を読み取って`ApplyPerCommandFilter`(Tauri版の`filter_lines_for_send`相当)を適用後、通常の入力欄送信と同じ経路(`DispatchSendAsync`)で対象ペインへ送信する。空文字列・前回送信内容と同一ハッシュの場合は送信しない
- 送信後アクション(`_editorPostSendAction`: Focus/Maximize/None)に従い対象ペインを操作する
- 対象ペインが閉じたら(`FormClosed`)ブリッジを破棄し一時ファイルを削除する
- 「エディタ連携ファイルパスをコピー」は同じブリッジ(既存があれば再利用、なければ新規作成)を使い、エディタ起動の代わりに一時ファイルパスをクリップボードへコピーする
- エディタ設定(`_editorMode`/`_customEditorPath`/`_editorPostSendAction`)はMDIウィンドウ単位ではなく**アプリケーション全体設定**で、.NET版では`layout.json`相当の永続化ファイルに保存される

Tauri版には.NET版の`layout.json`に相当するアプリ全体設定ファイルが存在しない(ウィンドウ/ペインレイアウトは`localStorage`で管理)。既存の類似ケースとして`tray-icon`capabilityの通知トグル設定が、exe隣接の`tray-settings.json`という小さな専用JSONファイルに保存されている前例がある(`native_ui.rs`の`tray_settings_path`/`TraySettingsFile`)。

## Goals / Non-Goals

**Goals:**
- Ctrl+E / ペインメニュー「エディタ連携」から一時ファイル経由で外部エディタを起動し、保存するたびに対象ペインへ自動送信する
- 「エディタ連携ファイルパスをコピー」でクリップボードへ一時ファイルパスをコピーする
- エディタ選択(Notepad/カスタム/既定の関連付けアプリ)と送信後アクション(フォーカス/最大化/何もしない)をアプリ全体設定として保持・変更できる

**Non-Goals:**
- プロファイル単位でのエディタ設定切り替え(.NET版もアプリ全体設定であり、プロファイル単位化は新規要件のため対象外)
- 一時ファイルの内容を対象ペインの出力(応答テキスト)と同期させる機能(.NET版にも無い、"下書き→送信"の片方向のみ)
- エディタの複数同時起動制御の高度化(.NET版と同様、1ペインにつき1ブリッジまで、既存ブリッジがあれば再利用するだけの単純な仕組みを踏襲)

## Decisions

### D1: ファイル監視はポーリング方式を採用する(`notify` crateを新規依存に追加しない)

Tauri版は既に`profile.rs::spawn_profiles_hot_reload`で「300msごとのmtimeポーリング、2回連続で同一mtimeなら安定とみなして適用」という自前ポーリング方式を採用しており、`FileSystemWatcher`相当の外部クレート(`notify`等)を使っていない。本機能でも同じ方式を踏襲する: バックグラウンドスレッドが対象一時ファイルのmtimeを250msごとにポーリングし、前回ポーリング時と異なるmtimeを検知したら500msのデバウンス猶予(.NET版のデバウンスタイマーと同じ待ち時間)を置いてから内容を読み取り送信する。

**代替案として検討**: `notify` crateによるイベント駆動監視。より低レイテンシ・低CPU負荷だが、新規外部依存が増え、既存コードベースの「ポーリングで統一する」慣習から外れる。エディタ連携は保存頻度が数秒〜分単位の人間操作が起点であり、250msポーリングの遅延は体感上問題にならないため、慣習の一貫性を優先してポーリング方式を採用する。

### D2: エディタ全体設定は `%LOCALAPPDATA%\amm\editor-settings.json` に保存する

**実装時に方針変更(タスク1.1着手時)**: proposalの初期案は`tray-icon`capabilityの`tray-settings.json`(exe隣接)への追随だったが、`input_history.rs`の`history_path()`に明記された既存の設計根拠(「標準的な非admin MSI/NSISインストールは`C:\Program Files\`配下になり、非adminユーザーはexe隣接に書き込めないため、exe隣接だとsave()が無言で失敗し設定が永続化されない」)がより優先すべき既存判断だと判明した。この理由は新規追加する設定ファイルにも等しく当てはまるため、`gateway.rs`の`mcp-servers.json`・`profile.rs`の`trusted-profiles.json`・`input_history.rs`の`history.json`と同じ`%LOCALAPPDATA%\amm\`配下に`editor-settings.json`(`{"editorMode": "Associated"|"Notepad"|"Custom", "customEditorPath": string, "postSendAction": "Focus"|"Maximize"|"None"}`)を置く方針に変更する。`tray-settings.json`のexe隣接は追随すべき前例ではなく、非adminインストール下で無言失敗しうる既存の潜在バグと判断し、新規ファイルではその失敗モードを踏襲しない。

### D3: 設定UIは既存の「AMM 設定...」ダイアログに項目を追加する

新規ダイアログを作らず、`openAmmSettingsDialog`(既存、`profile-schema`capability管轄、CloseProhibited/CollapseBlankLines/CommentPrefixesの3フィールドを持つ)にエディタ設定の3フィールド(エディタモード ラジオ/セレクト、カスタムパス、送信後アクション)を追加する。理由: 新規モーダルを増やすより、既存の「アプリ設定」という文脈が近いダイアログにまとめる方がユーザーの発見性が高い。ただしこのダイアログの永続化対象は`profiles.amm`(D2で決めた`editor-settings.json`とは別ファイル)であるため、ダイアログのOK処理は2つのファイルへ書き分ける必要がある(実装上の注意点、タスク側で明示する)。

### D4: 一時ファイルのクリーンアップは「対象ペインのclose」イベントにフックする

.NET版の`FormClosed`相当として、Tauri版の`closePane()`(`pane-lifecycle.js`)にブリッジ破棄・一時ファイル削除の呼び出しを追加する。アプリ終了時(`onCloseRequested`)は個別ペインcloseを経由しないため、`beforeunload`相当の全ペイン一括クリーンアップも必要(既存の`runGitGuardForAllPanes`と同様、全ペイン走査で対応)。

## Risks / Trade-offs

- **[Risk] ポーリング方式のレイテンシ**: 250msポーリング+500msデバウンスで、保存から送信まで最大750ms程度の遅延が生じる → .NET版もデバウンス500ms込みで同等の遅延があり、体感上の後退ではないため許容する
- **[Risk] エディタプロセスの起動失敗(カスタムパス不在等)**: .NET版と同様、カスタムパス不在時は関連付けアプリへフォールバックする。フォールバックも失敗した場合はエラーダイアログ表示に留める(.NET版に例外時の高度なリカバリはない)
- **[Risk] 一時ファイルの残留**: アプリが異常終了した場合、`%LOCALAPPDATA%\amm\editor\`配下に一時ファイルが残る可能性がある(.NET版も同じ制約)。次回起動時のクリーンアップは本変更のスコープ外とする(.NET版にも無い挙動のため、パリティ目的では不要)
- **[Trade-off] アプリ全体設定という設計を維持**: プロファイル単位にした方がTauri版の設計(プロファイル駆動)に馴染むが、.NET版からの挙動変更になり「パリティ実装」の趣旨から外れるため、今回はアプリ全体設定のまま移植する

## Open Questions

- なし(.NET版ソースの実地確認により実装方針は確定済み)
