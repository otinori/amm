---
applyTo: "**/*"
---

# UDR Decisions — 判断記録サマリ

本ファイルは GitHub Copilot（および他エージェント）が本リポジトリ内の任意ファイル編集時に参照する **判断要約**。`/udr-sync` により自動生成される（手動編集不可）。

詳細ポリシーは `AGENTS.md`、完全な判断本体は `.udr/records/<id>.yaml` 参照。

---

## 記録 / 参照の原則

- 新規判断が発生したら、`AGENTS.md §2.3` の処理フローに従い UDR として起票
- 既存コードを変更する際は、該当領域の UDR（`domain: design/architecture/risk` 等）を `.udr/records/` で確認し、その決定を尊重する
- UDR と矛盾する実装を提案する場合、必ず user に「既存 UDR を supersede するか、既存 UDR に従うか」を確認（FR-004）
- `status: proposed` の UDR は未承認。実装の根拠として使わず、user の承認を促す

---

## 現行判断サマリ（`/udr-sync` 自動生成、編集不可）

<!-- [UDR-SYNC-START] -->
# Active UDR Decisions (21 records, synced 2026-06-18T13:13Z)

## Auto（スコア順）
- **UDR-amm-20260618T1310-b7e** [risk / accepted] 全ソースセキュリティ/コードレビューの裁定 (受容/部分対応/延期)
  決定: 全ソースのセキュリティ＋シニアコードレビューを実施。A-1(autoChcp の cmd ラップ+引数非エスケープによるシェル注入)と A-3(approval/notify token を接続元にバインドしない)は「同一ユーザー=信頼(OS 認証のみ)」+ UDR-7f2/7a3/9c4 に基づき受容。A-2(OSC52 の無操作クリップボード書込)は退行なしの部分 hardening(JS で base64 128KiB 上限+source:'osc52' タグ、C# で OSC52 由来のみ 64KiB 上限+長さのみ監査)。A-4(CSP)は nonce 化+実機 WebView2 検証が要るため延期。設計判断不要の安全項目(B-1/A-10/A-7/A-6/A-2/B-7/B-8/B-9/B-11/FrameNav)は同日修正済。
  棄却: 全指摘の即時機械修正(A-1 一律 arg クォートは UDR-7f2 で棄却済・既存 .amm 破壊、A-4 CSP は実機検証なしで端末描画破損), 裁定を記録せず放置(棄却済の一律クォート等が将来再提起され判断コスト反復)。
- **UDR-amm-20260618T1146-f2a** [design / accepted] WaitState 検出の Running 固着を沈黙フォールバック+再武装で根治
  決定: WaitPatternDetector を「出力停止 500ms 後に 1 度だけ照合し未一致なら Running 固着」する一発判定から、Running の間はタイマーを再武装して間欠再評価し、(1)プロンプト一致で WaitingForInput 確定、(2)出力が _quietToWaitingMs(既定 4000ms)以上途絶えたら未一致でも「解析できないプロンプトで入力待ち」とみなし WaitingForInput へ自力回復、(3)それ以外は再武装、へ変更。しきい値はコンストラクタ注入可。Claude/Codex 完了直後の末尾再描画でプロンプト行が照合窓(直近50行 _recentLines)から押し出され Running 固着し「MDI リサイズでしか直らない/タイトル・MDI ボタンの状態アイコン● が処理中▶ のまま最新化されない」事象を、新規出力を待たず回復させる。タイトルと●ボタンは同一 UI イベントで同時更新されるため両方固着していた。解析可能プロンプト(cmd/PowerShell/`> `ボックス)は即時/照合で確定し沈黙フォールバック非到達。Unknown には落とさない(フラップ防止方針維持)。
  棄却: UI 側の定期再描画のみ(検出器の state 自体が Running 固着で誤状態を再表示するだけ・無効), 沈黙時に Unknown へ(Running↔Unknown フラップで背景色チカチカ再発), しきい値を2-3秒へ短縮(AI の thinking/tool 中の一時的無音 gap で誤って●化), 照合窓(_recentLines 50行)拡張のみ(押し出しを緩和するだけで末尾装飾行経路が残り真因未解決)。
- **UDR-amm-20260608T1102-7f2** [risk / accepted] ソースセキュリティ監査フォローアップの hardening 一式 (.amm 信頼境界 / WebView2 / IPC / 設定書込)
  決定: 起動経路のみ BuildLaunchCommandLine で裸 exe を安全 PATH 探索 (System32 優先・相対/カレント除外) で絶対パス解決し空白パスをクォート (表示用 CommandLine は不変)、HasExplicitFile (ダブルクリック/CLI 引数/ファイル→開く) の .amm は自動起動前に実行コマンドを列挙した確認ダイアログ、WebView2 は DevTools(Release のみ無効)/既定コンテキストメニュー/アクセラレータキー/autofill 無効化 + 外部ナビゲーション・新規ウィンドウ遮断 + AdditionalBrowserArguments でランタイムのバックグラウンド通信(テレメトリ)抑止、Named Pipe は ReadLineBoundedAsync で 1MiB 行長上限 (OOM DoS 防止)、設定ファイル書込を Flush(flushToDisk)+File.Replace の AtomicFileWriter に集約、sessionLog ディレクトリに current-user 限定 ACL。
  棄却: args 一律クォート(cmd ラップ前提のシェル機能を使う既存 .amm を破壊), DevTools 常時無効(terminal.html デバッグ不能), exe 解決を CommandLine プロパティ自体に組込(表示/ログ/既存テストの後方互換), 残課題 (nickname 辞書上限/TOML literal/コード署名) 同時対応(到達経路限定 or 配布運用判断で別判断点)。
- **UDR-amm-20260511T0803-a1c** [risk / accepted] OSS 公開向け hardening と配布整備
  決定: Named Pipe を current user SID の明示 ACL に変更し、README を実装に同期、sessionLog の平文保存注意を追記、個人パスと .lnk を除去し Apache 2.0 LICENSE を追加。
  棄却: README 注意書きのみ(実装未担保)。
- **UDR-amm-20260427T0225-7a3** [architecture / accepted] AMM Operator MCP サーバ機能 (stdio + Named Pipe bridge, OS 認証のみ)
  決定: stdio MCP のみ実装、別 csproj AMMOperator.Mcp で amm-mcp.exe を提供、AMM Operator GUI 内に Named Pipe サーバ (\\.\pipe\AMMOperator-MCP-{user}, current user ACL) を常駐、MCP 公式仕様準拠で initialize/tools/list/tools/call、3 ツール (send_message/list_participants/peek_queue)、受信は入力待ち時直接注入・他キュー (1 nickname 100 件上限古い順 drop)、mode=first は入力待ち優先→起動順、ヘッダなしで素通し。
  棄却: AMMOperator.exe --mcp-bridge (誤起動事故), HTTP/SSE (要望外), 独自 JSON-RPC (Claude Code 認識不可), ヘッダ付加 (AI 誤認), 上限なし (メモリ膨張), トークン認証 (ACL で十分), mode=first を起動順固定 (応答性低下)。
- **UDR-amm-20260610T0420-e5b** [design / accepted] IME 二重送信ガードを composition 活動ベースの間隔 dedup へ再設計 (c3e を置換)
  決定: c3e (compositionSeq + compositionEndAt + 直前転送一致 + GAP) は IME 候補ウィンドウ誤配置時に (a) 2 つの重複 onData の間に余計な compositionstart が割り込み compositionSeq がズレて seq 一致が外れる、(b) compositionend が遅延・欠落し nearCommit が成立しない、で dedup が発火せず二重送信が残存した。新方式は compositionSeq も compositionend.data も使わず、composition 活動 (compositionstart/update/end のいずれか) が直近 IME_ARM_MS(600ms) 以内 or 進行中であることを IME 入力の目印 (arming) にし、その状態で「直前に前方転送したチャンクと同一テキストが GAP(250ms) 以内に再到来」したら 1 回だけ破棄する。compositionstart は変換開始で必ず発火するので誤配置時も arming が成立する。IME 非使用のキーリピート等は composition 活動が無いので影響しない。送信元 onData 1 本 / ime_diag 診断ログ維持。
  棄却: c3e の seq 条件だけ外す(compositionEndAt 依存が残り compositionend 遅延/欠落で発火漏れ), GAP 無制限で composition 中の同一チャンクを常に圧縮(時間を空けた正規再確定を誤破棄), xterm の textarea input 経路を capture で抑止し確定送出を 1 系統化(通常入力/ペーストへの副作用大)。
- **UDR-amm-20260611T0309-9a4** [design / accepted] IME 二重送信ガードに composition 非依存の hard-gap dedup を追加
  決定: terminal.html の term.onData ガードに IME arming 非依存の第 2 破棄系統を追加。破棄条件を「同一チャンク かつ ( (imeRecent かつ gap≤IME_DUP_GAP_MS=250ms) または (ESC 始まり以外 かつ gap≤HARD_DUP_GAP_MS=30ms) )」へ拡張。実ログで「を」が gap=4ms / imeRecent=false / composition トレイル 67 分前停止 で二重送信された (TSF 直接確定等で compositionstart/update/end が一切発火しない確定経路) ため、物理的に不可能な極短間隔 (<30ms、最速キーリピート ~33ms 未満) の同一チャンクを composition 非依存で破棄する。捕捉系統は ime_diag の reason を dup_dropped / dup_dropped_hard で出し分け検証可能に。ESC 始まり (矢印リピート/マウスレポート) は対象外。e5b の arming 設計を温存しその素通り経路を補完。
  棄却: IME_ARM_MS(arming 窓) 拡張(composition イベント自体が出ないので imeRecent が true にならず無効), 同一チャンクを gap 無制限で無条件 dedup(正規の連続同一入力・キーリピート・Enter 連打 gap 180-1100ms を誤破棄), hard-gap 閾値を 33ms 以上(Windows 最速キーリピート ~33ms と衝突し正規リピートを誤認), composition 不発の真因(TSF/IME 実装)を特定して根治(環境依存で重く再現不安定、hard-gap で実害を即解消し真因調査は残課題)。
- **UDR-amm-20260611T1054-d7c** [design / accepted] IME 二重送信ガードに非隣接 (forward 履歴) dedup を追加
  決定: 既存ガード (1)〜(3) は lastForward (直前1件) としか比較しないため、確定テキストと重複コピーの間に Enter 等が割り込むと sameAsLast=false で破棄も dup_passed ログもされず素通りする盲点があった (「日本語確定文字が2回出る」残存事象の正体)。forward 済みチャンクの履歴リング recentForwards (最大10件) を terminal.html に追加し、IME arming 中 (composition 進行中 or 直近 IME_ARM_MS=600ms) かつ非 ESC のとき直近 forward を IME_DUP_GAP_MS=250ms 内でスキャンし、未消費 (used=false) の同一テキストが在れば 1 回だけ破棄する系統 (4) を追加 (dup_dropped_ime_gap, histAge 付き)。used フラグで forward 済みコピーごと 1 回に限定し正規の再確定/連続入力は誤破棄しない。既存 (1)〜(3) と Backspace 経路は不変。非隣接の通過は dup_passed_hist で新規ログ化。v0.1.3.3。
  棄却: hard-gap 閾値拡張 (9a4 が棄却済み、Backspace 等の正規リピートを誤食い), sameAsLast (隣接1件比較) 据え置き (非隣接重複を構造的に取りこぼす), 履歴 dedup を窓・回数制限なしで適用 (正規の連続同一入力を誤破棄), TSF/xterm 内部の二重 onData 発火を根治 (重く不安定で PoC 範囲超の残課題)。
- **UDR-amm-20260612T0132-b4e** [design / accepted] hard-gap dedup をオートリピート除外へ是正 + 診断レート制限で OOM 防止
  決定: v0.1.3.3 後のログで IME 完全無関係 (code=127 Backspace / trail=[] / sinceComp≈8分) の事象が報告され、精査で ~22〜37ms 定常ストリーム・dup_dropped_seq 皆無 = 正規 OS オートリピート (Backspace 長押し) と判明。HARD_DUP_GAP_MS=30ms は「最速リピート33ms未満」前提だが実機リピートが 30ms を割り込み dup_dropped_hard が正規入力を誤食いしていた (9a4 が棄却理由に挙げた衝突が顕在化)。さらに長押し中の毎イベント dup_passed postMessage が C# 側 UI スレッドの同期ファイル書込を詰まらせ WebView2 メッセージキュー膨張で OOM (経路は推定)。KeyboardEvent.repeat を lastKeyWasRepeat に記録し、hard-gap 条件 (2) に !lastKeyWasRepeat を追加 (オートリピートは gap 不問で破棄せず、合成/内部二重は repeat=false のまま捕捉)、dup_passed/dup_passed_hist 診断をオートリピート時に抑止、imeDiag に 30件/秒レート制限 (超過分は捨て窓切替時に diag_suppressed を 1 行) を追加。sameKeyEvent(keySeqDelta=0) と IME arming/履歴経路は不変。v0.1.3.4。
  棄却: HARD_DUP_GAP_MS を下げるだけ(実機 ~22ms 観測・安全な固定閾値が存在せず脆い), hard-gap dedup 撤去(TSF 直接確定の真の二重を取りこぼす), 診断抑止せず AppLogger を非同期化(無制限キューで別のメモリ膨張・緩和に過ぎない), TSF/xterm 内部の二重 onData 発火を根治(環境依存で重く PoC 範囲超)。
- **UDR-amm-20260615T0341-c5a** [design / accepted] 分割確定の二重送信を受信側コアレッサ (バースト XX→X 半減) で根治
  決定: c3e→e5b→9a4→d7c→b4e の 5 回は onData の隣接比較強化で、確定文字列が 1 文字ずつ分割 (と,こ,ろ,と,こ,ろ) で二重送出される「ところところ」(keySeqDelta=0 / imeRecent=false / TSF 直接確定) には原理的に届かなかった。onData 前段に IME/TSF 確定バーストのコアレッサを新設し、キー入力を伴わない (keyDownSeq 据え置き) 非 ESC チャンクを ~24ms (BURST_FLUSH_MS) 束ね、「2 チャンク以上 かつ バッファ全体が厳密な二重 (前半===後半)」のときだけ前半 X を送る (後半の重複を捨てる)。何を何を→何を / んん→ん / ASCII 内部二重 aa→a も統一補正。通常キーボード入力 (keyDownSeq が進む) と マウス/矢印 (ESC 始まり) は素通しで遅延ゼロ、単一チャンクは非半減 (右クリック貼付 "abab" 等の誤半減防止)。paste は保留バーストを先に flush し recentForwards もクリア。既存系統(1)〜(4)・hard-gap・repeat はキーボード経路の保険として温存。新診断 burst_halved (本文非記録)。v0.1.3.6。
  棄却: xterm/TSF の二重 emit を源で根治 (vendor 改変・調査コスト大、コアレッサを採用解として見送り), 隣接比較の 6 個目系統を追加 (分割2巡目は畳語 ことこと/わくわく と内容では区別不能), 確定バーストを無条件半減 (単一チャンク貼付や奇数長を誤半減)。
- **UDR-amm-20260610T0420-f8d** [design / accepted] MDI クライアントのスクロールバー除去 (タイルは fit) + 端末スクロールバー可視化 + ホイール全モード 1 行/ノッチ
  決定: (1) TileMdiLinear を UDR-amm-20260610T0221-f4a の「最小サイズでオーバーフローさせ MDI スクロール」方式から「クライアント領域を均等割りして余白なく埋める fit 方式」へ戻し、MdiClient (MDI を配置する親) 側にスクロールバーを出さない。(2) 端末 (MDI 子) 側は xterm-viewport の ::-webkit-scrollbar / -thumb を明示 CSS 指定し、WebView2(Chromium) 暗色テーマでも可視・ドラッグ可能なつまみ (min-height 28px) にする。(3) ホイールハンドラを UDR-amm-20260610T0133-b7d の deltaMode=PAGE 限定から全モード対象へ拡張し 1 ノッチ=1 行へ正規化 (PIXEL は概ね 100px/ノッチで割ってノッチ数、LINE/PAGE は delta をノッチ数、通常画面 term.scrollLines / alt 画面は矢印キー列、mouseTrackingMode 有効時は TUI へ委譲)。
  棄却: MdiClient のスクロールバーをウィンドウスタイル除去で完全無効化(ネイティブ再付与で脆い・サブクラス要), ホイールを PAGE 限定のまま据え置き(PIXEL モードで過剰スクロールが残る)。
  ※ (2)(3) はその後 UDR-amm-20260610T0537-a3e で調整: 端末(xterm)スクロールバーは実機でつまみが出ず操作できないとの再指摘・「バー不要」回答により ::-webkit-scrollbar 幅 0 で非表示化。ホイールは換算でも多すぎとの指摘継続のため delta 量を一切見ず wheel イベント 1 回=1 行へ固定 (alt 画面は矢印キー 1 個)。スクロールバックの移動はホイール/キーに委ねる。MDI 親バー除去/タイル fit (1) は不変。
- **UDR-amm-20260610T0405-c7a** [design / accepted] CommandType 導入 (種別ドロップダウン + プリセット適用) と Nickname の MCP 名自由化
  決定: per-profile に CommandType(Other/Cmd/PowerShell/ClaudeCode/Codex/CopilotCli) を追加。コマンド編集ダイアログに常時表示の「コマンドタイプ」ComboBox を置き、選択時にそのタイプのプリセット (CommandTemplates の対応エントリ) のふるまい関連フィールド (実行ファイル/引数/wait/改行/encoding/autoChcp/bracketed paste/cwd選択/起動時改名) を適用 (名前/Nickname/作業ディレクトリ/クイック送信/配置/フォントは保持、既存内容ありは上書き確認)。ResumeArgsFor を Nickname→CommandType 起点に変更。旧 AMM ファイル (commandType 無し) は MigrateLegacyFields が nickname/executable/args から推測補完 (純粋 cmd.exe のみ Cmd、cmd /c ラッパーは Other)。Nickname は MCP 受信名として自由化し、派生 clone は新コマンド名から付与、手入力含め EscapeNickname で MCP 安全化 ('#'=participant Key 区切り・空白・制御文字を '_'、連続圧縮・前後除去、Unicode 保持)。
  棄却: 種別は挙動タグのみでプリセット適用なし(タイプ選択で設定を揃える要望に不足), Nickname に CLI 種別判定を残し CommandType と二重管理(破綻), Nickname を ASCII へ強エスケープ(日本語識別性低下・recipient は文字列照合で Unicode 可)。
- **UDR-amm-20260610T0405-d2e** [design / accepted] 右クリックメニュー外側クリッククローズ + クリップボード改行 CRLF 正規化 + ペースト回帰修正
  決定: (1) 表示中の右クリックメニューを保持し、terminal.html の document.click 由来 click_activate 受信で明示 Close する (WebView2 は別 HWND で ContextMenuStrip の外側クリック自動クローズが効かないため)。(2) paste_response ハンドラの旧 imeConfirmed 参照 (c3e で削除済) を lastForward リセットに置換し、未定義例外で term.paste に到達しなかったペースト不可の回帰を根治。(3) 入力欄を WM_PASTE で改行を単一 CRLF へ正規化する NormalizingTextBox に変更 (Ctrl+V/Shift+Insert/右クリックを一括カバー)、端末選択コピーも単一 CRLF へ正規化 (一旦 LF へ畳んでから CRLF 展開し二重化回避)。Win32 EDIT は CRLF のみ改行描画するため LF 単体だと潰れる問題に対処。
  棄却: メニューを別 HWND 対策で全面改修(過剰), 入力欄を RichTextBox へ置換(書式/IME 等の副作用大)。
- **UDR-amm-20260610T0310-b6f** [design / accepted] コマンド毎の「起動時にセッションを復帰する」チェックボックス追加 (CLI 種別で resume トークン自動選択)
  決定: SessionProfile に resumeOnStart(bool, 既定 false) を追加し、CommandTemplateDialog に「起動時にセッションを復帰する」チェックボックスを設置。EffectiveArgs() が resumeOnStart=true のとき Nickname 由来の復帰トークン (ResumeArgsFor: claude/copilot=--resume, codex=resume) を Args 末尾へ付加し、BuildCommandLine 経由で表示 CommandLine / 起動 BuildLaunchCommandLine の双方に反映。codex の resume はサブコマンドだが cmd /c codex.cmd … 形式のため末尾付加で成立。既定 OFF で d8c の「新規フォルダ初回起動で即終了」を再発させず、opt-in で安全に resume を復活。
  棄却: resumeArgs(string[]) を schema に持たせ JSON 上書き可に(CommandTemplateDialog は UI 非表示フィールドを保存時に脱落させるため、チェックは残るのにトークンだけ失われる footgun), 全 CLI で常に resume(チェックボックスなし=新規フォルダ即終了に逆戻り)。
- **UDR-amm-20260610T0255-d8c** [design / accepted] テンプレ profiles.amm の AI CLI 既定起動から --resume を撤去 (新規フォルダ初回起動で CLI が終了する問題)
  決定: UDR-amm-20260605T1251-4e2 の resume 既定起動 (claude --resume / codex resume / copilot --resume) は、保存済みセッションが無い新規フォルダで初回起動すると CLI が「再開対象なし」で即終了するため撤去。テンプレ profiles.amm の Claude args を [] に、codex.cmd / copilot.cmd 末尾の resume / --resume を削除し、3 CLI とも常に新規起動とする。組込み既定プロファイル (SessionProfile.cs) は元々 resume 引数を持たないため整合。CLI ごとのセッション検出は Claude (~/.claude/projects/<cwd>) のみ fail-safe に可能だが、Codex (~/.codex/sessions は cwd 索引なし) / Copilot (保存場所不文書) は内部実装依存で脆く、ユーザは一律撤去を選択。
  棄却: Claude のみ条件付き resume を実装し他を撤去(CLI 間で挙動が不揃い), 3 CLI とも条件付き resume(Codex/Copilot はフォルダ単位の索引が無く検出が CLI バージョン依存で壊れやすい)。
- **UDR-amm-20260610T0133-b7d** [design / accepted] マウスホイールのページ単位スクロールを正規化 (cmd/PowerShell の実挙動に追従)
  決定: ターミナル (WebView2 + xterm.js) で Windows の「ホイールで一度にスクロールする量 = 一度に 1 画面ずつ」設定 (SPI_GETWHEELSCROLLLINES=WHEEL_PAGESCROLL) のとき、WebView2 が wheel を deltaMode=DOM_DELTA_PAGE で送り xterm が「行高×行数 (=1 ページ)」倍して 1 ノッチ=1 ページになる問題を、term.attachCustomWheelEventHandler で deltaMode=PAGE かつ mouseTrackingMode==none のときだけページ送りを抑止し 1 ノッチ=1 行 (現行 OS 設定下の cmd/PowerShell の実挙動) に正規化して解消 (通常画面 term.scrollLines / alt 画面は xterm 既定同様に矢印キー列送出)。当初 3 行で実装したがネイティブコンソールに揃える要望により 1 行へ。PIXEL/LINE は素通しで OS 設定に追従。
  棄却: 「1 画面ずつ」もクラシックコンソール同様 1 ページ送りで厳密一致(使いづらい), 3 行固定(ネイティブより速い), C# で SystemInformation.MouseWheelScrollLines を読み全モード自前制御(LINE は deltaY が既に行数/PIXEL はタッチパッド精密で OS スケール済み → 二重スケール・劣化リスク、実害は PAGE のみ)。
- **UDR-amm-20260610T1009-5c1** [design / accepted] コモンダイアログ (開く / 名前を付けて保存) の初期フォルダ優先順位を確定
  決定: Core/DialogPaths.ResolveInitialDirectory を新設し、OnFileOpen / OnFileSaveAs の InitialDirectory を 3 段階優先順位で解決する — (1) amm ファイル明示指定 (クリック起動 / コマンドライン / 開く / 名前を付けて保存で確定) ならそのフォルダ、(2) 起動時 CWD が %TEMP% %TMP% %SystemRoot% %ProgramData% %ProgramFiles% %ProgramFiles(x86)% %ProgramW6432% %LOCALAPPDATA% と一致 / その配下 (未定義の環境変数は無視) なら マイドキュメント、(3) それ以外は起動時 CWD。起動時 CWD は AppLaunchOptions.StartupCurrentDirectory に起動時点の値を固定保持 (ダイアログの CWD 変更で判定がぶれない)。判定は副作用なし static + 単体テスト 10 ケース。
  棄却: 環境変数フォルダ判定を完全一致のみ (System32 / インストール先 C:\Program Files\amm 等の主要ケースで発火せず目的未達), 従来の ProfilesPath フォルダ固定 / SaveAs 常時マイドキュメント (インストール後 Program Files 提示・要望の優先順位未充足)。
- **UDR-amm-20260605T1251-4e2** [design / accepted] Codex/Copilot へのフック拡張 (Codex=Level 1 / Copilot=Level 2) + AI CLI の resume 既定起動
  決定: HookCliRegistrar を McpCliKind ベースの 3 CLI 対応に拡張 — Codex は ~/.codex/config.toml のルート notify キー (既存設定との衝突は明示エラー) + [tui] notifications を OSC9 で有効化 (amm 追記行は "# added by amm" マーカー管理) し、xterm.js の OSC9 ハンドラが approval を含む通知を attention、それ以外を idle として解釈。Copilot は専有ファイル ~/.copilot/hooks/amm-hooks.json に agentStop→notify --state idle / permissionRequest→approve --source copilot (timeoutSec 60、台帳 45s < approve 55s < hook 60s の鎖) を登録し許可集約 (Level 2) まで対応。amm-mcp approve は --source で CLI 別応答形式 (copilot は {"behavior": ...} 直書き) を出し分け。テンプレ profiles.amm は picker 形式 resume を既定起動に変更したが、この resume 既定起動は UDR-amm-20260610T0255-d8c で撤回 (新規フォルダ初回起動で CLI が即終了するため。hook 拡張・OSC9・approve 出し分けは有効のまま)。
  棄却: Codex も Level 2(ブロッキング型 hook が存在せず構造的に不可), 既存 notify の自動上書き(単一キーでユーザー設定を壊す), Copilot settings.json への inline 同居(専有ファイルなら削除=解除で単純), 自動再開形式 --continue/--last(ユーザー指定は picker 形式), Copilot notification hook 追加(イベント名なし payload の推測マッピング回避)。
- **UDR-amm-20260605T1124-9c4** [design / accepted] Approval Hub Level 2: 非モーダルポップアップによる ToolUse 許可の集約回答
  決定: Claude Code の PermissionRequest hook (許可ダイアログ表示時のみ発火) から amm-mcp.exe approve → 既存 Pipe の amm/approval で要求を送り、ApprovalBroker (id→TCS 台帳、解放トリガー 4 種: 回答 / ペインアクティブ化・クローズ / タイムアウト 45 秒 / 切断) が人間の回答を仲介。UI は非モーダル + TopMost + WS_EX_NOACTIVATE のポップアップ 1 枚 (キュー表示、500ms 誤操作ガード、既定ボタンなし)。無回答系はすべて「決定なし」で hook を解放しペイン内プロンプトへフォールバック。表示メニューに即時トグル (layout.json 永続化)。MVP は Claude Code のみ。
  棄却: PreToolUse(全ツール発火でノイズ), キー注入(プロンプト UI 依存+レース), モーダルダイアログ(amm 全体ブロック), 下部集約バー単独(背面で不可視・保留), 「今後も許可」ボタン(誤操作で恒久化)。
- **UDR-amm-20260605T1043-3af** [design / accepted] Approval Hub Level 1: attention (許可・確認待ち) の可視化 — 通知のみ
  決定: hook の attention (permission_prompt / elicitation_dialog) を TerminalChildForm.HasAttention フラグで保持し (WaitState enum は拡張しない)、タイトル ⚠ / タイトルバー・切替ボタンのオレンジ表示 (入力待ち黄より優先) / 非フォアグラウンド時の FlashWindowEx タスクバー点滅で通知する。解除はペインのアクティブ化・Running 遷移・idle/busy 通知。回答はペイン内のまま (Level 2 = 集約回答は別判断)。
  棄却: Level 2 同時実装(同期ブロック+タイムアウト設計が重い・段階導入), WaitState enum 拡張(状態機械と IsWaiting 意味論を汚す), トースト通知(WinRT 依存過剰)。
- **UDR-amm-20260605T0523-7e1** [design / accepted] hook 駆動の入力待ち検知 (CLI hooks → amm-mcp notify → Named Pipe)
  決定: Claude Code の Stop / Notification hook から amm-mcp.exe notify (新サブコマンド) を起動し、既存 Named Pipe の新メソッド amm/notify で amm GUI に状態を push。MDI の特定は ConPTY 起動時に注入する環境変数 AMM_NOTIFY_ID の子プロセス継承で行い、env 不在 (amm 外の CLI) は no-op。waitPatterns は fallback として併存。MVP は Claude Code のみ (hook 登録は既存 McpCliRegistrar パターン踏襲の HookCliRegistrar、[CLI への MCP / フック登録...] ダイアログ統合)。
  棄却: MCP で状態通知(プロトコル不適合), waitPatterns 強化のみ(描画依存の限界), 専用 hook exe 新設(配布物増), 設定ファイルに識別子(per-session 不能), 3 CLI 一括(Codex 衝突/Copilot ラッパー要で後続)。

<!-- [UDR-SYNC-END] -->

---

*最終更新: 2026-04-23 / 骨格作成時点、未同期*
