# req-20260622-auto-send-idle — アイドル時自動送信

> 対象ブランチ: `claude/command-features-spec-20260622`  
> ステータス: Draft

## 1. 概要

ToolUse / 許可待ちではない純粋な入力待ち (`WaitingForInput && !HasAttention`) に遷移した際、
コマンド設定にあらかじめ登録したプロンプトを自動送信する機能。

## 2. 要求

### R-1 トリガー条件

- `WaitState == WaitingForInput` かつ `HasAttention == false`（ToolUse / 許可待ちでない）
- 前回の遷移が `Running` → `WaitingForInput`（初回遷移のみ発火、同一 idle 滞在中の再送禁止）

### R-2 per-profile 設定項目

`SessionProfile` に以下を追加する:

```json
"autoSendOnIdle": {
  "enabled": false,
  "prompt": "",
  "delayMs": 3000
}
```

| フィールド | 型 | 既定値 | 説明 |
|---|---|---|---|
| `enabled` | bool | `false` | 機能の ON/OFF |
| `prompt` | string | `""` | 送信するプロンプト文字列 |
| `delayMs` | int | `3000` | 遷移後の待機時間（ms）|

### R-3 自動送信の動作シーケンス

1. `WaitingForInput && !HasAttention` に遷移
2. `delayMs` のカウントダウンタイマーを開始
3. タイマー満了前に以下の**キャンセル条件**のいずれかが成立した場合 → タイマーを破棄（送信しない）
   - `HasAttention` が `true` になった（ToolUse / 許可待ち発生）
   - 状態が `Running` に変化した
   - ユーザーが MDI ペインのキャンセルボタンを押した（R-4 参照）
4. タイマー満了 → `SendInput(prompt)` を発行
5. 送信後、タイマーを**破棄**（次の Running→Idle 遷移まで再発火しない）

### R-4 キャンセル UI

- `delayMs` カウントダウン中、MDI ペインのタイトルバーに
  `⏱ 3s 後に自動送信 [✕]` のような通知を表示する
- `[✕]` クリックでタイマーをキャンセルする
- カウントダウンが進むにつれ残秒数を更新表示する（1秒粒度で十分）
- `delayMs` が 0 以下の場合はカウントダウン表示なしで即送信する

### R-5 UI 設定箇所

`CommandTemplateDialog` の「動作」タブ（または既存の詳細設定エリア）に
**「アイドル時自動送信」** セクションを追加する:

- チェックボックス: `アイドル時（ToolUse なし）に自動送信する`
- テキストボックス（複数行可）: 送信プロンプト（チェックが OFF のときは無効化）
- スピナー: 遅延時間（秒、既定 3 秒、範囲 0〜60）

### R-6 安全ガード

- `prompt` が空文字の場合は `enabled` が `true` でも送信しない（設定誤りへの保護）
- 連続オートリピートが起きないよう、1 回送信後は次の `Running` 遷移まで絶対に再発火しない
- 手動送信・MCP 経由の `send_message` も `Running` 遷移とみなしてタイマーをリセットする

## 3. 実装方針

### 3.1 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `src/AMMOperator/SessionProfile.cs` | `AutoSendOnIdle` クラス + `SessionProfile.AutoSendOnIdle` プロパティ追加 |
| `src/AMMOperator/TerminalChildForm.cs` | WaitState 遷移監視 + タイマー制御 + タイトルバー表示更新 |
| `src/AMMOperator/CommandTemplateDialog.cs` | 「アイドル時自動送信」セクション UI 追加 |

### 3.2 `SessionProfile` の追加クラス

```csharp
public class AutoSendOnIdleSettings
{
    public bool Enabled { get; set; } = false;
    public string Prompt { get; set; } = "";
    public int DelayMs { get; set; } = 3000;
}

// SessionProfile クラス内:
public AutoSendOnIdleSettings AutoSendOnIdle { get; set; } = new();
```

### 3.3 `TerminalChildForm` の状態監視

```csharp
private System.Threading.Timer? _autoSendTimer;
private bool _autoSendArmed = false; // Running→Idle ごとに true、送信後 false

private void OnWaitStateChanged(WaitState prev, WaitState next)
{
    var cfg = Profile.AutoSendOnIdle;

    // Running → WaitingForInput 遷移: タイマー開始
    if (prev == WaitState.Running && next == WaitState.WaitingForInput
        && !HasAttention && cfg.Enabled && cfg.Prompt.Length > 0)
    {
        _autoSendArmed = true;
        StartAutoSendCountdown(cfg);
    }

    // Running 遷移でリセット
    if (next == WaitState.Running)
    {
        CancelAutoSendTimer();
        _autoSendArmed = false;
    }
}

private void OnHasAttentionChanged(bool attention)
{
    if (attention) CancelAutoSendTimer();
}

private void StartAutoSendCountdown(AutoSendOnIdleSettings cfg)
{
    if (cfg.DelayMs <= 0)
    {
        ExecuteAutoSend(cfg.Prompt);
        return;
    }
    UpdateTitleBarCountdown(cfg.DelayMs / 1000);
    _autoSendTimer = new System.Threading.Timer(_ =>
    {
        if (!_autoSendArmed) return;
        BeginInvoke(() =>
        {
            if (!_autoSendArmed) return;
            CancelAutoSendTimer();
            ExecuteAutoSend(cfg.Prompt);
        });
    }, null, cfg.DelayMs, Timeout.Infinite);
    // 1秒ごとに残秒表示を更新（省略可）
}

private void CancelAutoSendTimer()
{
    _autoSendTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    _autoSendTimer?.Dispose();
    _autoSendTimer = null;
    ClearTitleBarCountdown();
}

private void ExecuteAutoSend(string prompt)
{
    _autoSendArmed = false;
    SendInput(prompt + "\n"); // 既存の SendInput を呼ぶ
}
```

## 4. 除外事項（明示的な非対応）

- **`HasAttention == true` の時の自動送信**: ToolUse 承認待ちへの自動応答は本機能の対象外。
  別途 Approval Hub（UDR-9c4）の延長として実装する。
- **グローバル設定**: 自動送信設定は per-profile のみ。アプリ全体の既定値設定は実装しない。
- **送信履歴**: 自動送信の実行ログは通常の sessionLog に記録されるため専用ログは不要。

## 5. バックログ参照

`tasks/backlog.md` — 「アイドル時自動送信」エントリ参照。
