## ADDED Requirements

### Requirement: 強制状態通知は自動検知の巻き戻しから保護される
`ForceState` による強制遷移の後、自動検知（沈黙タイマー・パターン照合による遷移判定）は、新しい ConPTY 出力（`Feed` 呼び出し）が実際に到着するまでその強制状態を上書きしてはならない (SHALL NOT)。無出力時間の起算点（沈黙判定用のタイムスタンプ）は `ForceState` 呼び出し時点にリセットされなければならない (SHALL)。

#### Scenario: busy 通知直後は沈黙タイマーで巻き戻らない
- **WHEN** `ForceState(Running)`（`busy` 通知経由）を呼んだ直後、実際には数秒間 ConPTY からの新規出力が無い
- **THEN** 沈黙しきい値（既定4000ms）に相当する時間が経過しても、新しい `Feed` 呼び出し（実出力）が届くまでは `WaitingForInput` へ自動遷移しない

#### Scenario: busy 通知後に新規出力が届けば通常の検知に戻る
- **WHEN** `ForceState(Running)` の後、実際に新しい ConPTY 出力が `Feed` される
- **THEN** 以後は通常のパターン照合・沈黙タイマーによる自動検知が再開される

### Requirement: OSC 9 ターミナル通知による attention 検知
CLI が自身のプロセス内から OSC 9 エスケープシーケンスで発行する通知は、hook 経由の外部状態通知と独立した attention 検知経路として扱わなければならない (SHALL)。ターミナル表示（xterm.js 等）は OSC 9 シーケンスを解析し、ペイロード内容に応じて `ForceState` 相当の通知へ転送しなければならない (SHALL)。この経路は、hook による通知手段を持たない CLI（例: Codex の TUI 通知）にとって attention 検知の主要経路となりうる。

#### Scenario: OSC 9 通知の attention 判定
- **WHEN** ConPTY 出力に OSC 9 エスケープシーケンスが含まれ、そのペイロードが承認待ち・確認待ちを示す内容である
- **THEN** 当該ペインに対し `idle` かつ `HasAttention=true` 相当の強制状態通知を発行する（hook 駆動の `amm/notify` と同一のマッピング結果になる）

#### Scenario: 承認待ちを示さない OSC 9 通知は無視される
- **WHEN** OSC 9 シーケンスのペイロードが承認待ち・確認待ちを示す内容を含まない
- **THEN** 状態通知は発行されない（通常のパターン照合・沈黙検知に委ねる）
