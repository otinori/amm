## Context

`profile-schema`のコマンド編集ダイアログ(`openCommandTemplateDialog`, `app.js`)は`SessionProfile`の全フィールドを表示するが、実装調査(grepで実使用箇所を全数確認)の結果、以下が判明した:

- `useBracketedPaste`/`newlineMode`: Tauri版で完全未使用。`newlineMode`は旧`.NET`版(`TerminalChildForm.cs`/`SessionProfile.cs`)でも参照箇所ゼロで、移植漏れではなく旧版由来の死んだフィールド
- `promptNewNameOnCommandAdd`/`sendLineByLine`/`autoChcp`/`outputEncoding`/`fontSize`: 旧`.NET`版には対応実装があるが(`MdiParentForm.cs:1457-1531`のOpenTerminal、`TerminalChildForm.cs:1462-1520`のSendTextLineByLineAsync/SendAsBracketedPasteAsync、`ConPtyWrapper.cs:39-41`のchcpラップ、`SessionProfile.cs:710-718`のGetEncoding、`TerminalChildForm.cs:1721-1725`のFontSize初期適用)、Tauri版では未移植

このchangeは、削除対象2件をスキーマ・UIから除去し、実装対象5件を旧版の挙動に合わせて追加する。

## Goals / Non-Goals

**Goals:**
- `useBracketedPaste`/`newlineMode`をスキーマ・編集ダイアログ・コマンドタイプ別プリセットから削除する
- `promptNewNameOnCommandAdd`/`sendLineByLine`/`autoChcp`/`outputEncoding`/`fontSize`を旧`.NET`版と同等の挙動でTauri版に実装する
- 実装は既存の設計パターン(モーダル基盤の再利用、Rust側は`profiles.amm`への即時書き込みをせず「反映はメモリ上のみ、永続化は上書き保存に委ねる」規約、`amm-pane-opened`イベントでのプロファイル由来設定のフロント側キャプチャ)を踏襲する

**Non-Goals:**
- bracketed paste送信自体の実装(削除するため対象外)
- `newlineMode`相当の改行コード変換の新規設計(死んだ設定のため対象外)
- 旧`.NET`版(`src/apps/Amm/`)への変更
- MCP/PowerShell経由の起動(`pane/open`のcommand指定パス)への`promptNewNameOnCommandAdd`適用(旧版もMCP起動はこのダイアログ経路を明示的に抑止しており、対象は「コマンド ▶」メニュー起動のみ)

## Decisions

### `useBracketedPaste`/`newlineMode`の削除
`SessionProfile`構造体・`defaultSessionProfile()`・`COMMAND_TYPE_PRESETS`・編集ダイアログのUI要素(チェックボックス/セレクト)を削除する。既存の`profiles.amm`にこれらのキーが残っていても、`SessionProfile`の`#[serde(flatten)] extra`フィールドが未知キーとして吸収するため読み込みエラーにはならない(後方互換)。プリセット適用時(`typeSelect`の`change`ハンドラ)からも該当行を削除する。

### `promptNewNameOnCommandAdd`: JS側でクローン生成、Rust側に`add_profile`コマンドを新設
旧版の「フォルダ選択→名前入力→クローンをprofilesへ追加→そのクローンで起動」という順序をそのままJS側の`launchProfileFromMenu`に組み込む(既存の`selectWorkingDirOnStart`分岐の後に連結)。名前入力は`prompt()`ではなく既存モーダル基盤(`openModal`/`addModalField`/`addModalActions`)によるカスタムダイアログとする(このセッションで一貫して採用している「隠れた規約より明示的なUI」の方針に合わせる)。

クローンの追加はRust側に新設する`add_profile(profile) -> Result<(), String>`コマンド(`commit_profiles`と同様、`ProfilesState.file.profiles`へのメモリ上追加のみ、`profiles.amm`への書き込みは行わない)経由で行う。クローン生成時、以下を上書きする(旧版の`OpenTerminal`と同じ):
- `name`: 入力された新名前
- `nickname`: 新名前を`EscapeNickname`相当の正規化(既存の`normalizeNickname`関数を再利用)で変換
- `workingDirectory`: フォルダを選択していればそれを焼き込む(選択していなければ元プロファイルの値を継承)
- `promptNewNameOnCommandAdd` / `selectWorkingDirOnStart`: 両方`false`(クローンで再度発動しないように)
- `autoStartCount`: `0`
- `windowGeometry`: `[]`(空)

名前が空、または既存プロファイル(大文字小文字無視)と衝突する場合は`openCommandTemplateDialog`の`checkNameConflict`と同じ方式でエラー表示しダイアログを維持する。

### `sendLineByLine`: JS側でpty_writeの複数回呼び出しに分割(Rust側の新規コマンドは作らない)
`amm-pane-opened`受信時、`pane.autoSendOnIdle`等と同様に`pane.sendLineByLine`をプロファイルの値からキャプチャする(コマンド追加時クローンにも引き継がれる)。`sendToPane`は、`pane.sendLineByLine`が真の場合、`filter_text_for_send`適用後のテキストを改行で分割し、`pty_write`を1行ずつ順に呼び出す間に80ms(旧版の既定値)の待機を挟む。既存の「一括で`text + '\r'`を書く」経路との分岐のみで、新規Rustコマンドは不要(`pty_write`は既存のまま複数回呼べば足りる)。

### `autoChcp`: Rust側でCommandBuilderをchcp付きラッパーへ差し替え
`spawn_pty_for_pane_with_patterns`に`auto_chcp: bool`引数を追加する。`true`の場合、`shell`/`args`から素直な単一コマンドライン文字列(スペース含む要素はダブルクォート)を組み立て、実際に`CommandBuilder`へ渡す実行ファイルを`cmd.exe`、引数を`["/d", "/s", "/c", format!("chcp 65001 > nul && {inner}")]`へ差し替える(`ConPtyWrapper.cs:39-41`と同じ技法)。呼び出し元(`mcp.rs::open_pane`)は解決済みプロファイルの`auto_chcp`をそのまま渡す。

### `outputEncoding`: `encoding_rs`クレートを新規依存として追加し、pty読み取りループのデコードをストリーミング対応にする
現状の`String::from_utf8_lossy(&buf[..n])`は固定UTF-8前提。`encoding_rs`(小型・unsafe不使用・エコシステム標準)を追加し、`PtyEntry`にプロファイルの`outputEncoding`から解決した`&'static Encoding`を保持、`encoding_rs::Decoder`(チャンク境界をまたぐマルチバイト文字を正しく扱うステートフルAPI)でループ内デコードする。マッピングは旧版`GetEncoding()`と同じ(`UTF-8`/`UTF8`→UTF-8、`SHIFT_JIS`/`SHIFT-JIS`→Shift-JIS、それ以外→UTF-8既定)。

### `fontSize`: `amm-pane-opened`イベントに`fontSize`を追加し、`createPane`の初期値に反映
`mcp.rs::open_pane`が返す`amm-pane-opened`ペイロードにプロファイルの`fontSize`(未設定なら`null`)を追加。`createPane`は`opts.fontSize`(Rustから来た値、既定`13`)をxterm.jsの`Terminal`コンストラクタへ渡す。セッション中の右クリックでのフォントサイズ変更(既存の`FONT_SIZES`メニュー)は別機能として現状のまま(初期値だけがプロファイル既定に従うようになる)。

## Risks / Trade-offs

- [Risk] `autoChcp`のコマンドライン再構築(素朴なダブルクォート付与)が、複雑な引数(内部にダブルクォートを含む等)で壊れる可能性 → [Mitigation] 対象は既定プリセットの`cmd.exe`/`powershell.exe`起動が主で、引数自体は単純な文字列が大半。壊れるケースが見つかれば追ってエスケープを強化する
- [Risk] `encoding_rs`導入によるバイナリサイズ増(小規模、既知の標準クレート) → [Mitigation] 許容範囲と判断。他に選択肢が乏しい(Rust標準にはコードページ変換が無い)
- [Risk] ストリーミングデコーダの状態(`Decoder`)をペインごとに保持する必要があり、`PtyEntry`の変更範囲がやや広がる → [Mitigation] 既存の`collapse_blank_lines`/`comment_prefixes`と同じ「起動時にプロファイルから1回だけキャプチャ」パターンに揃えるため、設計上の複雑さの増分は小さい
- [Risk] `useBracketedPaste`削除により、Copilot CLI等のInk系TUIへの高速貼り付けが引き続き取りこぼされる可能性(既知の制約、対応しない) → [Mitigation] プロポーザル記載の通り、ユーザー判断で実装しない選択。将来必要になれば別changeで再提案する

## Migration Plan

- 既存`profiles.amm`のマイグレーションは不要(`useBracketedPaste`/`newlineMode`キーは`extra`フィールドに吸収され、無視されるだけ)
- Rust: `cargo build`/`cargo test`で回帰確認。JS: `node --check`で構文確認、実機起動で目視確認
- ロールバック: 本changeは`src/apps/Amm`のみの変更で、問題があれば単純にコミットを戻せば良い

## Open Questions

(なし。方針はユーザーとの対話で確定済み)
