# req-20260622: PowerShell モジュール（Pattern D）

- **ID**: req-20260622-ps-module
- **状態**: Draft
- **関連 UDR**: -
- **更新日**: 2026-06-22

## 背景 / 目的

amm の機能（エージェント間メッセージング・MDI ウィンドウ制御）を
**PowerShell から直接操作できる cmdlet** として公開する。

CLI エージェント経由（amm-mcp.exe）でなく、PS スクリプト自体が amm の
Named Pipe に接続して操作できるようになることで、以下が実現する:

- **AI オートパイロット**: PS スクリプトが amm を駆動し、複数 AI セッションを
  自動的に起動・タスク投入・完了待機・終了できる
- **CI 統合**: GitHub Actions などのパイプラインから amm の機能を呼べる
- **Windows 管理との融合**: `Get-Process`, `Set-Item` 等の PS 組み込みコマンドと
  amm ツールをシームレスに組み合わせられる

```
[PS スクリプト / CI パイプライン]
    ↓ cmdlet (Open-AmmWindow, Send-AmmMessage, Wait-AmmIdle, ...)
[Amm.PowerShell.dll — .NET 9 Binary Module]
    ↓ Named Pipe JSON-RPC (\\.\pipe\amm-mcp-{user})
[amm.exe — McpPipeServer / WaitBroker]
```

## 要求（What）

### R-1: バイナリ PS モジュール
- `Amm.PowerShell.dll` を .NET 9 クラスライブラリとして実装し、
  PowerShell 7+ のバイナリモジュールとして読み込めること。
- `Import-Module Amm.PowerShell` でモジュールを読み込める。
- `Install-Module` 経由での配布（NuGet / PowerShell Gallery）を視野に入れた構成にする。

### R-2: MDI ウィンドウ制御 cmdlet
（詳細は [req-20260622-mdi-window-control.md](./req-20260622-mdi-window-control.md) R-1/R-2 を参照）

| Cmdlet | 対応 MCP ツール | 説明 |
|---|---|---|
| `Open-AmmWindow` | `mdi/open` | 新しい MDI ターミナルウィンドウを開く |
| `Close-AmmWindow` | `mdi/close` | 指定 session_id のウィンドウを閉じる |

### R-3: メッセージング・待機 cmdlet

| Cmdlet | 対応 Named Pipe メソッド | 説明 |
|---|---|---|
| `Send-AmmMessage` | `amm.send` | 指定セッションへメッセージを送信 |
| `Get-AmmSession` | `amm.listParticipants` | 起動中のセッション一覧を取得 |
| `Wait-AmmIdle` | `amm.waitState` (R-6) | 指定セッションがアイドル状態になるまで待機 |

`Wait-AmmIdle` は [req-20260622-mdi-window-control.md R-6](./req-20260622-mdi-window-control.md#r-6-セッション状態の待機-mdiwait_state) の
`amm.waitState` を呼び出し、フック経由の `idle` 通知が amm.exe に届くまで **ブロッキング待機** する。
ポーリングは行わない。

### R-4: Named Pipe 接続管理
- `Connect-Amm` / `Disconnect-Amm` でセッションを明示的に管理できる。
- 未接続の場合、各 cmdlet は自動的に接続を試みる（暗黙接続）。
- amm.exe が起動していない場合は適切なエラーメッセージを出力する（例外は投げない）。
- パイプ名: `\\.\pipe\amm-mcp-{WindowsIdentity.GetCurrent().User.Value}` (既存定義と同じ)

### R-5: PowerShell パイプライン対応
- `Open-AmmWindow` はセッションオブジェクト (`AmmSession`) を出力し、
  パイプラインで `Close-AmmWindow` に渡せる。
- 例: `Get-AmmSession | Where-Object Title -like "Agent-*" | Close-AmmWindow -Force`

## 非対象（Out of scope）

- **MCP クライアント全実装**: amm-mcp.exe の全機能を PS から呼ぶことは初期スコープ外。
  MDI 制御 + メッセージング + 待機の最小セットを優先する。
- **Linux / macOS 対応**: amm は Windows 専用。Named Pipe も Windows 形式。
- **PowerShell 5.x 互換**: PowerShell 7.4+ (.NET 9) のみサポート。
- **GUI なし自動テスト**: amm.exe が必要な統合テストは手動確認とする。
- **並列待機の単一接続化**: `Wait-AmmIdle` の並列実行は `Start-Job` で別プロセス化して対応。
  単一接続での多重化は将来検討（MCP streaming 採用時）。

## 受け入れ基準

- [ ] `Import-Module Amm.PowerShell` でモジュールが読み込める
- [ ] `Open-AmmWindow -Command "claude"` で MDI ウィンドウが開く
- [ ] `Get-AmmSession | Close-AmmWindow` でパイプライン動作する
- [ ] `Wait-AmmIdle` がエージェントの完了フック受信まで待機し `{ state, elapsed_ms }` を返す
- [ ] `Wait-AmmIdle -TimeoutMs 5000` が 5 秒後に `{ state: "timeout" }` を返す
- [ ] amm.exe 未起動時に分かりやすいエラーメッセージが表示される
- [ ] PowerShell 7.4 以上で動作する

## 設計メモ（実装時に詳細化）

### ファイル構成

```
src/
  modules/
    Amm.PowerShell/
      Amm.PowerShell.csproj
      Commands/
        OpenAmmWindowCmdlet.cs
        CloseAmmWindowCmdlet.cs
        SendAmmMessageCmdlet.cs
        GetAmmSessionCmdlet.cs
        WaitAmmIdleCmdlet.cs     # amm.waitState を呼ぶ（ブロッキング）
        ConnectAmmCmdlet.cs
        DisconnectAmmCmdlet.cs
      Models/
        AmmSession.cs
        WaitResult.cs
      Pipe/
        AmmPipeClient.cs         # Named Pipe クライアント
      Amm.PowerShell.psd1        # モジュールマニフェスト
```

### Wait-AmmIdle 実装例

```csharp
[Cmdlet(VerbsLifecycle.Wait, "AmmIdle")]
[OutputType(typeof(WaitResult))]
public class WaitAmmIdleCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)]
    public string SessionId { get; set; } = "";

    [Parameter]
    public int TimeoutMs { get; set; } = 300_000;

    protected override void ProcessRecord()
    {
        var client = AmmPipeClient.GetOrConnect();
        // amm.waitState はサーバー側で目標状態到達まで応答を保留する
        var result = client.SendRequest("amm.waitState",
            new { session_id = SessionId, target_state = "idle", timeout_ms = TimeoutMs });
        WriteObject(new WaitResult(result.state, result.elapsed_ms));
    }
}
```

### Open-AmmWindow 実装例

```csharp
[Cmdlet(VerbsCommon.Open, "AmmWindow")]
[OutputType(typeof(AmmSession))]
public class OpenAmmWindowCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true)] public string Command { get; set; } = "";
    [Parameter] public string? Title { get; set; }
    [Parameter] public string? WorkingDirectory { get; set; }

    protected override void ProcessRecord()
    {
        var client = AmmPipeClient.GetOrConnect();
        var result = client.SendRequest("amm.openWindow",
            new { command = Command, title = Title, workingDirectory = WorkingDirectory });
        WriteObject(new AmmSession(result.session_id, Title ?? Command));
    }
}
```

## 備考

- 現時点では **Draft 置き**。MDI ウィンドウ制御 spec と同時着手の想定。
- 関連 spec: [req-20260622-mdi-window-control.md](./req-20260622-mdi-window-control.md)
- 関連 spec: [req-20260622-mcp-gateway.md](./req-20260622-mcp-gateway.md)
