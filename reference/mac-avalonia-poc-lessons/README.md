# Mac Avalonia PoC — 実機教訓アーカイブ

出典: ローカルのみに存在し origin では削除済み（削除予定）だったブランチ
`claude/mac-version-production-5uzb42`（2026-07-04 分岐、2026-07-10 最終コミット、
main/現行 Tauri 系譜のどちらにも未マージ）。

## これは何か

Tauri 採用決定（`UDR-amm-20260713T1037-ff3`）より前に進めていた、Avalonia UI +
Iciclecreek.Avalonia.Terminal + Porta.Pty による Mac/Linux 移植の試作
（`src/apps/Amm.Desktop/`、約100コミット）。**このアーキテクチャ自体は Tauri 採用により
完全に代替され、コードは現行リポジトリのどこにも存在しない。復元・移植の対象ではない。**

保存する価値があるのは、このブランチで実機（macOS 26.5.1 ARM64）検証を通じて発見された
**OSレベルの挙動・落とし穴**の方で、Avalonia/.NET 固有ではなく Tauri/Rust 実装でも
再現しうるもの。ブランチ削除前に、該当ソース3ファイルと教訓の全文をここに抜き出した。

## ファイルと教訓の対応

### `TrayIconManager.cs` — macOSのフォーカススティール防止と前面化
`BringToForeground` / `ActivateViaAppleScript` 参照（195〜237行付近）。

`Window.Activate()` や `WindowState` の Minimized→復帰トグルだけでは、**他のアプリが
フォアグラウンドの状態から自アプリを前面化できないことが実機で確認された**
（Finder が前面なら成功するが、Claude Desktop が前面だと失敗した、という非対称性まで
確認済み）。近年の macOS は NSApplication 経由の自己アクティベーションを段階的に
制限しているためで、`osascript -e 'tell application id "..." to activate'`
（Apple Events / Launch Services 経由、NSApplication の自己アクティベーションとは
別の権限を持つ正規の経路）に切り替えることで解消した。

**現行コードとの関連**: `src/apps/Amm/src-tauri/src/native_ui.rs`(216-218行)と
`lib.rs`(53-55行)は `window.show()`/`unminimize()`/`set_focus()` という、
ここで「不十分」と確認された手法をそのまま使っている。2026-07-29のトースト通知
前面化機能（コミット `d2a1b70`）はまさにこの経路を通るため、他アプリが前面の状態から
クリックした場合に同じ問題が実機で再現する可能性がある。

### `EditorBridge.cs` — SIP保護バイナリの直接execとLaunch Constraints
`LaunchEditor`(114-126行)参照。

`settings.CustomEditorPath` をユーザーが Apple 純正アプリ（TextEdit 等）の
`Contents/MacOS/` 配下の実行ファイルに直接向けた場合、`UseShellExecute=false` での
直接 exec は macOS の Launch Constraints により **起動された側のプロセスが
Code Signature Invalid / Launch Constraint Violation でクラッシュする**
（amm 自体は無事）。amm 側のバグではないため直すものではないが、この挙動を知らずに
「カスタムエディタパス機能が壊れている」と誤診断しないための記録。実機で同機能を
テストする際はサードパーティ製アプリ（VS Code 等）かプレーンなシェルスクリプトを使うこと。

**現行コードとの関連**: `src/apps/Amm/src-tauri/src/editor_bridge.rs`(163行)が
`Command::new(settings.custom_editor_path.trim()).arg(file_path).spawn()` で
同じ形の直接 exec をしている。ユーザーが SIP 保護システムアプリのバイナリパスを
指定した場合、Rust 実装でも同じクラッシュ挙動になりうる（amm 側のバグとしては
扱わない、が問い合わせが来た際の一次切り分けに使える）。

### `GitHelper.cs` — Process標準入出力リダイレクトのAccessViolationException
`Run`(102-151行)冒頭のコメント(63-101行)参照。

このMac実機・.NET 9の組み合わせでは、`Process` の stdout/stderr を
`RedirectStandardOutput/Error` でリダイレクトすると（同期読み取り・
`OutputDataReceived`非同期読み取りのどちらでも）`System.AccessViolationException`
（キャッチ不能）でアプリ全体がクラッシュすることがあった。原因は .NET の Unix
`PipeStream` 実装が内部で `Socket` にラップする経路にあると推測。最終的な回避策は
「.NET の Process パイプでリダイレクトせず、`/bin/sh -c "... > tmpOut 2> tmpErr"`
で一時ファイルにリダイレクトさせ、終了後に `File.ReadAllText` で読む」方式。

**現行コードとの関連**: 言語がRustに変わった(`git_helper.rs`)ため直接の再発リスクは
低い（.NETのPipeStream/Socket実装固有の問題であり、Rustの`std::process::Command`は
同じ経路を通らない）。ただし「標準入出力リダイレクトが絡む不可解なクラッシュ」の
実例として、切り分けの参考に残す。

## その他の実機教訓（コード抽出はしていないが記録として)

出典ブランチの `AGENTS.md` 補助ルール節より、上記3件以外で今後刺さりうるもの:

- **`pkill -f <pattern>` の自己マッチ罠**: パターン文字列がコマンドライン全体
  (自分自身のargv)にもマッチし、無出力のまま異常終了することがある。
  `pkill -x` かフルパス指定を優先する。
- **`Process.Start`をアプリ起動直後（メインウィンドウ生成コンストラクタ等）から
  直接呼ばない**: ネイティブメッセージループ確立前の fork/spawn が不安定になり、
  `GitHelper`のAccessViolationExceptionと同系統のfail-fastクラッシュを実機で確認。
  既存の成功パターンは全て「ウィンドウ表示後 / ユーザー操作後」に`Process.Start`
  している。Tauri の `.setup()` 内で早期に外部プロセスを spawn する新規コードを
  書く場合、同種の問題が起きないか実機確認する価値がある。
- **`osascript -e 'display notification'` はユーザーの通知設定次第で無音になる**:
  「システム設定 → 通知 → スクリプトエディタ」の通知スタイルが「なし」だと
  一切表示されない。amm側のバグではなくユーザー環境設定起因。
- **実機でのUI検証は `.app` バンドル化 + `open` 起動を徹底し、生バイナリの
  直接execは避ける**: `dotnet run`/生バイナリをバックグラウンド起動すると、
  ウィンドウは生成されアクセシビリティツリー上は正常でも、ツールバー/
  ネイティブメニュー/ペインヘッダーが視覚的に描画されないことがある
  （LaunchServices経由の正規起動を経ていないためと推測、未確定）。
  Tauri でも実機UI検証は配布バイナリ（`.app`）を `open` で起動して行うべき、
  という一般則として有効。

## この教訓を今後どう使うか

このディレクトリはアーカイブ（読み物）であり、AGENTS.md の運用ルールには
まだ反映していない。特に「フォーカス前面化」と「カスタムエディタのexec」の2点は
現行Tauriコードに直接刺さる可能性があるため、該当機能に触るタイミングで
このREADMEを参照するか、AGENTS.md補助ルールへの反映を別途検討すること。
