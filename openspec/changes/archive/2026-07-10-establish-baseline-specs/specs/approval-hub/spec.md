## ADDED Requirements

### Requirement: Level 1 — 許可要求の通知のみ (Attention 表示)
CLI が確認要求 (Notification 系イベント) を発火したとき、amm は該当ペインに `HasAttention` フラグを立て、タイトルバー/ボタンのオレンジ表示とタイトル絵文字 (⚠) で人間に知らせなければならない (SHALL)。回答自体はペイン内で行い、amm は代答しない。

#### Scenario: 非フォアグラウンド時の通知
- **WHEN** amm がフォアグラウンドでない状態でペインが attention 状態になる
- **THEN** タスクバーが `FlashWindowEx` で点滅し、ペインをアクティブ化するか idle 通知が来るまで attention 表示が解除されない

### Requirement: Level 2 — 許可要求の集約回答台帳
`ApprovalBroker` は CLI からの許可要求を `token` (ペイン識別子) / `toolName` / `toolInputJson` 付きの台帳エントリとして保持し、GUI からの回答 (`"allow"` / `"deny"`) または解放 (`null`) を待つ非同期 API (`RequestAsync`) を提供しなければならない (SHALL)。1 リクエスト = 1 エントリとして、複数ペイン・同一ペインの並列要求を独立に扱う。

#### Scenario: 回答による解決
- **WHEN** GUI が特定エントリを `Resolve(id, "allow")` で解決する
- **THEN** 対応する `RequestAsync` の Task が `"allow"` で完了し、エントリは台帳から除去される

#### Scenario: 二重解決は先勝ち
- **WHEN** 既に解決済みのエントリ ID に対し再度 `Resolve` を呼ぶ
- **THEN** 2 回目の呼び出しは `false` を返し、最初の解決結果のみが有効になる

### Requirement: 4 種の解放トリガー
台帳エントリは (1) 人間の回答、(2) 明示解放 (`Resolve(id, null)` / `ReleaseByToken` / `ReleaseAll`)、(3) 既定 45 秒のタイムアウト、(4) パイプ切断 (`CancellationToken`) のいずれかで必ず解放されなければならない (SHALL)。決定なし (`null`) で解放された要求は、hook がペイン内プロンプトへフォールバックする合図として扱われる。

#### Scenario: タイムアウトによる解放
- **WHEN** 45 秒 (既定) 経過しても人間が回答しない
- **THEN** エントリは `null` 決定で解放され台帳から除去される

#### Scenario: パイプ切断による解放
- **WHEN** hook プロセス消滅等でセッションの `CancellationToken` がキャンセルされる
- **THEN** 該当エントリは `null` 決定で解放され、幽霊要求としてポップアップに残らない

#### Scenario: ペイン単位の一括解放
- **WHEN** 特定ペイン (token) がアクティブ化またはクローズされる
- **THEN** `ReleaseByToken` によりそのペイン由来の保留エントリのみが `null` 決定で解放され、他ペインの要求には影響しない

### Requirement: 非モーダル集約ポップアップ UI
許可要求の集約 UI (`ApprovalPopupForm`) はフォーカスを奪わない非アクティブ化ウィンドウ (`WS_EX_NOACTIVATE`) として表示され、複数要求がある場合はウィンドウを増やさず「1/N 件」表示で先頭から順に扱わなければならない (SHALL)。表示直後 500ms は誤操作防止のため許可/拒否/移動/閉じるボタンを無効化する。

#### Scenario: 複数要求のキュー表示
- **WHEN** 3 件の許可要求が同時に保留されている状態でポップアップが更新される
- **THEN** 先頭の 1 件のみが表示され「⚠ (1/3 件)」のラベルが付く

#### Scenario: 表示直後のクリックガード
- **WHEN** ポップアップが新しい要求で表示されてから 500ms 以内にボタンをクリックしようとする
- **THEN** ボタンは無効化されておりクリックは効果を持たない

### Requirement: amm-mcp.exe approve サブコマンドの決定出力契約
`amm-mcp.exe approve` は環境変数 `AMM_NOTIFY_ID` が無い場合は no-op で終了し、決定 (allow/deny) を得た場合のみ `--source` に応じた CLI 別フォーマット (Claude Code: `hookSpecificOutput.decision.behavior`、Copilot CLI: `behavior` 直書き) の JSON を標準出力に書かなければならない (SHALL)。無回答時は何も出力せず終了コード 0 とする。

#### Scenario: Claude Code 向け allow 出力
- **WHEN** `--source claude` (または省略) で allow 決定を受け取る
- **THEN** `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}` を標準出力に書く

#### Scenario: Copilot CLI 向け deny 出力
- **WHEN** `--source copilot` で deny 決定を受け取る
- **THEN** `{"behavior":"deny","message":"..."}` を標準出力に書く

#### Scenario: 無回答時は沈黙
- **WHEN** 45 秒の台帳タイムアウトにより決定なしで解放される
- **THEN** `amm-mcp.exe approve` は標準出力に何も書かず終了コード 0 で終了し、CLI 側は通常のペイン内プロンプトにフォールバックする
