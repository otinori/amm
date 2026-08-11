# Backlog — 未着手の積み残み・要望プール

> 着手が決まったら `TASKS.md` へ昇格する。

## 要望 / アイデア

> 2026-07-11: 本セクションに残っていた7項目（アイドル時自動送信・コマンド設定インポート/エクスポート・
> 右クリックからのクイック送信コマンド登録・システムトレイ常駐アイコン・MDIウィンドウ制御・
> PowerShellモジュール・MCPゲートウェイ）は、`openspec/changes/establish-baseline-specs` でのリバース
> SPEC 起票時にいずれも実装済みであることが判明したため撤去した。詳細は
> `tasks/done/2026-07-baseline-spec-and-backlog-cleanup.md` を参照。現行仕様は
> `openspec/specs/{auto-send-idle,command-import-export,quick-command-register,tray-icon,
> mdi-window-control,ps-module,mcp-gateway}/spec.md` を参照。

- **ペインで動作するCLI(Claude Code等)のACP(Agent Client Protocol)ホスト化の実現可能性検討**(2026-07-19提起)。現状ammは各ペインでCLIをConPTY上の生テキストPTYとして起動しているが、Claude Code/Codex CLI/Copilot CLI/Gemini CLI等は2026年1月開始のACP Registryに登録済みでACP経由の連携に対応している。amm側がホストとしてACP(stdin/stdout JSON-RPC)でこれらを駆動できれば、生テキストPTYより構造化されたやり取りが可能になる。実現可否・既存ConPTY方式との共存/置換方針は未検討。優先度低・後回しでよいとユーザーから明言あり。関連: `openspec/changes/add-acp-gateway/` / `openspec/changes/add-a2a-gateway/`(2026-08-10、旧`add-agent-protocol-gateway`をACP/A2Aで分割。こちらは逆方向、ammが外部ACP/A2Aエージェントを**呼び出す**クライアント側の変更で別スコープ)
  - **予備調査メモ(2026-07-19)**: Claude Code CLI 単体には `--acp` 相当のネイティブフラグは無く(`claude --acp` は不明オプションエラー)、ACP対応は `claude-code-acp` / `acp-claude-code` / `claude-agent-acp` 等の**サードパーティ製ブリッジアダプタ**(`claude -p --output-format stream-json --verbose` をラップしJSON-RPC化)経由が実態。ACP Registry(2026年1月開始)への登録は「ACP経由で到達可能」を意味し、必ずしも「CLIバイナリがネイティブにACPを話す」ことを意味しない。Codex CLI/Gemini CLI/Copilot CLIも同様にエコシステム上ではACP対応として扱われているが、ネイティブ実装かブリッジ経由かはCLIごとに個別確認が必要。amm側で実装する場合、(a)非公式サードパーティブリッジへの依存というサプライチェーンリスク、(b)将来各ベンダーがネイティブACP対応した場合の移行、の両方を検討事項に加える必要がある。

## 既知の残課題

- TSF（Text Services Framework）由来の IME 二重送信の真因根治（現状は受信側 dedup/coalescer で緩和）
- ~~NSISアンインストーラが`amm-mcp.exe`を削除しない~~ → **2026-07-26 再調査の結果、誤報と判明・撤回**。破損したamm.exeを含む初期ビルドのアンインストール結果を見て記録したが、これはそのビルド固有の状態(調査中に`resources/`を一時的に操作した影響の可能性)によるものだった。最終的なPATH修正版インストーラの生成スクリプトを確認したところ`Delete "$INSTDIR\amm-mcp.exe"`は正しく含まれており、実機での再検証でもアンインストール後に`C:\Program Files\amm`ディレクトリごと完全に消えることを確認した。対応不要。
- ~~MSI版インストーラのPATH自動追加は未検証~~ → **2026-07-26 実機検証完了、問題なし**。`msiexec /i`でインストール→GUI起動(WebView2プロセス6個)・PATH解決を確認→`msiexec /x`でアンインストール→Machine PATHがベースラインとバイト単位で完全一致(1234文字・30件・amm残留なし)することを確認した。予想通りWiXのEnvironmentテーブルはMSIエンジン自体が処理するため、NSIS版で見つかったNSIS_MAX_STRLEN起因のバグは発生しなかった。

## レガシー.NET版の指摘事項について (2026-07-26以降、対象外)

`UDR-amm-20260726T0839-5d2`により、レガシー.NET版(`src/apps/Amm.net/`等)は今後保守しない方針が確定した。以下は同方針確定前にこのbacklogで個別追跡していた.NET版固有の指摘だが、以後は追跡対象外とする（当該記録は`records/reviews/code-review/2026-07-26-full-repo-architecture-security-review.md`に一次証跡として残る）。**2026-08-10、公開準備に伴いレガシー.NET版のコード自体を削除**したため、以下は完全に実体を失った過去の指摘として記録のみ残す:

- H-2/H-3/H-4（`ConPtyWrapper.cs`のコマンドインジェクション、WinFormsのasync void例外ガード欠落、`McpPipeServer.cs`のパイプスクワッティング対策なし）
- .NET側hook_cli(`HookCliRegistrar.cs`)のcmd.exeメタ文字非エスケープ
- `MdiParentForm.cs`(4056行)の肥大化、.NET版ダイアログ群のテストカバレッジ不足、DPIボタン高さ計算式・ファイル名サニタイズの重複
- P/Invoke(`NativeDropInterop.ExtractPaths`)の検証不足
- `tools\build-installer.cmd`のバージョン不整合
- `ManagedMcpProcess`の再起動競合、`GitHelper.Run`のタイムアウト時Kill漏れ、承認ポップアップの1000文字打ち切り、`mdi/open`の無確認起動
- `tests/apps/Amm.Tests`のCS0433ビルド不能（2026-07-26発見、`Amm.Tauri→Amm`/`Amm→Amm.net`リネーム時の参照更新漏れが原因と推定。詳細は`tasks/retro-pending.md`2026-07-26エントリ参照）

## アーキテクチャ・品質面の残課題 (2026-07-26 全体レビュー、Tauri版/Low-Info)

`records/reviews/code-review/2026-07-26-full-repo-architecture-security-review.md`のLow/Info節のうち、現行主体実装(Tauri/Rust版)に該当するもの。**方針: 全件対応する**（2026-07-26、実機Windows環境が利用可能になったためユーザー承認）。

- ✅**2026-07-26 対応済み**. **アーキテクチャ肥大化**: `src/apps/Amm/src-tauri/src/lib.rs`(1729行、約60コマンド)・`public/app.js`(3429行)がいずれも単一ファイルに多責務を同居させている。責務別モジュールへ分割する。→ Rust側はlib.rsを`native_ui.rs`(トレイ/ウィンドウchrome)・`commands_profile.rs`(プロファイル/MCPサーバ設定CRUD)・`pty.rs`(PTYライフサイクル)・`commands_misc.rs`(approval/history/git/hook登録等)へ分割、lib.rs自体は194行(モジュール宣言+`run()`のみ)に縮小。フロントエンド側はapp.jsを`pane-layout.js`/`pane-lifecycle.js`/`send-helpers.js`/`dialogs-quick-stats.js`/`dialogs-profile-mcp.js`/`input-history.js`/`events-integration.js`の7ファイル(classic script、共有グローバルスコープ、`index.html`の読込順=旧app.jsの並び順を維持)へ分割。実機CDP検証(ダイアログ開閉・ペイン作成/送信/クローズ・git-guardモーダル・二重Escクリア)でconsole error 0件を確認済み。
- ✅**2026-07-26 対応済み**. **`std::sync::Mutex` + `.unwrap()`の多用**（Rust版`lib.rs`全体）: パニック時のポイズニングで波及故障のリスク。監査し必要箇所を安全なハンドリングへ置換する。→ 67箇所全ての`.lock().unwrap()`を`.lock().unwrap_or_else(|e| e.into_inner())`へ置換(lib.rs/native_ui.rs/commands_profile.rs/commands_misc.rs/pty.rs/input_history.rs)。`approval.rs`/`gateway.rs`は`tokio::sync::Mutex`(非同期・ポイズニングなし)のため対象外と確認済み。
- ✅**2026-07-26 対応済み**. **テストカバレッジの偏り**: Rust版`git_helper.rs`/`chat_recording.rs`が無テスト。単体テストを追加する。→ 実git repoを使った統合テスト(git_helper.rs、6件)とsanitize/feed/is_quiet/complete/chrono_liteの単体テスト(chat_recording.rs、12件)を追加。
- ✅**2026-07-26 対応済み**. **コード重複**: ANSI stripパターンが3ファイル(Rust)で独立定義、`splitArgsInput`系関数が`app.js`内で重複。共通化する。→ Rust側は新設`ansi.rs`に一本化(profile.rs/wait_detect.rs/chat_recording.rsから移行)。app.js側は`splitArgsRespectingQuotes`(実質`splitArgsInput`への薄いラッパーのみで残存していた)を削除し呼び出し元を直接`splitArgsInput`へ。
- ✅**2026-07-26 対応済み**. **`profile.rs::save_profiles`の一時ファイル名が固定**。ランダム名+flush済みの方式へ変更する。→ `uuid::Uuid::new_v4()`によるランダム名+`File::sync_all()`でのfsync後にrenameする方式へ変更。
- ✅**2026-07-26 対応済み**. **xterm.jsの`Terminal`インスタンスがペインクローズ時に`dispose()`されずリーク**（`app.js:764-792`）: 長時間運用でのメモリ増加要因。→ `closePane()`に`pane.term.dispose()`を追加。
- ✅**2026-07-26 対応済み**. **PTY書き込み/リサイズ系`invoke()`にエラーハンドリングが欠落**（他の大半のinvoke呼び出しと不整合、`app.js`）。→ 全`pty_write`/`pty_resize`呼び出しに`.catch((err) => console.error(...))`を追加。
- ✅**2026-07-26 対応済み**. **`window.resize`にデバウンスがなく、ドラッグ中に全ペイン分の`pty_resize`が連打される**（`app.js`）。→ 150msデバウンスを追加。

## UDR棚卸し要 — 参照先不在のUDR ID (2026-07-26調査完了)

2026-07-26にリポジトリ全体を`grep`で走査し、`.udr/records/`にファイル実体を持たないUDR ID参照を確定した(コミット7ede90bで「約15件」と概算していたものを実数で確認)。**方針**: 個別の内容復元(記憶・git履歴からの再構築)はせず、`/udr-review`での棚卸しパス(orphan/dangling ID検出)に委ねる。以下は次回`/udr-review`実行時の起点として使える確定リスト。

### 現行ドキュメントが参照(実害あり、優先度中)
- `UDR-amm-20260427T0225-7a3` — `docs/build.md`/`docs/design/architecture.md`/`records/reviews/.../2026-07-26-full-repo-architecture-security-review.md`が参照(レガシー.NET版Named Pipe ACL。`UDR-amm-20260726T0839-5d2`により.NET版自体が保守終了となったため実害はさらに縮小)
- `UDR-amm-20260511T0803-a1c` — `docs/ops/runbook.md`/`SECURITY.md`が参照(セッションログの平文保存・current-user ACLという現行のセキュリティ設計判断の根拠として引用されている。**内容自体は実装済みだが判断記録が失われている**ため、優先度はやや高め)
- `UDR-amm-20260605T1124-9c4` / `UDR-amm-20260605T1251-4e2` / `UDR-amm-20260608T1102-7f2` — `docs/design/architecture.md`が参照(`7f2`は`SECURITY.md`からも参照)
- `UDR-amm-20260610T0255-d8c` / `UDR-amm-20260610T0310-b6f` / `UDR-amm-20260615T0341-c5a` — `docs/manual/faq.md`が参照
- `UDR-amm-20260618T1310-b7e` — `tasks/backlog.md`自身の「セキュリティ／コードレビュー残課題 (2026-06-18 レビュー)」節が参照(裁定済み事項の根拠)

### アーカイブ済み文書のみが参照(経緯保持用、対応不要)
`docs/design/spec/archive/{spec.md,spec-v2.md,req-20260604-tooluse-approval-hub.md,req-20260622-tray-icon.md}`(いずれも`openspec/specs/`へ統合済みの旧仕様文書、CLAUDE.md記載の「経緯保持用」)のみが参照する以下は、アーカイブ文書の性質上そのままで問題ない: `UDR-amm-20260423T1213-6c2`, `20260424T1015-9b9`, `20260424T1031-116`, `20260424T1058-0e9`, `20260427T0055-2c1`, `20260427T0159-d4e`, `20260427T0238-fb5`, `20260605T0523-7e1`, `20260605T1043-3af`。

### 除外(実在しない参照/テンプレート例示)
`AGENTS.md`の`UDR-amm-20260423T1430-a3f`、`.codex/prompts/udr-search.md`/`udr-trace.md`の`UDR-atlas-20260423T1430-a3f`は、いずれもID命名規則を示すための例示("例:"付き)であり実際の判断参照ではない。対応不要。

## セキュリティ／コードレビュー残課題 (2026-06-18 レビュー)

全ソースのセキュリティ＋シニアコードレビュー結果のうち、未対応分。`✅ 実装済` は当日修正で対応済。

### 裁定済 — 受容 / 延期 (UDR-amm-20260618T1310-b7e)
- **A-1 引数のシェル注入** → **受容**。`.amm` 作者は元々コマンド全体を指定でき autoChcp は能力を増やさない (権限境界の越境ではない)。
  信頼されない .amm の自動起動は確認ダイアログでゲート済 (UDR-7f2)。一律クォートは UDR-7f2 で棄却済のため行わない。
- **A-3 approval/notify token の接続バインド** → **受容**。公式モデルは「同一ユーザー=信頼 (OS 認証のみ)」(UDR-7a3/9c4)。
  MDI ペイン分離は UX であり境界ではない。境界化するなら別判断点で GetNamedPipeClientProcessId 検証を起こす。
- **A-4 WebView2 CSP** → **延期**。インライン script の nonce 化 + 実機 WebView2 検証が必要 (ブラインド適用は破損リスク)。実機検証可能なセッションで実施。

### 未着手 (安全。優先度低・順次)
- **A-5** Named Pipe 行読みの 1 文字 ReadAsync (`McpPipeServer.cs:151-166`) → **優先度低に再評価**。StreamReader が内部バッファを持つため per-char I/O syscall は発生せず大半が同期完了。DoS 価値は 1MiB 上限内のループ overhead に留まる。バッファ読み化は leftover 跨ぎ管理のリスクに見合わないため保留。
- **A-8** osc_notify のレート制限 → **見送り検討中**。単純な間隔ゲートは最終 idle を取りこぼし「処理中固着」を再発させる恐れ。最新状態を保持する設計が要る。
- **A-9** QuotePath/arg の内部 `"` エスケープ (A-1 を受容したため優先度低)。
- **B-4** Pipe の Contains 事前判定+二重パースを単一ディスパッチへ (`McpPipeServer.cs:176`、軽微)。
- **B-5** CreatePopupMenu の未 attach 経路で DestroyMenu (`Win32SystemMenu.cs:87`、極小・据え置き)。
- **B-10** AppLogger の ACL 適用を失敗時リトライ可に (`AppLogger.cs:105`、per-flush の I/O 増に注意)。

### ✅ 実装済 (2026-06-18)
- **B-1** profiles.amm を AtomicFileWriter 経由の原子的書込へ。
- **A-10** AtomicFileWriter の tmp をランダム名 + 例外時 best-effort 削除へ。
- **A-7** ユーザー定義 waitPatterns に matchTimeout(100ms) 付与 + RegexMatchTimeoutException 吸収 (ReDoS)。
- **A-6** TomlEscape 導入。Codex config.toml のパスに `'` が含まれても壊れない (HookCliRegistrar/McpCliRegistrar)。
- **B-7** 死コード ToInt 削除。**B-8** JsonClone 失敗時に AppLogger.Warn。
- **A-2** OSC52 クリップボード書込を部分 hardening: JS で base64 上限(128KiB) + `source:'osc52'` タグ、C# で OSC52 由来のみ 64KiB 上限 + 長さのみ監査ログ。ユーザー選択コピーは従来どおり。(完全な ON/OFF トグルは将来課題)
- **B-9** OnWebMessageReceived の malformed を例外種別のみログ (本文は出さない)。
- **B-11** WorkingDirectory 保存時の trim 漏れ是正。(safeName はタイムスタンプ前置+.log 接尾で予約名/末尾ドットが無効化されるため変更不要)
- **Info** FrameNavigationStarting も同ガードで遮断 (将来の iframe 混入対策)。
- **B-2** async void イベント経路の UI クラッシュ防止: 共通ガード RunGuarded 導入。SendInput/BroadcastInput/SendToIndexed をラッパ化し、共有経路 SendInputToTargetAsync と EditorBridge.OnDebounceElapsed の送信を try/catch で保護。
- **B-6** InferCommandType を ArgsReferToTool でトークン単位照合へ。パスに "codex"/"copilot" を部分的に含む旧 .amm の誤判定を防止。
- (B-3 は Dispose が watcher を join 後にハンドルを閉じるため実レースでないと判断し対象外)

## クローズ時の最終判断 (2026-06-18, リポジトリ ZIP アーカイブ予定)

「今回で修正終了」前提での判断: **残項目は意図的に未適用のままクローズするのが妥当。**
理由 (= 最終アーカイブ状態を壊さない方を優先):
- **A-4 (CSP)**: 実機 WebView2 で terminal.html の描画 (インライン script / xterm.js の eval 有無) を確認できない。
  ブラインド適用で端末描画が壊れると、修正できる「次回」が無いアーカイブ状態を毀損する。適用しない方がリスクが低い。
- **A-9 (QuotePath の " エスケープ)**: Windows ではファイル名に `"` を含められない (無効文字) ため実質 moot。
- **B-4 / B-5 / B-10**: それぞれ二重パース(無害)・到達ほぼ不能なメニュー生成失敗経路・per-flush I/O 増の懸念ありで、
  価値が極小。クローズ間際にセキュリティ/ログ系へ変更を入れる方がリスクが高い。
判断: セキュリティ上・安定性上の実害ある指摘は全て解消済。残りは「触らない」ことが最善。
