# コードレビュー記録 — 設計書 + ソースコード全体（IT アーキテクト / シニアプログラマー / セキュリティスペシャリスト視点）

- 日付: 2026-07-26
- 対象: リポジトリ全体（`src/apps/Amm/`(Tauri/Rust、現行主体実装) 約8,300行 + `public/app.js`等 約3,600行、`src/apps/Amm.net/`(レガシー.NET) 約19,400行、`src/modules/`(PowerShell) 、`src/installer/`+`src/apps/Amm/src-tauri/{wix,nsis}/`(インストーラ)、`openspec/specs/`全16 capability、`.udr/records/`全7件、`docs/design/`、`HANDOVER.md`/`CLAUDE.md`/`AGENTS.md`/`README.md`）
- 対象ブランチ: `claude/dotnet-tauri-feature-audit-jwltx6`
- レビュー実施者: Claude Code。ユーザー指示によりサブエージェント8体（general-purpose、読み取り専用）に領域分割して並列レビューを実施し、メインエージェントが各成果物のファイル網羅性・3視点(アーキ/コード品質/セキュリティ)充足・所見の具体性を精査（QA）した上で本記録に統合。QAで差し戻しが必要な報告はなかった（全8件が初回で基準を満たした。うち3件はエージェント側のセッション利用上限により一度中断し、再開後に完遂）。
- 手法: 静的読解のみ。実行時検証・ペネトレーションテストは行っていない。

## 追記（2026-07-26、対応実施）

ユーザー判断により以下の方針で対応した:

- **Tauri版(現行主体実装)のH-1/H-5と、CSP無効化・mcp.rsのUTF-8破損・GitHub Actionsスクリプトインジェクションの計5件を修正**。いずれも実ビルド・実テスト（`cargo build`/`cargo test`、全103件中の既知フレーキーテスト1件を除き全パス）で検証済み。CSPは実際にamm.exeを起動しCDP経由でCSP違反ゼロ・UI操作(MCPゲートウェイダイアログのcssText経由スタイル含む)が正常動作することも確認済み。GitHub Actionsの入力検証はpwshロジックをローカルで抽出実行し、注入文字列が実際にREJECTされることを確認済み。詳細は各修正箇所のコードコメントおよび新規追加テスト(`profile.rs`の`quote_command_token_*`/`chcp_wrapped_command_*`、`mcp.rs`の`read_line_bounded_tests`)を参照。
- **レガシー.NET版のH-2/H-3/H-4は修正しない、既知問題として`tasks/backlog.md`に記録するのみ**（レガシー版は当面参照・保守のみの位置づけのため）。
- **続報（同日追加対応）**: Medium節のTOFU回避経路(`.amm`信頼モデル)・`resolve_targets`非決定順・hook_cli.rsのcmd.exeメタ文字非エスケープ(Rust側のみ)を追加修正。Named Pipe ACL/`.amm`信頼モデル/承認ハブ設計転換の3件をUDRに追記。C-1（`openspec/specs/`のcutover同期、`migrate-to-tauri`archive・9件delta specマージ）を完了。残るLow/Info項目・.NET版hook_cli.rsメタ文字非エスケープは`tasks/backlog.md`へ記録し保留。

### H-1修正の実装上の重要な発見

`quote_command_token`の当初案（埋め込み`"`を`^`でエスケープしcmd.exeのクォート対称性を保つ）は、実際には`portable_pty::CommandBuilder::arg()`が内部で標準CRT/ArgvQuoteアルゴリズム(`append_quoted`)を用いて引数全体を再クォートするため機能しないことが判明した（append_quotedはキャレットエスケープを理解せず、あらゆる`"`の直前に無条件でバックスラッシュを1つ以上挿入するため、キャレットと引用符を隣接させる入力を構成できない）。Windowsのパスはそもそも`"`を含められないため、**埋め込み`"`を含むトークンは安全にエスケープする代わりに起動そのものを拒否する**方式に変更した。実証テストは当初`portable_pty`経由のPTY起動で行う予定だったがConPTYの非同期ライフサイクルに起因してテストがハングしたため、`std::process::Command`（Windows側の引数クオート実装はほぼ同一）を使う方式に切り替えて検証した。

## サブエージェント構成（トレーサビリティ用）

| # | 担当領域 | 対象ファイル数 | 結果 |
|---|---|---|---|
| 1 | Rust IPC/セキュリティクラスタ (mcp.rs, approval.rs, hook_cli.rs, mcp_cli.rs, amm-mcp/*) | 7 | 完了 |
| 2 | Rust コア/lib.rs+gateway.rs | 5 | 完了 |
| 3 | Rust データ/プロファイル層 (profile.rs, wait_detect.rs, input_history.rs, chat_*.rs, git_helper.rs) | 6 | 完了 |
| 4 | フロントエンド (app.js, index.html, style.css, capabilities) | 4 | 完了 |
| 5 | レガシー.NET MCP/IPCクラスタ (Core/Mcp/*, Amm.Mcp/*) | 14 | 完了（1回中断・再開） |
| 6 | レガシー.NET Forms (MdiParentForm.cs等) | 5 | 完了（1回中断・再開） |
| 7 | レガシー.NET Core/ダイアログ群 | 27 | 完了 |
| 8 | PowerShellモジュール(新旧)+インストーラ+ビルドスクリプト | 21 | 完了（1回中断・再開） |
| 9 | 設計文書(openspec/specs/全16件, UDR全7件, docs/design/, HANDOVER等) | 30+ | 完了 |

## サマリ

**Critical 1件、High 5件、Medium 約15件、Low/Info 多数**。最大の構造的問題は、`openspec/specs/`配下16 capabilityの正本仕様が cutover(2026-07-26、Tauri版への正式切替) 後もほぼ全件レガシー.NET/WinForms版の記述のまま更新されておらず、`pane-management`の正本specが存在しない・複数specがツール名`mdi/*`のまま(実装は`pane/*`)という状態になっている点（3系統のレビュアーが独立に同一の乖離を発見しており、確度が高い）。セキュリティ面では、Rust版・レガシー.NET版の両方に**独立して同種のシェルコマンドインジェクション経路**（`chcp`ラップ経由、profile由来の引数にダブルクォートを含めることでcmd.exeのクォート対称性を破壊）が存在することが最も重要な発見。また子プロセスのライフサイクル管理（PTYペインのJob Object保護欠如、.NET版`ManagedMcpProcess`の再起動競合）にゾンビプロセス化のリスクがある。良好な点として、Named Pipe ACL(SDDL)の実装と自動検証テスト、`.amm`自動起動の信頼確認ゲート、Git操作のシェル非経由実行、フロントエンドのXSS対策(innerHTML不使用の徹底)は、複数レビュアーが独立して高く評価している。

---

## 🔴 Critical

### C-1. ✅**2026-07-26 修正済み**. `openspec/specs/` 正本仕様がcutover後もレガシー.NET版の記述のまま。`pane-management`のspecが正本ディレクトリに存在しない

- **該当**: `openspec/specs/mdi-window-control/spec.md`・`mdi-window-management/spec.md`（廃止対象と自認されながら現存）、`openspec/changes/migrate-to-tauri/specs/pane-management/spec.md`（正しい仕様だが未マージ）、`openspec/specs/mcp-server/spec.md`・`mcp-gateway/spec.md`（ツール名`mdi/*`のまま。実装は`UDR-amm-20260713T0447-98f`により`pane/*`へリネーム済み — `src/apps/Amm/src-tauri/src/mcp.rs`のコメント・実装・`PARITY-AUDIT.md`は全て`pane/*`で一致）
- **問題**: `openspec/changes/migrate-to-tauri/tasks.md` 8.1.1は「`mdi-window-control`/`mdi-window-management`はこの変更で撤廃対象」と当事者自身が認識しているが、実際には一度も撤廃・置換されていない。`migrate-to-tauri` change自体も`openspec archive`されておらず(`openspec/changes/`直下に現役のまま残存)、README.md/CLAUDE.mdの「`openspec/specs/`はcapability別の正本仕様（16件、Tauri版準拠）」という記述は事実と異なる。
- **裏付け**: 設計文書レビュー(#9)が独立にこの問題の全体構造を特定。加えて、Rust IPC/セキュリティレビュー(#1)・Rustコアレビュー(#2)がそれぞれ独立に「コード内コメントが参照する`pane-management`specが存在しない」「`mcp-server`/`mcp-gateway`specの`mdi/*`表記が実装と乖離」を発見しており、3系統から同一の乖離が確認された。
- **推奨対応**: `migrate-to-tauri`を正式に`openspec archive`し、`pane-management`をspecs/へ新設、`mdi-window-control`/`mdi-window-management`を廃止。他13 capabilityも`openspec/changes/migrate-to-tauri/specs/`のdelta（wait-detection・hook-cli・approval-hub・git-integration・input-history・profile-schema）を本マージする。
- **対応内容**: `migrate-to-tauri`を`openspec/changes/archive/2026-07-26-migrate-to-tauri/`へarchive、9件のdelta specを本マージ（`pane-management`新設、`mdi-window-control`/`mdi-window-management`廃止、他6 capabilityへMODIFIEDデルタ反映）、`mcp-server`/`mcp-gateway`の`mdi/*`表記を`pane/*`へ修正。`openspec validate --specs`で15 capability全件パス確認済み。

---

## 🟠 High

### H-1. ✅**2026-07-26 修正済み**. `profile.rs`のchcpラップ経由コマンド構築が、cmd.exeのクォート対称性破壊によるコマンドインジェクションを許す（Rust版）

- **該当**: `src/apps/Amm/src-tauri/src/profile.rs:399-414`（`quote_command_token`/`build_chcp_wrapped_command`）。呼び出し元 `lib.rs:955`。`auto_chcp`既定値`true`。
- **問題**: `quote_command_token`はトークンに空白が含まれる場合のみ`"..."`で囲むだけで、内部の`"`をエスケープしない。組み立てられた文字列は`cmd.exe /d /s /c "chcp 65001 > nul && {inner}"`として実行され、`args`に埋め込みダブルクォートを含むトークン（例: `foo" & calc.exe & "`）を仕込むとcmd.exe側のクォート解釈が破綻し、`&`以降が別コマンドとして実行される。
- **到達経路**: `command-import-export`機能（`.ammprofiles`の外部インポート）や共有`profiles.amm`経由でユーザーが気づかず取り込み、起動する。`.amm`自動起動の信頼確認ゲート（`is_path_trusted`）は`mcpServers`の自動起動のみを対象とし、`executable`/`args`は対象外。
- **推奨対応**: CRT準拠のダブルクォートエスケープを実装するか、`chcp`適用をシェル文字列結合でなくコンソールAPI直接呼び出しに置き換える。

### H-2. ⏸**対応しない(既知問題としてtasks/backlog.mdに記録、2026-07-26)**. `ConPtyWrapper`のchcpラップ経由コマンド構築が同種のインジェクションを許す（レガシー.NET版、H-1と独立に同一クラスの脆弱性）

- **該当**: `src/apps/Amm.net/Core/ConPtyWrapper.cs:39-42`、`src/apps/Amm.net/Core/SessionProfile.cs:517-531`（`BuildCommandLine`）
- **問題**: H-1と同一の脆弱性パターン（`cmd.exe /d /s /c "chcp 65001 > nul && {commandLine}"`、内部のダブルクォート非エスケープ）が、Rust版とは独立に開発された.NET版にも存在する。`AutoChcp`既定値も`true`。
- **到達経路**: H-1と同様、`.ammprofiles`インポート経由。加えて`ImportProfilesDialog.BuildLabel`はコマンド内容をそのまま表示するのみでシェル制御文字への警告がない。`MdiParentForm.ConfirmAutoStartIfUntrusted`は`HasExplicitFile=true`の場合のみ動作し、既定ファイル名の暗黙ロードや`CommandManagerDialog`からの手動起動には確認が挟まらない。
- **推奨対応**: H-1と同様の対策。両実装が同一の設計判断ミスを独立に踏んでいる事実自体を、将来の移植・改修時の教訓としてUDR/tasks.mdに記録する価値がある。

### H-3. ⏸**対応しない(既知問題としてtasks/backlog.mdに記録、2026-07-26)**. WinFormsの一部イベント経路でasync void例外ガードが欠落し、amm全体クラッシュ（全MDIセッション喪失）につながる（レガシー.NET版）

- **該当**: `MdiParentForm.cs:1596-1604`（`QuickPromptRequested`）、`MdiParentForm.cs:3647-3673`（プロンプト再送信/クイック送信メニュー）、`TerminalChildForm.cs:1743-1766`（同メニュー）
- **問題**: 開発者自身が用意した`RunGuarded`共通例外ガード（`MdiParentForm.cs:2965-2971`、キーボード送信経路にのみ適用）が、右クリックメニュー経由の送信経路には適用されていない。ペインクローズ直後の書き込み競合等で例外が飛ぶと、既定のWinForms例外処理でプロセス全体がクラッシュし、無関係な他ペインの全AIエージェントセッションが道連れになる。
- **推奨対応**: 全ての送信系async voidイベントハンドラに`RunGuarded`相当のtry/catchを一律適用する。

### H-4. ⏸**対応しない(既知問題としてtasks/backlog.mdに記録、2026-07-26)**. レガシー.NET版Named Pipeサーバにパイプスクワッティング対策がなく、クライアント側も接続先を検証しない

- **該当**: `src/apps/Amm.net/Core/Mcp/McpPipeServer.cs:83-103`（`CreateServerStream`、常に複数インスタンス許容）、`src/modules/Amm.PowerShell.net/Pipe/AmmPipeClient.cs:24-35`。対比: Rust版`src/apps/Amm/src-tauri/src/mcp.rs`は`first_pipe_instance(true)`でスクワッティング対策済み。
- **問題**: 同一Windowsユーザー権限の別プロセス（侵害された拡張機能・postinstallスクリプト等）が同名パイプの別インスタンスを先に確保できれば、正規クライアントの接続先がOSにより不定になり、メッセージの盗聴・偽装応答が成立しうる。ACL（現在ユーザーのみ）は「誰が接続できるか」は制限するが「誰が名前を先取りできるか」は制限しない。
- **推奨対応**: `McpPipeServer`の初回インスタンス生成を`FILE_FLAG_FIRST_PIPE_INSTANCE`相当で作成し、既存インスタンスがあれば失敗させる。

### H-5. ✅**2026-07-26 修正済み**. PTYペイン（AIエージェント本体プロセス）にWindows Job Object保護がなく、ハンドルも保持されずゾンビプロセス化しうる（Rust版）

- **該当**: `src/apps/Amm/src-tauri/src/lib.rs:24-44`（`PtyEntry`にプロセスハンドル/PIDのフィールドなし）、`lib.rs:980`（`spawn_command()`の戻り値を束縛せず破棄）。対比: `gateway.rs`の外部MCPプロセスは`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`で保護済み（`assign_kill_on_close_job`/`close_job_handle`, 437-484行）。
- **問題**: ペインを強制クローズした際、CLIエージェントが生成した孫プロセス（`npx`経由のツール等）がConPTYの終了シグナルを受け取れない/別コンソールに属する場合、amm側にはそのプロセスを追跡・終了させる手段が一切ない。同じアプリ内で「外部MCPサーバーは保護、ペインの主機能プロセスは無保護」という非対称な設計になっている。
- **推奨対応**: `PtyEntry`にも`gateway.rs`と同様のJob Object割り当てを追加する。

---

## 🟡 Medium（テーマ別グループ化）

### セキュリティ設計

- ✅**2026-07-26 修正済み**. **CSPが明示的に無効化**（`tauri.conf.json:25-27`、`"csp": null`）: 3系統のレビュアー(#2, #4, #8)が独立に指摘。現状XSSの具体的な入口は発見されていない（`innerHTML`未使用が徹底、frontend-jsレビュー確認済み）が、多層防御の最終防衛線が意図的に外されている。→ `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'`を設定。実機でamm.exeを起動しCDP経由でCSP違反0件・cssText使用ダイアログの正常動作を確認済み。
- ✅**2026-07-26 修正済み（Rust版）**. **`.amm`自動起動の信頼判定がパス文字列の完全一致のみ**（`profile.rs:669-687`、TOFU）: ファイル内容のハッシュ検証がなく、信頼済みパスへの後からのファイル差し替えで確認ゲートを回避できる。→ `TrustedProfileEntry{path, content_hash}` + FNV-1a ハッシュへ再設計。パス一致だけでなく内容ハッシュ一致も要求するよう変更、回帰テスト追加。
- ⏸**Rust側のみ2026-07-26修正済み、.NET側は対応しない(既知問題としてtasks/backlog.mdに記録)**. **フック登録コマンドの`mcp_exe_path`がcmd.exeメタ文字に対して非エスケープ**（Rust `hook_cli.rs:109,335,340` / .NET `HookCliRegistrar.cs:200,438,447,472,481`、両実装で同一パターン）: インストール先パスに`&`等を含む場合、hook発火のたびに任意コマンドが実行されうる。実害は限定的（インストールパスは通常固定）だが両実装で共通の未対応ギャップ。→ Rust版に`validate_mcp_exe_path_for_cmd_wrapping()`を追加し`register_claude`/`register_copilot_like`から呼び出し、メタ文字を含むパスでの登録を拒否。.NET版`HookCliRegistrar.cs`は未対応のまま。
- ✅**2026-07-26 修正済み**. **GitHub Actionsワークフローのスクリプトインジェクション**（`release.yml:42-53`、`pr-prerelease.yml:49-70`）: `${{ github.event.inputs.version }}`等を`run:`ブロックへ直接式展開しており、GitHub公式が明示的に警告するパターン。さらに`tools\publish.cmd`側でも`%AMM_VERSION_ARG%`が未クオートで`dotnet publish`へ展開される。`env:`経由の参照へ変更すべき。→ 両ファイルの該当箇所を`env:`経由の参照に変更し、バージョン/PR番号の形式検証(正規表現)を追加。`tools\publish.cmd`自体のクオート強化は見送り(CI側の検証で実際に到達可能な注入経路は閉じたため)。ローカルでpwshロジックを抽出実行し、注入文字列が実際にREJECTされることを確認済み。
- **承認ポップアップの`tool_input`要約が1000文字で打ち切られ、危険なペイロード末尾が非表示のまま許可されうる**（`ApprovalPopupForm.cs:212-236`、レガシー.NET版）。
- **レガシーMCP `mdi/open`が無確認で任意コマンドを起動**（`MdiParentForm.cs:415-471`）: `.amm`自動起動には確認ゲートがあるのに、同じ「任意コマンド起動」能力を持つMCP経路には確認が一切ない非対称設計。

### プロセスライフサイクル

- **`ManagedMcpProcess`の自動再起動が`DisposeAsync`と競合しゾンビプロセスを生みうる**（.NET版`Gateway/ManagedMcpProcess.cs:134-150,255-277`）: `Process.Exited`イベントの非同期発火とdispose処理のタイミング次第で、誰にも追跡されない子プロセスが起動されうる。H-5(Rust版PTYハンドル未保持)と同じ「子プロセスライフサイクル管理」というテーマの別表出。
- **`GitHelper.Run`がタイムアウト時に子プロセスをKillしない**（.NET版`Git/GitHelper.cs:56-84`）: `git.exe`が残存し`index.lock`保持により以降のgit操作が連鎖的に失敗しうる。

### データ/プロトコル処理

- ✅**2026-07-26 修正済み**. **`mcp.rs`の`read_line_bounded`がバイト単位で`char`化しUTF-8マルチバイト文字列を破損**（`mcp.rs:766-785`）: 日本語等を含むメッセージが受信側で文字化けする片方向バグ。→ 生バイトをバッファに蓄積し行末で一括UTF-8デコード(lossy)する方式に変更。日本語を含む回帰テスト(`read_line_bounded_tests`)追加。
- ✅**2026-07-26 修正済み**. **`mcp.rs`の`resolve_targets`フォールバックがHashMap由来の非決定順**（`mcp.rs:89-110`）: spec規定の「起動順の先頭」を実装していない。→ フォールバック直前に`instance`昇順でソートするよう修正、回帰テスト(`resolve_targets_tests`)追加。
- **wait-detectionの状態遷移がspec記載の2段階タイマーから意図的に簡略化されているが、spec.md未更新**（`wait_detect.rs`冒頭コメント自認、`openspec/specs/wait-detection/spec.md:36-49`）: rust-data-profileレビューと設計文書レビューが独立に指摘。
- **ユーザー指定`waitPatterns`正規表現のReDoS対策(100ms matchTimeout)がspec要求に反し未実装**（`wait_detect.rs:100-105`）: Rustの`regex`クレートはバックトラッキング型でなく実害は限定的だが、spec通りの厳密性は満たされていない。

### フロントエンド

- **xterm.jsの`Terminal`インスタンスがペインクローズ時に`dispose()`されずリーク**（`app.js:764-792`、`.dispose(`呼び出しがコード中に皆無）: 長時間運用でのメモリ増加要因。
- **`invoke()`のロード時ローカル束縛によりテストダブル差し替えが効かない**（`app.js:1`）: 既に`~/.claude/settings.json`を誤って書き換えた実インシデントの根本原因（`HANDOVER.md`記録済み）。
- **PTY書き込み/リサイズ系`invoke()`にエラーハンドリングが欠落**（他の大半のinvoke呼び出しと不整合）。
- **`window.resize`にデバウンスがなく、ドラッグ中に全ペイン分の`pty_resize`が連打される**。

### ドキュメント/UDR

- ✅**2026-07-26 記録済み**. **Named Pipe ACL実装・`.amm`信頼モデルという重要なセキュリティ設計判断がUDR未記録**: 旧.NET版には対応するUDR(`UDR-amm-20260427T0225-7a3`)があったのに、Tauri再実装時には記録されなかった（退行）。→ `UDR-amm-20260726T0745-7b1`(Named Pipe ACL)・`UDR-amm-20260726T0745-ccd`(.amm信頼モデル)を追記。
- ✅**2026-07-26 記録済み**. **承認ハブの第2WebviewWindow→DOMオーバーレイ設計変更（実機デバッガでのデッドロック根本原因特定を伴う重大な設計転換）がUDR未記録**。`architecture.md`自身の「理由はUDRへリンク」という方針に反しインラインで理由を記載している。→ `UDR-amm-20260726T0745-194`を追記。
- ✅**2026-07-26 修正済み**. **`UDR-amm-20260719T0013-b7e`の`code_refs`がcutoverリネーム前の旧パスのまま**。→ `src/apps/Amm.Mcp/`等の旧パスを現行`src/apps/Amm/src-tauri/`配下の実パスへ、`migrate-to-tauri/design.md`を archive後のパスへ修正。

---

## 🟢 Low / Info（要約、詳細は各サブエージェント記録参照）

- **アーキテクチャ肥大化**: `lib.rs`(1729行、約60コマンド)・`app.js`(3429行)・`MdiParentForm.cs`(4056行)がいずれも単一ファイルに多責務を同居させている。
- **`std::sync::Mutex` + `.unwrap()`の多用**（Rust版lib.rs全体）: パニック時のポイズニングで波及故障のリスク。
- **テストカバレッジの偏り**: Rust版`git_helper.rs`/`chat_recording.rs`が無テスト、.NET版`Gateway/*`・`WaitBroker`・`AtomicFileWriter`・ダイアログ群27ファイル中21ファイルが無テスト。
- **コード重複**: ANSI stripパターンが3ファイル(Rust)で独立定義、DPIボタン高さ計算式が7ダイアログ(.NET)で重複、ファイル名サニタイズが2箇所(.NET)で重複、`splitArgsInput`系関数がapp.js内で重複。
- **P/Invoke境界の軽微な入力検証不足**（`NativeDropInterop.ExtractPaths`のDragQueryFile件数上限なし等）。
- **ドキュメント鮮度**: `HANDOVER.md`の番号重複(7番2連続)、`README.md`の「archive済み」という誤記載、`openspec/config.yaml`のcontextがcutover前のまま、`docs/design/amm-companion-boundary.md`にcutoverバナーなし。
- **バージョン管理**: `tools\build-installer.cmd`の既定バージョンが`0.1.1.0`で実際の`1.2.0.0`と大きく乖離。
- **`profile.rs::save_profiles`の一時ファイル名が固定**（.NET版`AtomicFileWriter`はランダム名+flush済みでより堅牢。Rust版が見習うべき非対称性）。

---

## ✅ 良い設計判断として明記しておくべき点（複数レビュアーが独立に高評価）

- **Named Pipe ACL(SDDL)の実装と自動検証テスト**（Rust版`mcp.rs`、.NET版`McpPipeServer.cs`）: 実際に生成したパイプのDACLをOS APIで読み戻して検証する単体テストまで用意されている。
- **`.amm`自動起動の信頼確認ゲート**（両実装）: 実機検証で「確認ダイアログが到達不能」という機能不全を発見・修正した経緯が丁寧に記録されている。
- **Git操作のシェル非経由実行**（両実装とも`Command::args`/`ArgumentList`で配列渡し）: コミットメッセージ等のユーザー入力がシェルインジェクション経路にならない。
- **フロントエンドのXSS対策の徹底**（`app.js`全3429行で`innerHTML`への未検証データ埋め込みが皆無、明文化されたコーディング規約として遵守）。
- **承認ハブの第2WebviewWindowデッドロックの根本原因特定**（実機デバッガでの解析に基づく構造的解決、UDR未記録である点はMedium参照）。
- **`SessionProfile`/`profile.rs`の実行ファイルPATH解決**（両実装とも相対/カレントディレクトリ由来のPATH成分を除外し、実行ファイルハイジャックを構造的に防止）。
- **WiXの`Environment`テーブル活用**によるPATH追加（カスタムアクション手組みを回避）、NSIS側のPATH操作も境界マッチ・`REG_EXPAND_SZ`維持等、実害を意識した実装。
- **`ApprovalBroker`の4解放トリガー設計とテスト網羅**（Rust版、Tauri再実装時にRust版のみ接続切断検知を追加実装、.NET版は同機能が実装されず退行している点はMedium参照）。

---

## 次のアクション

**2026-07-26時点で残っている項目:**

`tasks/backlog.md`の「セキュリティ／コードレビュー残課題 (2026-07-26 全体レビュー)」節へ転記済み。レガシー.NET版H-2/H-3/H-4・.NET版hook_cli.rsメタ文字非エスケープ（既知問題、修正しない方針）、およびアーキテクチャ肥大化・テストカバレッジ偏り・コード重複等のLow/Info項目群（優先度低、後回し）。

**対応済み(2026-07-26):** C-1、H-1、H-5、CSP無効化、mcp.rs UTF-8破損、GitHub Actionsスクリプトインジェクション、TOFU回避経路、resolve_targets非決定順、hook_cli.rsメタ文字非エスケープ(Rust側)、Named Pipe ACL/`.amm`信頼モデル/承認ハブ設計転換のUDR追記、`UDR-amm-20260719T0013-b7e`のcode_refs修正（上記各節参照）。
**対応しない方針が確定(2026-07-26):** H-2、H-3、H-4（レガシー.NET版、`tasks/backlog.md`に既知問題として記録済み）。
