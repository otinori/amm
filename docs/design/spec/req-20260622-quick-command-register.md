# req-20260622: 右クリックからのクイック送信コマンド登録

- **ID**: req-20260622-quick-command-register
- **状態**: Draft
- **関連 UDR**: -
- **更新日**: 2026-06-22

## 背景 / 目的

現在、クイック送信ボタン（右クリックメニュー上部のショートカット）は
設定ダイアログ（`CommandTemplateDialog`）からのみ登録できる。

「直前に送ったプロンプトをそのまま登録したい」という場面で、
設定画面を開く → コピーする → 貼り付ける という手順が煩雑。

本 spec では **フォーカス中の MDI ペインの右クリックメニューから直接登録** できる
ショートカットを追加する。

## 要求（What）

### R-1: 右クリックメニューへの登録項目追加
- 端末ペインの右クリックメニューに「**クイック送信に登録...**」を追加する。
- 表示条件: 直前に送信したプロンプト（`lastForward`）が空でないこと。
- 位置: 既存のクイック送信項目セクションの末尾（区切り線の直後）。

### R-2: 登録ダイアログ
- 「クイック送信に登録...」クリック時、以下の入力欄を持つモーダルダイアログを表示する。

  | 欄 | 初期値 | 必須 | 説明 |
  |---|---|---|---|
  | **ラベル** | `lastForward` の先頭 30 文字 | ○ | メニューに表示される名前 |
  | **テキスト** | `lastForward` 全文 | ○ | 実際に送信される内容 |

- OK を押すと現在フォーカス中のプロファイルの `QuickSendItems` に追加し、
  プロファイルを保存する。
- Cancel / Esc で何もしない。

### R-3: 保存先
- 追加先は **現在フォーカス中の MDI ペインに紐付く `SessionProfile`** の `QuickSendItems`。
- `AppSettings.Save()` (= `AtomicFileWriter`) で `.amm` ファイルに即時保存する。
- 設定ダイアログ（`CommandTemplateDialog`）で登録した項目と同じリストに追加されるため、
  設定画面を開くと登録済みの項目として表示される。

### R-4: 既存機能との整合
- 既存の「continue」等のデフォルト登録（`SessionProfile.cs` の組み込み既定値）は変更しない。
- 登録済み項目の編集・削除は従来どおり設定ダイアログから行う。
- 重複ラベルは登録を許可する（設定ダイアログと同じ挙動）。

## 非対象（Out of scope）

- **登録済み項目のその場編集**: 登録後の変更は設定ダイアログで行う。
- **複数ペインへの一括登録**: 登録先は常にフォーカス中のペインのみ。
- **送信履歴からの選択**: `lastForward` の 1 件のみを対象とする（履歴 picker は別 spec）。

## 受け入れ基準

- [ ] 端末ペインを右クリックすると「クイック送信に登録...」が表示される
- [ ] `lastForward` が空の場合は項目がグレーアウト（または非表示）になる
- [ ] ダイアログに `lastForward` の内容が初期値として入る
- [ ] OK 後に右クリックメニューへ新しいクイック送信項目が追加されている
- [ ] `.amm` ファイルを確認すると `quickSendItems` に追加されている
- [ ] 設定ダイアログを開いても同じ項目が表示される
- [ ] Cancel / Esc でプロファイルが変更されない

## 設計メモ（実装時に詳細化）

### 変更箇所

```
terminal.html / terminal.js
  └─ 右クリックメニュー生成部
       lastForward が空でなければ「クイック送信に登録...」を末尾に追加
       クリック → postMessage({ type: 'register_quick_send', text: lastForward })

TerminalChildForm.cs
  └─ WebMessageReceived ハンドラ
       'register_quick_send' 受信 → QuickSendRegisterDialog を ShowDialog

QuickSendRegisterDialog.cs (新規、小規模 WinForms ダイアログ)
  └─ TextBox: label（必須）, TextBox: text（必須）
  └─ OK → profile.QuickSendItems.Add(new QuickSendItem(label, text))
  └─       AppSettings.Save()
  └─ Cancel → 何もしない
```

## 備考

- 現時点では **Draft 置き**。実装コストは小さく、単独で着手可能。
- 関連: `SessionProfile.cs`（QuickSendItems）、`terminal.html`（右クリックメニュー）、
  `CommandTemplateDialog.cs`（既存設定 UI）
