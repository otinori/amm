## Context

現行のTauri実装(`src/apps/Amm/src-tauri`)は、`docs/design/cross-platform-feasibility.md`(2026-07-20更新)の実態調査により、Windows依存が以下5箇所の`#[cfg(windows)]`ブロックに閉じていることが判明済み(該当箇所は`native_ui.rs`(システムメニュー・タスクバー点滅、十数箇所)・`mcp.rs`/`bin/amm-mcp/pipe_client.rs`(Named Pipe IPC)・`gateway.rs`(Job Object)・`editor_bridge.rs`(該当なし、Launch Constraintsは実装ではなく実行時OS挙動)・`Cargo.toml`の`[target.'cfg(windows)'.dependencies]`(`windows` crate))。Tauri本体・`portable-pty`・`tauri-plugin-notification`・tray-icon機能・xterm.jsは追加実装なしでMacでも動く。

`reference/mac-avalonia-poc-lessons/`には、別アーキテクチャ(Avalonia)による破棄済みMac PoCの実機教訓(macOSのフォーカススティール防止対策・SIP Launch Constraints)が保存されている。アーキテクチャは異なるがOSレベルの制約は今回のTauri実装にも直接刺さるため、本designはこれらを踏まえた実装方針を定める。

## Goals / Non-Goals

**Goals:**
- Windows版の5箇所のWindows専用実装それぞれに、Mac相当の実装を`cfg(target_os = "macos")`で追加し、Windows版のコードパス・動作・配布物は一切変更しない。
- Windows/Mac/Linuxで共有できるコード(Tauri本体・pty・通知・UI大部分)はそのまま共有し、プラットフォーム固有コードの置き場所を明確にする(既存の`native_ui.rs`パターンを踏襲し、新規プラットフォーム固有ファイルを追加する形で分離、`cfg`の散在を最小化する)。
- macOS版の`.app`/`.dmg`をcargo-tauriのバンドル機能で生成し、Windows版の`tools/publish-tauri.cmd`/`tools/build-installer-tauri.cmd`に相当するMac版ビルド・配布フローを整備する。
- Mac版をWindows版とのfeature parityに到達させ、実Mac機での動作確認を完了させる。

**Non-Goals:**
- Linux版の実装・実機検証(将来課題。`cfg(unix)`側で自然にMacと共有できる箇所はそうするが、Linux固有の検証は行わない)。
- Windows版の新機能追加・不具合修正(本changeのスコープ外。Windows版は「後日ユーザーが再度実機確認する」までコード凍結)。
- App Store配布・Apple Developer Program加入が前提のnotarization(下記Open Questionsで扱う。今回はひとまず未署名/ad-hoc署名での配布を前提に進める)。

## Decisions

### D1. プラットフォーム分岐の置き場所: 既存の`native_ui.rs`パターンを踏襲し、機能別に`_macos`サフィックスの新規ファイルを追加
`native_ui.rs`(タスクバー点滅・システムメニュー)は既にWindows専用ロジックをlib.rsから分離済みのファイル。ここに`#[cfg(target_os = "macos")]`版の関数を同居させるのではなく、`native_ui_macos.rs`(ウィンドウ内代替UI・Dockバウンス・osascript前面化)を新設し、呼び出し側(`lib.rs`)で`#[cfg(windows)] use native_ui::...` / `#[cfg(target_os = "macos")] use native_ui_macos::...`という薄いディスパッチのみを持たせる。同様にIPCトランスポートは`mcp.rs`内に`#[cfg(windows)]`/`#[cfg(unix)]`のペア関数(`create_pipe_server`/`create_unix_socket_server`等)を追加し、`bin/amm-mcp/pipe_client.rs`も同様にペア化する。
**棄却した代替**: 全プラットフォーム分岐を一つの`platform/`モジュールツリー(`platform/windows.rs`/`platform/macos.rs`/`platform/unix.rs`)に集約する案。既存コードとの差分が大きくなり(native_ui.rs等の呼び出し元を含む大規模リファクタが必要)、5箇所の局所的な差し替えという実態に対して過剰。既存パターンを踏襲する方が変更範囲を最小化できる。

### D2. IPCトランスポート: Named Pipe → Unix domain socket、プロトコル(JSON-RPCフレーミング)は変更しない
`tokio::net::UnixListener`/`UnixStream`を使い、ソケットパスは`$TMPDIR/amm-mcp-<uid>.sock`(Windowsの`\\.\pipe\amm-mcp-<user>`に相当)。Named Pipe版で行っているACL制限(SDDL、`UDR-amm-20260726T0745-7b1`)に相当する保護として、ソケットファイルのパーミッションを`0600`(所有者のみ読み書き)に設定し、親ディレクトリも`0700`で作成する(Unixソケットのアクセス制御はファイルシステムパーミッションに従うため)。

### D3. 子プロセスツリー終了: Windows Job Object → プロセスグループ + シグナル
`gateway.rs`の外部MCPサーバプロセス起動時、Unix系では`tokio::process::Command::process_group(0)`(新規プロセスグループの先頭にする)で起動し、終了時は`libc::killpg`で`SIGTERM`→猶予後`SIGKILL`のエスカレーションを行う。Windows Job Objectの「ハンドルを閉じれば子孫プロセスも道連れに終了する」という自動性は無いため、gateway側の終了経路(通常終了・異常終了・amm自体のクラッシュ)全てで明示的に`killpg`を呼ぶ必要がある点に注意。

**同一パターンが`pty.rs`にも存在(2026-07-30実機検証で発見)**: `gateway.rs`と全く同じ`assign_kill_on_close_job`/`close_job_handle`という関数名・実装パターンが、`pty.rs`にも**別途**存在していた(security: H-5、ペイン自身のCLIエージェントプロセス+その子孫を強制終了する仕組み)。当初のcfg分岐実装(1〜3)ではgateway.rs版のみ修正しこちらを見落としており、macOS版では「ペインを閉じても子孫プロセスが残る」という実害のあるバグとして残っていた。修正時に判明した重要な違い: `portable_pty`でspawnされた子プロセスは(pty自身のジョブコントロールが機能するために必須の前提として)**既に自分自身のセッション/プロセスグループリーダーになっている**(`pgid == pid`を実機で確認済み)ため、`gateway.rs`と異なり`process_group(0)`を明示的に呼ぶ必要が無く、`assign_kill_on_close_job`は`child.process_id()`を取得するだけでよい。単体テスト(`pty::unix_process_group_tests`)でこの前提とkillpgによる終了の両方を実プロセスで検証済み。

### D4. システムメニュー拡張 → macOSはウィンドウ内UIへの作り替え(UI/UX変更許容)
macOSにはWin32の「システムメニュー」に相当する概念が無い(ウィンドウの装飾自体はOS標準のトラフィックライトのみ)。「名前変更」「エディタ連携」「エディタ連携ファイルパスをコピー」「フォントサイズ」「AMM設定」の5項目は、既存のペインタイトルバー右クリックコンテキストメニュー(`UDR-amm-20260723T0302-4a9`のトップバー化と同じ思想)に統合する。Windows版のシステムメニュー自体は変更しない(cfgで分岐するのみ)。

### D5. タスクバー点滅 → Dockバウンス(`request_user_attention`)
Tauriの`WebviewWindow::request_user_attention(Some(UserAttentionType::Informational))`がmacOSではDockアイコンのバウンスに対応する(Tauri公式APIとして提供、Cocoa直叩き不要と判明)。Windows版は引き続き`FlashWindowEx`を使用。

### D6. 前面化操作 → macOSは`osascript activate`経由に切り替え
`reference/mac-avalonia-poc-lessons/README.md`に記録済みの実機教訓の通り、`window.show()`/`unminimize()`/`set_focus()`だけでは他アプリが前面の状態からの前面化に失敗しうる(近年のmacOSのフォーカススティール防止強化)。`native_ui_macos.rs`に`osascript -e 'tell application id "<bundle-id>" to activate'`をfire-and-forgetで叩く関数を実装し、`lib.rs`/`native_ui.rs`の前面化呼び出し箇所(トレイクリック・トースト通知クリック・承認要求通知等、`d2a1b70`で追加された経路含む)をmacOSではこちらへ差し替える。bundle identifierは`tauri.conf.json`の値を使う。

### D7. macOSビルド・配布: cargo-tauriの`.app`/`.dmg`バンドル、署名はひとまずad-hoc
`cargo tauri build --target <arch>`(Windows版と同様`-j 2`相当の並列度制約は要調査)で`.app`を生成し、`.dmg`はTauriのbundler設定(`tauri.conf.json`の`bundle.macOS`/`bundle.dmg`)で生成する。Apple Developer Program証明書によるnotarizationは今回のスコープ外(Open Questions参照)とし、ad-hoc署名(`codesign --sign -`)のみ行う。Gatekeeperの「開発元を確認できません」警告が出ることをドキュメント化し、初回起動手順(右クリック→開く、または`xattr -d com.apple.quarantine`)をユーザー向け案内に含める。

### D8. 実機UI検証は`.app`バンドル化+`open`起動を徹底
`reference/mac-avalonia-poc-lessons/README.md`記載の教訓(生バイナリ直接execでツールバー等が描画されない既知の罠)を踏まえ、tasks.mdの実機検証手順は必ず`tools/build-macos-app.sh`等でバンドル化した`.app`を`open`コマンドで起動する形を明記する。`cargo run`/生バイナリの直接execでの検証は行わない。

### D9. editor-integrationの既定モード(関連付けアプリで開く) → macOSは`open`コマンド
`editor_bridge.rs`の`shell_execute_open`(Windowsは`ShellExecuteW`)が、フェーズ1〜6の実装完了後・feature parity検証中に**真の空スタブ**(`#[cfg(not(windows))] fn shell_execute_open(_file_path: &Path) {}`)のまま残っていたことが判明(2026-07-30)。editor-integrationの既定モード(「関連付けアプリ」、多くのユーザーがそのまま使う設定)がmacOSで完全に無反応になる実害あるバグだった。macOSの`open <path>`コマンド(LaunchServices経由でファイルを既定アプリで開く、`ShellExecuteW`の直接の等価物)を呼ぶよう修正。「Notepad」モード(Windows専用、`notepad.exe`ハードコード)はmacOSでは意味を持たないためそのまま未対応(ユーザーがこのモードを選ぶ想定がない)。

## Risks / Trade-offs

- [Risk] Unix domain socketのパーミッション設定だけでは、Named Pipe版のSDDL ACLほど厳密な保証にならない可能性(マルチユーザー環境での競合等) → Mitigation: ソケットファイルを毎回一意な一時ディレクトリ(`0700`)配下に作成し、ディレクトリ自体のパーミッションで実質的に隔離する。実機での権限昇格試験はtasks.mdの検証項目に含める。
- [Risk] プロセスグループでの子孫プロセス一括終了が、デタッチされた孫プロセス(二重fork等)を取りこぼす可能性(Job Objectほどの強制力がない) → Mitigation: 実機での「あえて子プロセスがデタッチするMCPサーバ」を模したテストケースで検証し、取りこぼしが確認された場合はamm-mcp側のプロセス起動規約(常にプロセスグループを継承させる)をドキュメント化する。
- [Risk] Gatekeeper未署名警告によりMac版の配布・初回起動体験がWindows版より悪化する → Mitigation: 今回はドキュメントでの案内に留める。将来的なnotarization対応はOpen Questionsとして明示し、必要になった時点でユーザーとApple Developer Program加入を含めて再検討する。
- [Risk] `osascript`ベースの前面化・通知は、ユーザーの「システム設定→通知→スクリプトエディタ」権限設定に依存し、環境によっては無音/無反応になりうる(過去のAvalonia版PoCで実機確認済みの制約) → Mitigation: amm側のバグではないことをドキュメント化し、問い合わせ時の一次切り分け手順として`reference/mac-avalonia-poc-lessons/README.md`を参照する。
- [Risk] 単一作業者・単一Mac実機での検証のため、複数バージョンのmacOS/Apple Silicon以外(Intel Mac)での動作差異を見落とす可能性 → Mitigation: 検証環境(OSバージョン・アーキテクチャ)をtasks.md/HANDOVER.mdに明記し、既知の未検証範囲として引き継ぐ。

## Migration Plan

段階的に実装し、各段階でMac実機ビルド・動作確認を行う(`migrate-to-tauri`のフェーズ制と同じ進め方):
1. cfg分岐の足場作り(Cargo.tomlのtarget別dependency定義、モジュールファイルの新設)
2. IPCトランスポート(Unix domain socket)の実装・amm-mcp CLI側の追従
3. プロセスツリー管理(プロセスグループ+シグナル)の実装
4. システムメニュー代替UI(ウィンドウ内)の実装
5. Dockバウンス通知・osascript前面化の実装
6. Mac版ビルド・`.app`/`.dmg`インストーラの整備
7. 実機でのfeature parity検証(Windows版PARITY-AUDIT.mdに相当する棚卸し)
8. ドキュメント更新(README/CLAUDE.md/docs/build.md、cross-platform-feasibility.mdのアーカイブ化)

ロールバック戦略: 全変更が`cfg(target_os = "macos")`/`cfg(unix)`配下に閉じるため、Windows版のビルド・動作に影響しない。問題が発生した場合はMac側の追加コードを無効化するだけで既存Windows版は無傷。

## Open Questions（解決済み）

- **notarization(公証)を行うか** → **行わない**。Apple Developer Program($99/年)加入が前提となるため現状は見送り。ad-hoc署名のみで配布し、Gatekeeper警告はユーザー側の初回起動手順(右クリック→開く、または`xattr -d com.apple.quarantine`)でカバーする(6.3で文書化済み)。
- **配布形態は`.dmg`のみで十分か、`.pkg`も用意するか** → **`.dmg`のみ**。ドラッグ&ドロップでApplicationsフォルダへコピーする一般的なmacOSアプリの配布形式とし、追加のインストーラスクリプトは用意しない。
- **Mac版の対応OSバージョン下限** → **Tauriの既定値のまま**(`tauri.conf.json`の`bundle.macOS.minimumSystemVersion`を明示指定せず、既定の`LSMinimumSystemVersion`相当(10.13〜)に任せる)。実機検証はこのセッションで使用したmacOS 26系のみ実施済み、それより古いバージョンでの動作は未検証のまま。
- **Amm.PowerShell(`ps-module`)のMac対応** → **対象外、Windows版のみの機能と割り切る**。一度Unix domain socket接続(`Open-AmmUnixSocketConnection`)を実装したが、本decision を受けてrevertした(コミット`116985f`)。`ps-module`のspec deltaもこのchangeから削除。将来pwsh on macOSでの需要が具体化したら別changeとして再検討する。

- **`amm-mcp`の配置・PATH到達性** → **解決済み(2026-07-30)**。`commands_misc.rs`の`resolve_mcp_exe_path()`(hook_cli/mcp_cli登録が設定ファイルへ書き込む文字列そのもの)を、`resolve_profiles_path`と同じ`Contents/Resources/amm-mcp`優先ロジックへ修正。設定ファイルには**絶対パス**が書き込まれるため、そもそもPATH到達性は不要(PATHに無くても絶対パス実行できる)と判明——「PATHに無いから到達できない」という前提自体が誤りだった。シンボリックリンク等の追加インストール手順は不要。

### D10. per-userデータファイルの保存先解決に潜んでいたLOCALAPPDATA前提の一括修正
`editor_bridge.rs`(editor-settings.json)・`input_history.rs`(history.json)・`profile.rs`(trusted-profiles.json)・`gateway.rs`(mcp-servers.json)の4箇所が、独立に`std::env::var("LOCALAPPDATA")`をノーガードでコピペしていた(`#[cfg(windows)]`すら無い)ことが2026-07-30の実機feature parity検証で判明。`LOCALAPPDATA`はmacOS/Linuxに存在せず、`.unwrap_or_default()`で無言のまま空`PathBuf`にフォールバックしていたため、これら4種の永続化ファイル全てがCWD相対の意味不明な場所に読み書きされる(=実質的に永続化されない)実害バグだった。`lib.rs`に共通ヘルパー`app_data_base_dir()`を新設(Windows: `LOCALAPPDATA`のまま変更なし、macOS: `~/Library/Application Support`、Linux: XDG Base Directory)し4箇所を置き換えて解消。同じセッションで発見した関連バグ2件も合わせて修正: (1) `native_ui.rs`の`tray_settings_path()`がexe隣接(`.app`バンドルでは`Contents/MacOS/`、書き込み不適切)だったため、Windows版の挙動は変更せずmacOS/Unixのみ`app_data_base_dir()`へ切り替え。(2) `commands_misc.rs`の`resolve_home_dir()`が`USERPROFILE`(Windows専用)のみで、macOSでは`~/.claude/`等の設定ファイルパスがルート相対の壊れたパスになっていた(hook-cli/mcp-cli登録機能に直結する重大バグ)ため、`HOME`環境変数を使うUnix分岐を追加。(3) `pty.rs`の`spawn_pty_for_pane_with_patterns`が、プロファイル/コマンド未指定(`command: None`、素の「+ Pane」相当)時のデフォルトシェルを`COMSPEC`環境変数→文字列リテラル`"powershell.exe"`にフォールバックしており、macOSではどちらも存在しないため**プロファイル無しの新規ペイン作成自体が失敗する**実害バグだった。Windows版は変更せず、macOS/Unixは`$SHELL`環境変数(無ければ`/bin/zsh`)にフォールバックするよう修正。

これらは全て`#[cfg(windows)]`を伴わない「一見クロスプラットフォームに見えるが実際はWindows専用の環境変数を素朴に呼んでいるだけ」のコードで、当初のファイル単位`grep -rln "cfg(windows)"`監査では検出できなかった。教訓は`tasks/retro-pending.md`参照。

### D12. 既定プロファイル(profiles.amm)・ブートストラップフォールバック(default_cmd)のcmd.exe前提を解消し、Windows/macOSで別ファイル化
user(「macのコマンドにCmdは無いね。zsh?」)の指摘をきっかけに監査したところ、2箇所で無条件のcmd.exe前提が見つかった。(1) `pty.rs`の`spawn_pty_for_pane_with_patterns`で、D10で`cfg`分岐済みの`default_shell`クロージャのすぐ下にある`auto_chcp`判定(`build_chcp_wrapped_command`呼び出し)には`cfg`が一切無く、`autoChcp: true`なプロファイル(Cmd/PowerShell由来。インポート・複製で生き残りうる)はmacOSでも実シェルを`cmd.exe /d /s /c "chcp 65001 > nul && <shell>"`へ無条件でラップし、cmd.exeが存在しないため必ず失敗していた。`let auto_chcp = auto_chcp && cfg!(windows);`の1行で解消。(2) `profile.rs`の`default_cmd()`(profiles.amm欠損/破損時の唯一のフォールバックプロファイル)も同様に`executable: "cmd.exe"`を無条件返却しており、`cfg(windows)`/`cfg(target_os = "macos")`(`$SHELL`→`/bin/zsh`)/`cfg(all(unix, not(target_os = "macos")))`(`$SHELL`→`/bin/bash`)の3分岐へ修正(あわせて`auto_chcp`のWindows分岐を.NET版`CreateDefaultCmd`の`AutoChcp = true`と一致するよう修正、従来のRust版はここが矛盾して`false`だった)。

さらに配布用の既定`profiles.amm`(`src/apps/Amm/profiles.amm`)自体が、CMD/PowerShell/`claude.exe`/`cmd.exe /c %APPDATA%\npm\*.cmd`というWindows専用内容を無条件で両OSの`.app`/インストーラへ同梱していたことも判明。`src/apps/Amm/profiles.macos.amm`(zsh・`claude`/`copilot`/`codex`/`gemini`をbare実行ファイル名でPATH解決、Windows npm `.cmd`ラッパー方式は使わない)を新設し、Tauriのプラットフォーム別設定オーバーライド`tauri.macos.conf.json`(base設定の`"../profiles.amm": "./"`エントリを`null`で無効化し、`"../profiles.macos.amm": "./profiles.amm"`を追加)でmacOSビルドのみ切替。
**棄却した代替**: (a) 各プラットフォームのpublishスクリプトが`src-tauri/resources/`へ該当ファイルをstageする方式(amm-mcp.exeと同じ scheme) — 実装したところ、`tauri-build`のbuild.rsが素の`cargo build`実行時にも`bundle.resources`のglobパターン(`resources/*`)を検証しており、他の直接参照エントリ(`../profiles.amm`等)を除去した状態でリソースstage前に`cargo build`を呼ぶと`glob pattern resources/* path not found`でビルド自体が失敗することを実機で確認したため不採用。(b) 単一の`profiles.amm`に両OS分の情報を持たせ実行時に取捨選択する案 — `SessionProfile`のスキーマ(`executable`はプレーン文字列)にプラットフォーム条件分岐の概念を持ち込む変更は影響範囲が大きく、パッケージング層で解決する方が最小差分。

### D13. git-integration閉じるガードがmacOSで常に無音スキップされていたバグの修正
git-test-repo(未コミットファイル1件)を作業ディレクトリに実機でペインクローズガードを検証したところ、`GitCommitDialog`相当のモーダル(`askCommitDecision`)が一度も表示されずペインが無条件にクローズされることを発見した。`alert()`デバッグ計装で追跡したところ、`git_helper.rs`の`get_repo_root()`が`git rev-parse --show-toplevel`の出力(常にフォワードスラッシュ)を`trimmed.replace('/', "\\")`で無条件にバックスラッシュ区切りへ変換しており、macOSでは実在しないパス(`\private\tmp\...\repo`)を返していたことが判明。この壊れたパスが`git_status_short`に渡り、対象ディレクトリが存在しないため`run()`のガード節(`!dir.is_dir()`)が即座に空出力`("", "")`を返し、`runGitGuardForRepo`の「statusが空なら変更なし」というフォールバック(`if (!status || !status.trim()) return true;`)がそのまま「変更なし」と誤認して**ガード自体が常に無音でパスされる**という、git-integration機能全体を実質的に無効化する重大バグだった。`cfg!(windows)`で分岐し、Windows以外はgitの出力をそのまま返すよう修正。修正後、実機でペインクローズ時にコミットダイアログが正しく表示され、「コミットして続行」選択で`git commit`が実際に実行され作業ツリーがcleanになることを確認済み。

### D11. hook-cli登録コマンドの`cmd.exe`ラップ → macOSは`test -x && exec`
`hook_cli.rs`の`register_claude`/`register_copilot_like`が、登録するフック実行コマンドを無条件に`cmd /c if exist "..." "..." <args>`(Windowsの`cmd.exe`バッチイディオム、パスが古くなっていた場合の存在チェック込み)として構築していたことが判明。`cmd`コマンド自体がmacOSに存在しないため、**macOSではhook-cli機能(notify/approve)が登録はできても実行時に必ず失敗する**(Claude Code等が「コマンドが見つからない」で無反応になる)状態だった。POSIXシェルの等価な構文`test -x "..." && "..." <args>`(同じ「実在すれば実行」というガード)へ切り替え、Windows版の挙動は完全に維持。

あわせて、登録済みコマンドを検出する側のロジック(`is_amm_notify_command`・`extract_exe_path`の正規表現・codex用notify行の2つの`.contains("amm-mcp.exe")`チェック)も全て`.exe`拡張子を必須としており、macOSでの拡張子なしパス(`amm-mcp`)を一切認識できず、再登録の冪等性(重複防止)が壊れる状態だったため、`.exe`を任意([省略可能]の正規表現/拡張子なし部分一致)に修正。バリデーション関数(`validate_mcp_exe_path_for_cmd_wrapping`)もシェル文脈に合わせて許容外文字集合を`cmd.exe`用(`"&|<>^%`)とPOSIXシェル用(`` "$`\ ``)で分岐。既存のWindows形式パスをテストフィクスチャに使っていた3件のテストが、Unix版バリデータでバックスラッシュを拒否してこのMac実機で失敗したため、フィクスチャをフォワードスラッシュ表記(`C:/amm/amm-mcp.exe`)へ変更(意味は変えず、プラットフォーム非依存の文字列に揃えただけ)。
