# req-20260622: MDI ウィンドウ制御（オートパイロット強化）

- **ID**: req-20260622-mdi-window-control
- **状態**: Draft
- **関連 UDR**: -
- **更新日**: 2026-06-22

## 背景 / 目的

amm の MDI ウィンドウ（TerminalChildForm）は現在 **ユーザー操作のみ** で開閉する。
AI エージェントや PowerShell スクリプトがウィンドウを **プログラムから制御** できれば、
「エージェントが自分でセッションを生やして並列タスクを割り当て、完了後に閉じる」
オートパイロット動作が実現できる。

本 spec では以下の 2 インタフェースに `mdi/open` / `mdi/close` / `mdi/wait_state` 機能を追加する:

1. **amm-mcp.exe** — MCP ツールとして公開 (`mdi/open`, `mdi/close`, `mdi/wait_state`)
2. **Amm.PowerShell.dll** — PS cmdlet として公開 (`Open-AmmWindow`, `Close-AmmWindow`, `Wait-AmmIdle`)
   ※ PS モジュール全体の設計は [req-20260622-ps-module.md](./req-20260622-ps-module.md) を参照

```
[CLI エージェント / PS スクリプト]
    ↓ MCP ツール呼び出し / PS cmdlet
[amm-mcp.exe または Amm.PowerShell.dll]
    ↓ Named Pipe JSON-RPC
      amm.openWindow / amm.closeWindow / amm.waitState
[amm.exe — UI スレッド Invoke / WaitBroker]
    ↓
TerminalChildForm の生成 / 破棄 / 状態待機
```

## 要求（What）

### R-1: MDI ウィンドウのオープン
- `mdi/open` ツール（および `Open-AmmWindow` cmdlet）を呼び出すと
  新しい TerminalChildForm が作成・フォアグラウンド表示される。
- パラメータ:

  | 名前 | 型 | 必須 | 説明 |
  |---|---|---|---|
  | `command` | string | 必須 | 起動するコマンド（例: `claude`, `cmd.exe`） |
  | `args` | string[] | 任意 | コマンド引数 |
  | `title` | string | 任意 | ウィンドウタイトル（未指定時はコマンド名） |
  | `profile` | string | 任意 | `.amm` プロファイル名（省略時はデフォルト） |
  | `workingDirectory` | string | 任意 | 初期作業ディレクトリ |

- 戻り値: `{ session_id: string }` — 以後の操作に使う一意 ID

### R-2: MDI ウィンドウのクローズ
- `mdi/close` ツール（および `Close-AmmWindow` cmdlet）を呼び出すと
  指定 session_id の TerminalChildForm が閉じられる。
- パラメータ:

  | 名前 | 型 | 必須 | 説明 |
  |---|---|---|---|
  | `session_id` | string | 必須 | `mdi/open` で返された ID |
  | `force` | bool | 任意 | true の場合、確認ダイアログなしで強制閉鎖 |

- `session_id` が存在しない場合はエラーを返す（例外は throw しない）。

### R-3: Named Pipe プロトコル拡張
- `amm-mcp.exe` → `amm.exe` 間の既存 Named Pipe に以下の JSON-RPC メソッドを追加する:
  - `amm.openWindow(params)` → `{ session_id }` または `{ error }`
  - `amm.closeWindow({ session_id, force? })` → `{ success }` または `{ error }`
  - `amm.waitState({ session_id, target_state, timeout_ms? })` → `{ state, elapsed_ms }` または `{ error }`
- 既存メソッド（`amm.send`, `amm.listParticipants` 等）は変更しない。
- `amm.exe` 側は受信後 `Control.Invoke` で UI スレッドに切り替えて操作する（waitState は非ブロッキング登録）。

### R-4: セッション ID 管理
- `session_id` は amm.exe が生成する GUID 文字列とする。
- amm.exe は起動中の TerminalChildForm を session_id でルックアップできる
  内部ディクショナリを持つ。
- TerminalChildForm がユーザー操作で閉じられた場合もエントリを削除する。

### R-5: 既存インターフェースとの互換
- 既存の `send_message` / `list_participants` 等のツールは変更なし。
- `amm-mcp.exe` のコマンドラインインターフェースは変更しない。

### R-6: セッション状態の待機 (`mdi/wait_state`)
- `mdi/wait_state` ツール（および `Wait-AmmIdle` cmdlet）を呼び出すと、
  指定 session_id のセッションが目標状態に遷移するまで **レスポンスをブロック** する。
- パラメータ:

  | 名前 | 型 | 必須 | 説明 |
  |---|---|---|---|
  | `session_id` | string | 必須 | 監視対象セッションの ID |
  | `target_state` | `"idle"` \| `"attention"` | 必須 | 待機する状態 |
  | `timeout_ms` | int | 任意 | タイムアウト ms（既定: 300000 = 5 分） |

- 戻り値:
  - `{ state: "idle" | "attention", elapsed_ms: int }` — 状態に到達した場合
  - `{ state: "timeout", elapsed_ms: int }` — タイムアウトした場合
  - `{ error: string }` — session_id 不明 / amm.exe 未起動

- **実装方針**: `ApprovalBroker` と同パターンの `WaitBroker` を新設する。
  - `amm.waitState` 受信時に `WaitBroker.RegisterWait(session_id, target_state, timeout_ms)` して応答を保留。
  - 既存の `amm/notify` ハンドラが受信した `state` に一致する pending wait を
    `WaitBroker.Resolve(session_id, state)` で解放し、応答を返す。
  - タイムアウト経過時は `{ state: "timeout" }` を返す（クラッシュしない）。
  - セッションクローズ / amm-mcp.exe 切断時はすべての pending wait を自動解放する。

- **MCP ブロッキング特性**: MCP stdio プロトコルはリクエスト単位でレスポンス待ちが前提のため、
  エージェントが `mdi/wait_state` を呼んでいる間は他のツール呼び出しを行わない。
  並列待機が必要な場合は複数の `amm-mcp.exe` 接続（= 複数プロセス）を使う。

## 非対象（Out of scope）

- **ウィンドウ一覧の取得** (`mdi/list`): R-1〜R-6 が安定してから別 spec で追加する。
- **ウィンドウへの入力送信**: 既存 `send_message` ツールで代替可能。
- **MDI レイアウト制御**（タイル・カスケード等）: UI 専用操作として別途検討。
- **リモートからの接続**: Named Pipe は同一ユーザー OS プロセスのみ（既存信頼モデルと同じ）。
- **状態変化のストリーミング**: 初期実装は単発待機のみ（MCP streaming は将来検討）。

## 受け入れ基準

- [ ] CLI エージェントが `mdi/open` を呼ぶと新しいターミナルウィンドウが開く
- [ ] CLI エージェントが `mdi/close` を呼ぶと指定ウィンドウが閉じる
- [ ] `Open-AmmWindow` / `Close-AmmWindow` cmdlet が同等の動作をする
- [ ] ユーザーが手動でウィンドウを閉じた後に `mdi/close` を呼んでもクラッシュしない
- [ ] amm.exe が起動していない状態で呼んだ場合は適切なエラーが返る
- [ ] 既存ツール（`send_message` 等）は従来どおり動作する
- [ ] `mdi/wait_state` を呼ぶとフック経由で `idle` が来るまでブロックし、来たら即座に返す
- [ ] `Wait-AmmIdle` cmdlet が同等の動作をする
- [ ] `timeout_ms` 経過後に `{ state: "timeout" }` が返り、クラッシュしない
- [ ] セッションが手動クローズされた場合も wait が解放される

## 設計メモ（実装時に詳細化）

### 主要変更箇所

```
amm.exe
  └─ McpPipeServer.cs        新メソッド dispatch 追加 (openWindow / closeWindow / waitState)
                              amm/notify ハンドラで WaitBroker.Resolve() を呼ぶ
  └─ MdiWindowController.cs  (新規) openWindow / closeWindow ロジック
  └─ WaitBroker.cs           (新規) ApprovalBroker と同パターン
                              RegisterWait / Resolve / ReleaseBySession / ReleaseAll
  └─ MdiParentForm.cs        TerminalChildForm の生成 API を公開

amm-mcp.exe
  └─ McpToolHandler.cs       mdi/open, mdi/close, mdi/wait_state ツール定義追加
  └─ PipeClient.cs           amm.openWindow / amm.closeWindow / amm.waitState 呼び出し追加

Amm.PowerShell.dll (Pattern D)
  └─ OpenAmmWindowCmdlet.cs
  └─ CloseAmmWindowCmdlet.cs
  └─ WaitAmmIdleCmdlet.cs    amm.waitState を呼ぶ
```

### WaitBroker の構造（ApprovalBroker 参考）

```csharp
class WaitBroker
{
    // session_id → pending TaskCompletionSource のリスト
    Dictionary<string, List<WaitEntry>> _pending;

    // amm.waitState 受信時: TCS を登録してタスクを返す（非ブロッキング）
    Task<WaitResult> RegisterWait(string sessionId, string targetState, int timeoutMs);

    // amm/notify 受信時: 一致する pending を解放
    void Resolve(string sessionId, string state);

    // セッションクローズ時: timeout 扱いで全 pending を解放
    void ReleaseBySession(string sessionId);
}
```

### オートパイロットユースケース例

```powershell
# セッション生成
$s1 = Open-AmmWindow -Command "claude" -Title "Agent-1: ログ解析"
$s2 = Open-AmmWindow -Command "claude" -Title "Agent-2: テスト生成"

# タスク投入
Send-AmmMessage -SessionId $s1.session_id -Message "src/logs/ を解析してください"
Send-AmmMessage -SessionId $s2.session_id -Message "失敗テストのコードを生成してください"

# 並列完了待ち（各 cmdlet が別接続でブロック）
$w1 = Start-Job { Wait-AmmIdle -SessionId $using:s1.session_id -TimeoutMs 300000 }
$w2 = Start-Job { Wait-AmmIdle -SessionId $using:s2.session_id -TimeoutMs 300000 }
$w1, $w2 | Wait-Job | Receive-Job

# クリーンアップ
$s1, $s2 | Close-AmmWindow -Force
```

## 備考

- 現時点では **Draft 置き**。Pattern D（PS モジュール）の実装と同時に着手する想定。
- 関連 spec: [req-20260622-ps-module.md](./req-20260622-ps-module.md)
- 関連コンポーネント: `McpPipeServer.cs`, `ApprovalBroker.cs`（WaitBroker の参考実装）,
  `TerminalChildForm.cs`, `MdiParentForm.cs`
