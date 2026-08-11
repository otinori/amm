## ADDED Requirements

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

#### Scenario: 即時一致でちらつきなく確定
- **WHEN** `Feed` した直後の直近行がいずれかのパターンに一致する
- **THEN** 500ms のタイマ満了を待たずその場で `WaitingForInput` へ即時遷移する (一致しなければ `Running` へ遷移し 500ms の無出力タイマを開始する)

#### Scenario: 不一致時は Running を維持し Unknown へは落とさない
- **WHEN** 500ms の無出力タイマが満了してもパターンが一致しない、かつ無出力時間が既定の沈黙しきい値 (既定 4000ms) 未満
- **THEN** `Running` を維持したままタイマを再武装する (`Unknown` へは遷移させず、ちらつきを防ぐ)

#### Scenario: 長時間沈黙時の自力回復
- **WHEN** パターンが一致しないまま無出力時間が沈黙しきい値以上続く
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
MCP 経由の `mdi/wait_state` リクエストのように、状態遷移を非同期に待ち受けたい呼び出しのために、セッション単位の待機台帳を提供しなければならない (SHALL)。

#### Scenario: 待機の登録と解放
- **WHEN** `RegisterWait(sessionId, notifyToken, targetState, timeoutMs)` を呼ぶ
- **THEN** 完了前の `Task<WaitResult>` を返し、対応する `notifyToken` への `ResolveByToken` 呼び出し、または `timeoutMs` 経過、またはキャンセルトークン発火のいずれか最初に到達したもので解決する (既定タイムアウトは 300,000ms = 5 分)
- **AND** 同一エントリに対する複数の解放試行は最初の 1 回のみが有効になる (先勝ちで冪等)

#### Scenario: セッションクローズ時の一括解放
- **WHEN** MDI セッションが閉じられる、またはアプリが終了する
- **THEN** `ReleaseBySession` / `ReleaseAll` が該当する全ての保留中待機を `"timeout"` 状態で解決し、待ち手を無期限にブロックさせない
