# wait-detection Specification

## Purpose
TBD - created by archiving change establish-baseline-specs. Update Purpose after archive.
## Requirements
### Requirement: 既定入力待ちパターン
`WaitPatternDetector` は profile 側で `waitPatterns` が指定されない場合、以下の既定正規表現群を入力待ち判定に使わなければならない (SHALL)。

#### Scenario: 既定パターン一覧
- **WHEN** `waitPatterns` を指定せずに `WaitPatternDetector` を生成する
- **THEN** 次のパターンが (この順序の優先度に関わらず) 全て有効になる: `[\$#>]\s*$` (shell 末尾プロンプト) / `PS\s+\S+>\s*$` (PowerShell) / `(y/n)\s*$` (大小無視) / `password[:\s]*$` (大小無視) / `:\s*$` / `\?\s*$` / `^>(?:\s|$)` (Claude Code/Codex box プロンプト) / `^[❯›](?:\s|$)` (Inquirer.js/Copilot CLI 系) / `続行するには何かキーを押してください`

### Requirement: ユーザー指定パターンは既定パターンに追加合成される
profile の `waitPatterns` が空でない場合、その正規表現群は既定パターン群を置き換えるのではなく、両方を合成して使わなければならない (SHALL)。

#### Scenario: 末尾型のみの旧設定でも box プロンプトを検出
- **WHEN** profile の `waitPatterns` が `[">\\s*$"]` (末尾型のみ) の旧形式である
- **THEN** Claude Code/Codex の行頭型 box プロンプト (`│ > ... │`) も既定パターンとの合成により引き続き検出される

#### Scenario: ユーザー指定パターンの ReDoS 対策
- **WHEN** ユーザー指定の正規表現をコンパイルする
- **THEN** 100ms の `matchTimeout` を付与し (既定パターンは線形マッチのため無制限)、タイムアウト発生時は不一致として処理を継続する (検出スレッドを停止させない)

### Requirement: 出力バッファの前処理
`Feed` は ConPTY からの出力チャンクを行単位に分割し、ANSI エスケープ除去・装飾行除外・直近行バッファ保持を行ったうえでパターン照合しなければならない (SHALL)。

#### Scenario: ANSI 除去後に照合
- **WHEN** ANSI カラーコードを含む `PS C:\>` のような出力を Feed する
- **THEN** ANSI シーケンスを除去した文字列に対してパターン照合し、色コードの有無に関わらず一致判定できる

#### Scenario: 装飾のみの行を除外し直近50行を保持
- **WHEN** box drawing 罫線 (U+2500〜U+257F) または `|` のみで構成される行を受け取る
- **THEN** その行は「最後の非空行」候補・直近行バッファ両方から除外する
- **AND** 直近行バッファは最大 50 行を保持し、照合時は新しい順に走査したうえで各行の両端の空白とフレーム文字を交互に剥がしてから正規表現に掛ける (プロンプト box の下にヒント行が描画されるケースでも取りこぼさないため)

### Requirement: 状態遷移ロジック
`WaitPatternDetector` は `Unknown` / `Running` / `WaitingForInput` / `Stopped` の 4 状態を持ち、以下の規則で遷移しなければならない (SHALL)。

Tauri版は、旧.NET版が持っていた「500msの無出力タイマを毎回再武装し、一致しなければ別途4000msの沈黙しきい値で昇格させる」という2段階のちらつき防止窓を、単一の沈黙監視スレッドが判定する2終端状態(即時一致→即座にWaitingForInput、不一致のまま4000ms沈黙→自力回復でWaitingForInput)へ意図的に簡略化している(`wait_detect.rs`冒頭コメントで明記された既知の簡略化であり、見落としではない)。「500msのちらつき防止窓」というニュアンスは失われるが、実害は確認されていない。

#### Scenario: 即時一致で確定
- **WHEN** `Feed` した直後の直近行がいずれかのパターンに一致する
- **THEN** その場で `WaitingForInput` へ即時遷移する (一致しなければ `Running` を維持する)

#### Scenario: 長時間沈黙時の自力回復
- **WHEN** パターンが一致しないまま無出力時間が沈黙しきい値(既定 4000ms)以上続く
- **THEN** 「解析できないプロンプトで入力待ち」とみなし `WaitingForInput` へ遷移する (完了直後の末尾再描画でプロンプト行が照合窓から押し出され `Running` に固着する事象からの自力回復)

#### Scenario: プロセス終了
- **WHEN** `NotifyProcessExited()` が呼ばれる
- **THEN** 無条件で `Stopped` へ遷移し、以後の Feed に関わらずタイマは停止する

### Requirement: hook 駆動の外部状態通知 (イベント駆動検知)
正規表現スクレイピングの補完として、CLI 自身の hook が申告する状態を無条件に信頼する強制遷移経路 (`ForceState`) を持たなければならない (SHALL)。CLI ごとのペイロード差異は `NotifyPayloadMapper` が `idle` / `attention` / `busy` の 3 語彙へ正規化する。

#### Scenario: 状態語彙のマッピング
- **WHEN** CLI hook から届いたペイロードを `NotifyPayloadMapper.MapState` に渡す
- **THEN** Claude Code の `hook_event_name="Stop"` および `notification_type` が `idle_prompt`/`agent_idle`/`agent_completed` のものは `idle`、`permission_prompt`/`elicitation_dialog` は `attention`、Codex の `type="agent-turn-complete"` は `idle` にマップされる
- **AND** ペイロードが `null`、またはイベント種別を示すキー (`hook_event_name`/`type`) を含まない場合は `idle` にフォールバックし、`shell_completed` 等の非該当 `notification_type` は `null` (無視) を返す

#### Scenario: idle/attention/busy から WaitState への反映と Stopped からの不可逆性
- **WHEN** `idle` または `attention` の通知を受ける
- **THEN** `ForceState(WaitState.WaitingForInput)` を呼び、`attention` の場合は追加で `HasAttention` フラグを立てる
- **WHEN** `busy` の通知を受ける
- **THEN** `ForceState(WaitState.Running)` を呼び `HasAttention` を降ろす
- **AND** 現在の状態が既に `Stopped` の場合、いかなる `ForceState` 呼び出しも状態を変化させない (プロセス終了後の復活は不可)

### Requirement: 非同期待機ブローカー (WaitBroker)
MCP 経由の `pane/wait_state` リクエストのように、状態遷移を非同期に待ち受けたい呼び出しのために、セッション単位の待機台帳を提供しなければならない (SHALL)。

#### Scenario: 待機の登録と解放
- **WHEN** `RegisterWait(sessionId, notifyToken, targetState, timeoutMs)` を呼ぶ
- **THEN** 完了前の `Task<WaitResult>` を返し、対応する `notifyToken` への `ResolveByToken` 呼び出し、または `timeoutMs` 経過、またはキャンセルトークン発火のいずれか最初に到達したもので解決する (既定タイムアウトは 300,000ms = 5 分)
- **AND** 同一エントリに対する複数の解放試行は最初の 1 回のみが有効になる (先勝ちで冪等)

#### Scenario: セッションクローズ時の一括解放
- **WHEN** ペインのセッションが閉じられる、またはアプリが終了する
- **THEN** `ReleaseBySession` / `ReleaseAll` が該当する全ての保留中待機を `"timeout"` 状態で解決し、待ち手を無期限にブロックさせない

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

