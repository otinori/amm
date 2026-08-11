## 1. 足場作り

- [x] 1.1 `Cargo.toml` に `[target.'cfg(unix)'.dependencies]` を追加(`libc`または`nix` crate、`killpg`/`setpgid`用)
- [x] 1.2 `native_ui_macos.rs` を新設し、`lib.rs`から`#[cfg(windows)]`/`#[cfg(target_os = "macos")]`の薄いディスパッチで呼び分ける構造にする
- [x] 1.3 `tauri.conf.json`にmacOS向けの`bundle.macOS`/`bundle.dmg`設定を追加(bundle identifier等、cargo-tauriのデフォルトを確認の上調整) — `bundle.targets`を`"all"`へ変更(macOS実機ビルドで`.app`/`.dmg`が生成されることを確認済み)。`bundle.macOS`は既定値(identifier=`com.otinori.amm`は元から設定済み)で問題なかったため追加設定なし
- [x] 1.4 Mac実機で`cargo build`/`cargo check`が通ることを確認 — `cargo check -j 2`/`cargo build -j 2`とも実機でクリーンに成功(pre-existing dead-code警告以外の警告・エラーなし)

## 2. IPCトランスポート(Named Pipe → Unix domain socket)

- [x] 2.1 `mcp.rs`に`#[cfg(unix)]`版のサーバ起動関数(`tokio::net::UnixListener`、`$TMPDIR/amm-mcp-{uid}.sock`)を実装、ソケットファイルを`0600`・親ディレクトリを`0700`で作成
- [x] 2.2 `bin/amm-mcp/pipe_client.rs`に`#[cfg(unix)]`版のクライアント接続関数(`UnixStream`)を実装、`AMM_MCP_SOCKET_PATH`環境変数での上書きに対応
- [x] 2.3 NDJSONフレーミング・JSON-RPCハンドシェイク・1MiB行長上限のロジックがトランスポート非依存であることを確認 — `handle_connection`を`AsyncRead+AsyncWrite`ジェネリックに変更し完全共有化(Windows/Unixで同一実装)
- [x] 2.4 単体テスト追加: Unix domain socketの複数同時接続、1MiB超ペイロード切断、ソケットファイルのパーミッション確認 — `bind_unix_socket`を`spawn_server`から切り出しテスト可能にした上で4件追加(`mcp::unix_socket_tests`)、実プロセス/実ソケット経由で確認
- [x] 2.5 Mac実機で`amm-mcp list`等が新GUIへ実接続できることを確認 — `.app`バンドル経由で起動したGUIに対し`amm-mcp list`が`[]`(空の参加者一覧)を実際に返すことを実機確認済み。ソケットの実パーミッションも`drwx------`(dir)/`srw-------`(socket)を確認

## 3. プロセスツリー管理(Windows Job Object → プロセスグループ+シグナル)

- [x] 3.1 `gateway.rs`の外部MCPサーバプロセス起動箇所に`#[cfg(unix)]`版を実装(`cmd.process_group(0)`、tokioのCommandは`std::os::unix::process::CommandExt`のimport不要で直接呼べることを確認)
- [x] 3.2 終了経路(通常終了・異常終了・ammクラッシュ)全てで`killpg`(SIGTERM→猶予後SIGKILL)を呼ぶことを確認・実装 — 既存の`close_job()`呼び出し箇所(通常終了/異常終了時の両方)がそのままUnix版`close_job_handle`を通るため追加配線不要だった
- [x] **重大な見落としを追加修正**: `gateway.rs`と全く同名・同パターンの`assign_kill_on_close_job`/`close_job_handle`が`pty.rs`(security: H-5、**ペイン自身のCLIエージェントプロセス+子孫の強制終了**)にも別途存在し、こちらは修正漏れで空スタブのまま残っていた(=macOS版ではペインを閉じても子孫プロセスが残留する実害バグ)。design.md D3に追記の上、`portable_pty`spawnは既に自分自身のプロセスグループリーダーである(`process_group(0)`不要)ことを実機確認し修正、`pty::unix_process_group_tests`(2件)を追加
- [x] 3.3/3.4 単体テスト追加: 実際に`/bin/sh`子プロセス+バックグラウンド化した孫プロセス(`sleep`)を実際にspawnし、`assign_kill_on_close_job`/`close_job_handle`(実装そのもの)経由で`killpg`した際に子・孫の両方が終了することを実機確認するテストを追加(`gateway::unix_process_group_tests`)。**実機での重要な発見**: `kill(pid, 0)`は自分自身の子プロセスがゾンビ化(終了済みだが`wait()`未実行)している間もmacOSでは成功(生存扱い)を返し続けるため、直接の子プロセスの終了確認には`kill(pid,0)`ではなく`child.try_wait()`/`child.wait()`を使う必要があると判明(孫プロセスは自分の子ではないため`kill(pid,0)`で正しく判定可能、この非対称性がテスト失敗の原因だった)。`tasks/retro-pending.md`参照

## 4. システムメニュー代替UI(macOS、ウィンドウ内)

- [x] 4.1 ペインタイトルバーの右クリックコンテキストメニューに、Windows版システムメニューと同等の項目(名前変更/エディタ連携/エディタ連携ファイルパスをコピー/フォントサイズ/AMM設定)を追加 — **実装済みであることが判明**: `pane-lifecycle.js`のペインタイトル右クリックメニューが`runSystemMenuAction`共有関数経由で既に全項目(名前変更/フォントサイズ/エディタ連携/エディタ連携パスコピー/AMM設定)を提供済み(Windows開発時に別の目的で追加されていたUIが、そのままmacOSのシステムメニュー代替として機能する)。新規コード不要
- [x] 4.2 各項目の実行結果がWindows版のシステムメニュー経由と同じコマンド呼び出しに落ちることを確認 — `runSystemMenuAction`は`native_ui.rs`のOSシステムメニュー経路とペインタイトルバー右クリック経路の両方から呼ばれる共有実装であることをコード確認済み
- [x] 4.3 Mac実機でコンテキストメニューの表示・各項目の動作を確認 — `cliclick`で右クリックし、名前変更/フォントサイズ▶/チャット記録/統計情報▶/エディタ連携/エディタ連携パスコピー/AMM設定...の表示、フォントサイズ16pt選択で実際にペインの表示サイズが変わることを実機確認済み

## 5. フォーカス前面化・attention通知(macOS)

- [x] 5.1 `native_ui_macos.rs`に`osascript -e 'tell application id "<bundle-id>" to activate'`をfire-and-forgetで叩く関数を実装
- [x] 5.2 トレイクリック・トースト通知クリック・承認要求通知(`d2a1b70`で追加された経路)の前面化呼び出しをmacOSでは上記関数へ差し替え — `native_ui.rs`の共有`show_main_window()`に集約して差し替え(トレイクリック・トースト通知クリックの両経路がここを通るため一箇所の修正で両方カバー)
- [x] 5.3 Dockバウンス通知(`request_user_attention(Informational)`相当のTauri API)をattention状態遷移時に発火するよう実装 — `flash_window`コマンド(`pane-layout.js`の`setAttention`から呼ばれる、pane-management/approval-hub共有経路)にmacOS分岐を追加
- [x] 5.4 Mac実機で「他アプリが前面の状態からのamm前面化」が実際に成功することを確認 — **重要な発見**: 生バイナリ直接execでは`osascript`が`tell application id "com.otinori.amm"`を解決できず失敗(Launch Servicesに未登録のため)、正しく`.app`バンドル化+`open`起動した状態では`osascript activate`が exit 0 で成功することを実機確認(過去のAvalonia版PoCの教訓通り、`.app`バンドル化の重要性を再確認)
- [x] 5.5 `osascript`ベースの通知/前面化が「システム設定→通知→スクリプトエディタ」権限に依存する既知の制約をドキュメント化 — design.mdのRisks節に記載済み

## 6. Mac版ビルド・インストーラ

- [x] 6.1 `tools/publish-tauri.cmd`相当のMac版ビルドスクリプト(シェルスクリプト)を新設 — `tools/publish-tauri-macos.sh`(plain `cargo build`+amm-mcpステージング+破損サニティチェック)と`tools/build-installer-tauri-macos.sh`(`cargo tauri build`+破損時自動パッチ+ad-hoc署名+`.dmg`失敗時のgraceful degradation)を新設、実機で2回連続実行しどちらも正常終了することを確認
- [x] 6.2 `cargo tauri build`でmacOS向け`.app`/`.dmg`を実際に生成できることを確認 — `.app`は実機生成・署名・`open`起動・IPC接続・`osascript activate`まで全て確認済み。`.dmg`は`cargo-tauri`の既知バグ(後述)とFinder自動化権限の2つの障害に遭遇、tasks/retro-pending.md参照
  - **重大な発見1**: `cargo tauri build`が`artifacts/target/release/amm`(トップレベル)を`amm-mcp`バイナリの内容で上書きする破損バグを実機で再現(Windows版で既知だった`tasks/retro-pending.md`2026-07-26のバグと同一、Mac実機でも発生 = OS非依存の`cargo-tauri`自体のバグと判明)。ビルドスクリプトに自動検出・自動パッチ(`deps/amm-<hash>`から復旧)を組み込み済み。パッチ対象の選定は**mtime(最新)基準**にする必要がある点に注意(サイズ基準だと`cargo tauri build`内部の`--features tauri/custom-protocol`付きビルドと素の`cargo build`の2種類が`deps/`に混在し、誤って古い方を選びかねない。実機で両基準を比較し発覚・修正済み)
  - **既知の制約2**: `.dmg`のFinder装飾ステップ(`bundle_dmg.sh`内のAppleScript)がこのセッションの非対話的環境ではmacOSのオートメーション権限が無く`AppleEventがタイムアウト`で失敗する。`.app`自体の生成には影響なし。ビルドスクリプトは`.dmg`失敗を致命的エラーにせず`.app`のみで正常終了する設計にした。ユーザーの対話的セッションでの再実行、または権限付与後の再実行で`.dmg`も生成されるはず
- [x] 6.3 ad-hoc署名(`codesign --sign -`)を適用し、Gatekeeper警告が出た場合の初回起動手順(右クリック→開く、`xattr -d com.apple.quarantine`)をドキュメント化 — ad-hoc署名はビルドスクリプトに組み込み済み・`codesign --verify`で実機検証済み。ユーザー向け初回起動手順のドキュメント化は9.2(README/CLAUDE.md更新)へ回す
- [ ] 6.4 実機で`.dmg`からのインストール・`.app`の`open`起動・アンインストール(`.app`削除)をE2Eで確認(`.dmg`生成がこのセッションの環境制約で未完了のため保留、ユーザーの対話的環境での確認待ち)
- [x] 6.5 `docs/build.md`にMac版のビルド・配布手順を追記 — 「macOS版(開発中)」節を新設済み(9.2と併せて実施)

## 7. Amm.PowerShellのmacOS対応 — 対象外(2026-07-30ユーザー判断)

`Amm.PowerShell`はWindows版のみの機能と割り切ることが決まった。一度`Open-AmmUnixSocketConnection`(`System.Net.Sockets.Socket`+`UnixDomainSocketEndPoint`)を実装したが、この決定を受けてrevert済み(コミット`116985f`)。`specs/ps-module/`のspec deltaもこのchangeから削除し、proposal.md/design.mdのOpen Questionsから対応するエントリを解決済みに更新した。将来pwsh on macOSでの需要が具体化したら別changeとして再検討する。

## 7.5 実機`cargo test`で判明した既存バグの修正(スコープ外だが発見・修正)

- [x] `mcp.rs`の`await_approval_or_disconnect`が`tokio::select!`の非biased順序に起因するレース条件を持ち、クライアントが承認要求の待機開始と同時/直前に切断した場合に即時解放されず45秒の内部タイムアウトを待ちきってしまうバグを発見・修正(`biased;`追加)。macOS固有ではなくWindows版にも存在した潜在バグ(`tasks/retro-pending.md`参照)
- [x] `profile.rs`の`safe_search_path`がPATH区切り文字`;`をハードコードしており、macOS/Linuxの`:`区切り`$PATH`では常に無言でフォールバックし、Windows専用のPATHハイジャック対策(絶対パス以外のPATHエントリをスキップ)がmacOS上では実質機能していなかったバグを発見・修正(`std::env::split_paths`+Unix版は実行ビット判定の`is_executable_file`を新設)
- [x] 上記に伴い、Windows専用の前提(`cmd.exe`/`System32`/`chcp`)を無条件に検証していた2件のテスト(`resolve_executable_path_resolves_bare_name_via_system32`/`chcp_wrapped_command_blocks_metachar_injection`)を`#[cfg(windows)]`化、Unix版のPATH解決テストを新規追加。Mac実機で`cargo test`が142件全てpassすることを確認

## 8. 実機でのfeature parity検証

- [x] 8.1 全16 capabilityのMac版棚卸し表(実機で確認できた範囲、Windows版`PARITY-AUDIT.md`ほど網羅的な項目別チェックリストではない簡易版):

  | capability | 状態 | 備考 |
  |---|---|---|
  | mcp-server | ✅ 確認済み | IPC(Unix socket)・パーミッション・複数同時接続・1MiB切断を単体テスト+実機で確認 |
  | pane-management | ✅ 確認済み | 日本語表示・コンテキストメニュー・フォントサイズ変更・ドラッグ&ドロップ修正を実機確認。**重大バグ発見・修正**: プロファイル未指定の新規ペイン作成が`COMSPEC`/`"powershell.exe"`フォールバックでmacOSでは確実に失敗する状態だった(D10)。修正後、`pane/open`(profile_name未指定・command未指定)をamm-mcp CLI経由で実際に呼び出し、`$SHELL`(zsh)フォールバックで正常にペインが起動することを実機で再確認済み |
  | tray-icon | 🟡 部分確認 | フォーカス前面化(`osascript activate`)は実機確認済み。トレイアイコン自体のクリック/メニュー個別動作は未確認(macOSメニューバーのNSStatusItemは合成クリックでの検証が困難) |
  | mcp-gateway | ✅ 確認済み | プロセスグループ終了(`killpg`)を実プロセスツリーで単体テスト確認済み。**関連バグ修正**: `global_config_path()`の`LOCALAPPDATA`前提バグをD10で一括修正。実機で「MCPゲートウェイ設定」から実際にstdioサーバー(`amm-mcp --bridge`自身をテスト対象として利用)を登録・再起動し、`✓ 実行中 (6 ツール)`と正しくinitialize/tools/listハンドシェイクが成功することを確認済み。あわせて保存後の通知文言に`amm.exe`というWindows専用の.exe拡張子がハードコードされていたバグも発見・修正(`amm`表記へ) |
  | approval-hub | ✅ 確認済み | Level 2承認オーバーレイを`amm-mcp approve --source claude`(`AMM_NOTIFY_ID`経由)で実際に発火させ、「⚠ (1/1件) Bash {command}」+「はい/確認」ボタンが正しく表示され、「はい」クリックで`{"hookSpecificOutput":{"decision":{"behavior":"allow"},"hookEventName":"PermissionRequest"}}`が実際に返却されることを実機で確認済み。Dockバウンス(Level 1)はコード実行のみ確認、目視での動作(アニメーション)は自動操作環境では捕捉困難なため未確認のまま |
  | wait-detection | ✅ 確認済み | `AMM_NOTIFY_ID`経由のidle/busy/attention状態遷移が実機で正しく反映されることを確認(pane-managementのタイトル絵文字/色経由) |
  | editor-integration | ✅ 確認済み | **重大バグ発見・修正**: 既定モード(関連付けアプリで開く)の`shell_execute_open`がmacOSで真の空スタブだった(D9)。`open`コマンド呼び出しへ修正し、実機でペイン内容を一時ファイル経由でXcodeに開けることを確認済み |
  | profile-schema | ✅ 確認済み | バンドル同梱`profiles.amm`のロード・追加/編集フォーム(ネストしたセクション含む)の表示を実機確認。**重大バグ発見・修正**: `resolve_profiles_path`のバンドル探索漏れ(既出)に加え、今回新たに(1) `default_cmd()`(profiles.amm欠損時のフォールバック)が`cmd.exe`をOS分岐無しで返す、(2) `pty.rs`の`auto_chcp`分岐に`cfg`が無く`autoChcp:true`なプロファイルがmacOSでも`cmd.exe`ラップを試みて確実に失敗する、(3) 配布用既定`profiles.amm`自体がCMD/PowerShell/`claude.exe`等Windows専用内容を両OSへ無条件同梱、の3件を発見・修正(D12)。`profiles.macos.amm`+`tauri.macos.conf.json`で解決し、実機で「コマンド▶」メニューがzsh/Claude Code/Copilot/Codex/Geminiを正しく表示、zshペイン起動、`autoChcp:true`でも(cmd.exeを介さず)正常起動することを確認済み。「コマンドを管理」ダイアログでの追加(新規プロファイルがリストに反映)・削除(リストから除去)・キャンセル(無保存で破棄、再オープンで元の5件に復元)を実機で確認済み。並び替え(↑/↓)は単体では未確認だが同一リストレンダリング機構であり低リスクと判断 |
  | chat-recording | 🗑️ 機能削除済み | 実機でchatRecord有効プロファイルからコマンド送信→出力静穏後、`<workDir>/.amm/logs/<yyyyMMdd>/<profile>-<timestamp>-<hex>.json`とその`index.json`が仕様通りの構造(id/profile/mdi_name/sent_at/responded_at/duration_ms/command/response_tail)で書き出されることを確認済み。副産物として`.amm/stats/<yyyyMMdd>/<profile>-<profile>.json`(quick-stats)も同時に生成されることを確認。注記(プラットフォーム非依存、macOS固有ではない): `response_tail`にbracketed paste modeのANSIシーケンス(`\e[?2004h`/`l`)がストリップされず残存している件は既知事項として記録のみだったが、2026-08-03ユーザー要望によりチャット記録・統計情報機能自体を全面削除(`openspec/specs/chat-recording/`含む)したため、上記確認内容は歴史的記録としてのみ残す |
  | auto-send-idle | ✅ 確認済み | 実機でautoSendOnIdle(enabled/prompt/delayMs)を設定したプロファイルでRunning→WaitingForInput遷移毎にカウントダウン(`⏱Ns`表示)後プロンプトが自動送信されることを確認。同一アイドル中の再発火防止・次のRunning遷移での再アームも仕様通り動作(自動送信されるコマンド自体が新たな遷移を生む設定にした結果、意図せず繰り返し発火する挙動も観察したが、これは仕様通りの正しい動作でありバグではない) |
  | command-import-export | 🟡 部分確認 | エクスポート/インポートのトリガー自体は実機確認したが、ネイティブのNSSavePanel/NSOpenPanel(ファイル選択ダイアログ)が本セッションの自動操作環境からの合成クリック・キー入力を一切受け付けず(Tauri自前のwebviewダイアログ/NSAlertは応答する)、ファイル選択を伴う完了までは検証できなかった。macOSのセキュリティ機構による既知の制約とみられ、amm側のバグではない可能性が高いが実際のファイル選択込みの動作は人間による目視確認が必要 |
  | git-integration | ✅ 確認済み | **重大バグ発見・修正(D13)**: `get_repo_root()`がgitの出力(フォワードスラッシュ)を無条件でバックスラッシュへ変換しており、macOSでは実在しないパスを返すため`git status`が常に空を返し、**ペインクローズ時のGitガード自体が無音で常にスキップされる**状態だった。`cfg!(windows)`で分岐し解消。実機で未コミットファイルのあるリポジトリを作業ディレクトリにペインを閉じ、コミット確認モーダルの表示・「コミットして続行」選択での実際のコミット実行・作業ツリーがcleanになることまで確認済み |
  | hook-cli | ✅ 確認済み | **最重要バグ発見・修正(D11)**: 登録するhookコマンド自体が無条件で`cmd /c if exist ...`(Windows専用)を構築しており、**macOSではhook機能が登録できても実行時に必ず失敗する**根本的な機能不全だった。POSIXシェル版(`test -x && exec`)へ修正、検出ロジック(`.exe`必須の正規表現/部分一致4箇所)も合わせて修正。加えて`resolve_home_dir()`(`USERPROFILE`のみ)・`resolve_mcp_exe_path()`(`.app`バンドルでの所在誤り)も修正済み。単体テスト2件追加、実機でUIからClaude Code向けhook/MCPを登録し`~/.claude.json`(mcpServers)・`~/.claude/settings.json`(hooks)に`.exe`/`cmd.exe`を含まない正しいエントリが書き込まれることを実ファイルで確認済み |
  | input-history | ✅ 確認済み | **関連バグ修正**: `history_path()`が使う`LOCALAPPDATA`前提バグをD10で一括修正(下記参照)。実機で共通入力欄からCtrl+S送信→Ctrl+Hで送信履歴ドロップダウンに正しく表示されることを確認、`~/Library/Application Support/amm/history.json`への永続化(アプリ再起動をまたぐ保持)も実ファイルで確認済み |
  | quick-command-register | ✅ 確認済み | 右クリックのクイックプロンプト登録(「continue」)がサブメニューに正しく表示されることを実機確認済み |
  | ps-module | N/A | 2026-07-30ユーザー判断によりWindows版のみの機能、macOS対応スコープ外 |
- [x] 8.2 実機UI検証は`.app`バンドル化+`open`起動を徹底し、生バイナリ直接execでの検証は行わない — 徹底済み。`cliclick`/`osascript System Events`でボタン・右クリックメニュー・クリップボード貼り付けを実機操作し確認
- [x] 8.3 ペインの日本語表示・xterm.jsの描画・pty往復がMac実機で問題ないことを確認 — クリップボード貼り付け(Cmd+V)経由で`echo 日本語テスト...`を実行し文字化けなしで往復することを確認済み。**注記**: `osascript keystroke`コマンドでの直接タイプは非ASCII文字を正しく送れず盛大に文字化けする(amm側のバグではなく`keystroke`自体の既知の制約、必ずクリップボード貼り付けで検証すること)。実際のIME変換(かな漢字変換の複数キーストローク)そのものはこのセッションでは未検証
- [x] pane-managementのmacOS代替コンテキストメニュー(4.3)を実機確認 — 右クリックで「名前変更/フォントサイズ▶/チャット記録:OFF/統計情報▶/エディタ連携/エディタ連携ファイルパスをコピー/AMM設定...」が表示され、フォントサイズ16pt選択で実際に表示サイズが変わることを確認
- [x] `AMM_NOTIFY_ID`環境変数がペインのシェルに正しく注入されることを実機確認(`pane/open`のsession_idと完全一致)
- [ ] attention状態のDockバウンス(5.3)は`amm-mcp notify --state attention`発火まで確認したが、Dock自体がスクリーンショットに写らず(auto-hideの可能性)バウンス自体の目視確認はできず。次回実機で要再確認
- [x] **新規発見**: このMac実機で`amm`アプリの通知(Notification Center)権限がデフォルトで「オフ」になっていることを発見(システム設定→通知→amm)。`amm-mcp notify --state attention`実行時、実際の通知内容ではなくmacOSの汎用「"amm"の通知」初回許可バナーのみが表示された。ad-hoc署名・未notarization状態のビルドではこうなりうる。ユーザー向けドキュメント(9.2)に「初回はシステム設定で通知を許可してください」という案内を追記する必要がある
- [x] 8.4 発見した不具合を都度修正し、`tasks/retro-pending.md`に教訓を記録 — このchangeの実装・検証を通じて継続実施(承認レース条件・PATH解決バグ・バイナリ破損・keystroke/座標系の教訓・zombie判定の教訓など計7件超を記録・修正)。今後も残りの未確認項目(8.1の⬜)を検証する過程で継続する運用そのものは今回で終わりではない

## 9. ドキュメント更新

- [x] 9.1 `docs/design/cross-platform-feasibility.md`の結論を反映 — 2026-07-30追記として実行フェーズへの移行・実機確認済み範囲を記録(アーカイブ化はchange完了後に実施予定、現時点では背景資料として残す)
- [x] 9.2 `README.md`/`CLAUDE.md`/`docs/build.md`の対応プラットフォーム記載をMac版対応中に更新 — README「対応プラットフォーム」節・CLAUDE.md冒頭・docs/build.mdへの「macOS版(開発中)」節新設(ビルド手順・既知の問題・通知権限の初回案内を含む)
- [x] 9.3 `HANDOVER.md`に本changeの完了状況・次にやること(Windows版再検証の引き継ぎ事項含む)を記録 — 「現在地」を2026-07-30時点に更新(旧内容は2026-07-26セッションの節へ退避)、「次にやること」に本change最優先項目とWindows側保留項目を整理
- [x] 9.4 未決事項についてユーザーと合意 — notarizationは行わない(ad-hoc署名のみ)、配布形態は`.dmg`のみ、対応OSバージョン下限はTauri既定値のまま。design.mdのOpen Questionsに記録済み
