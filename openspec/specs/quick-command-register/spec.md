# quick-command-register Specification

## Purpose
TBD - created by archiving change establish-baseline-specs. Update Purpose after archive.
## Requirements
### Requirement: 右クリックメニューへの「クイック送信に登録...」項目
`TerminalChildForm` の右クリックコンテキストメニューは、既存の「クイック送信 ▶」サブメニュー（`SessionProfile.QuickPrompts` が1件以上ある場合のみ表示）とその後のセパレータの直後に、常に「クイック送信に登録...」項目を追加しなければならない (SHALL)。この項目は、直前に端末へ転送されたテキスト（webview からの `lastText`、ANSI エスケープシーケンス除去後）が空文字である場合、無効化（グレーアウト）されなければならない (MUST)。

#### Scenario: 直前送信テキストがある場合は有効
- **WHEN** 直前に何らかのテキストを端末へ転送しており、ANSI除去後の内容が非空である
- **THEN** 右クリックメニューの「クイック送信に登録...」は有効なクリック可能項目として表示される

#### Scenario: 直前送信テキストが矢印キー等のみの場合は無効
- **WHEN** 直前の転送内容が矢印キー等のエスケープシーケンスのみで、ANSI除去後の文字列が空になる
- **THEN** 「クイック送信に登録...」はグレーアウトされクリックできない

### Requirement: 登録ダイアログの初期値
「クイック送信に登録...」クリック時、amm は `QuickSendRegisterDialog` を表示し、ラベル欄の初期値を「ANSI除去後の直前送信テキストの先頭行を最大30文字に切り詰めた文字列」、テキスト欄の初期値を「ANSI除去後の直前送信テキスト全文」としなければならない (SHALL)。ラベル欄は最大100文字、テキスト欄は複数行入力・縦スクロール可とする。

#### Scenario: 単一行プロンプトのラベル初期値
- **WHEN** 直前送信テキストが31文字以上の単一行文字列である
- **THEN** ラベル欄の初期値はその文字列の先頭30文字になる

#### Scenario: 複数行プロンプトのラベル初期値
- **WHEN** 直前送信テキストが複数行を含む
- **THEN** ラベル欄の初期値は最初の行（改行より前の部分）のみを基に生成される（30文字超なら切り詰め）

### Requirement: OKによる即時登録・保存
`QuickSendRegisterDialog` で OK が押されテキスト欄が空でない場合、amm は現在フォーカス中の MDI ペインに紐づく `SessionProfile.QuickPrompts` 配列へ `QuickPrompt { Label, Prompt }` を末尾追加し、直後に `SaveProfilesToAmmFile()` を呼び出して `.amm` ファイルへ即時保存しなければならない (SHALL)。この保存は `command-import-export` のインポート/エクスポートとは異なり、ユーザーによる別途の「上書き保存」操作を要さない。

#### Scenario: OK押下で即時にファイルへ反映
- **WHEN** ラベルとテキストを入力してダイアログの OK を押す
- **THEN** フォーカス中ペインの `SessionProfile.QuickPrompts` に新しい `QuickPrompt` が追加され、同じ呼び出しの中で `.amm` ファイルへ保存される

#### Scenario: テキストが空ならOKでも追加しない
- **WHEN** テキスト欄が空のまま OK を押す（ダイアログ結果が `DialogResult.OK` であっても `ResultPrompt` が空文字）
- **THEN** `QuickPrompts` への追加も保存も行われない

### Requirement: Cancel/Esc時の非変更
`QuickSendRegisterDialog` を Cancel または Esc で閉じた場合、amm はプロファイルの `QuickPrompts` を変更してはならず (MUST NOT)、`.amm` ファイルへの保存も行ってはならない (MUST NOT)。

#### Scenario: Cancelで何も変更されない
- **WHEN** ダイアログでキャンセルボタンを押す
- **THEN** `SessionProfile.QuickPrompts` は変更前と同一のままであり、ファイル保存も発生しない

### Requirement: 登録済み項目は設定ダイアログと同一リストを共有する
「クイック送信に登録...」で追加された `QuickPrompt` は、`CommandManagerDialog`（コマンド設定ダイアログ）で編集する `QuickPrompts` と同一のプロパティであり、右クリックメニューの「クイック送信 ▶」サブメニューにもそのまま反映されなければならない (SHALL)。重複するラベルの登録は許可される（設定ダイアログ経由での追加と同じ挙動）。

#### Scenario: 設定ダイアログでも同じ項目が見える
- **WHEN** 右クリック登録で `QuickPrompt` を1件追加した後、コマンド設定ダイアログを開く
- **THEN** 追加した項目がそのプロファイルの一覧に表示される

#### Scenario: 重複ラベルの登録を許可する
- **WHEN** 既存の `QuickPrompts` に同じラベルを持つ項目が既にある状態で、同じラベルを指定して登録する
- **THEN** 重複エラーにはならず新しいエントリとしてそのまま追加される

