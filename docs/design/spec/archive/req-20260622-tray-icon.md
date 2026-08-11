# req-20260622: システムトレイ常駐アイコン＋入力待ちトースト通知

- **ID**: req-20260622-tray-icon
- **状態**: Draft
- **関連 UDR**: UDR-amm-20260605T1043-3af（Approval Hub Level 1: attention 可視化）
- **更新日**: 2026-06-22

## 背景 / 目的

現状、amm が最前面にないとき（他のウィンドウの裏にある等）は、
いずれかの MDI ペインが入力待ちになっても気づけない。

`FlashWindowEx` によるタイトルバー点滅（UDR-3af）はウィンドウが見えている場合には有効だが、
**amm 自体が画面外・隠れている場合** には効かない。

本 spec ではシステムトレイへの常駐アイコンを追加し、
入力待ちセッションが発生したらトースト通知でユーザーに知らせ、
アイコンクリックで該当セッションにジャンプできるようにする。

## 要求（What）

### R-1: トレイアイコン常駐
- amm 起動中はシステムトレイ（通知領域）にアイコンを常駐させる。
- アイコンは amm のアプリアイコン（既存 `amm.ico`）を使用。
- ツールチップ: `"amm — 起動中"` または `"amm — 入力待ち N 件"`（N > 0 のとき）。
- amm 終了時にアイコンを除去する（`NotifyIcon.Dispose()`）。

### R-2: 入力待ちトースト通知
- いずれかの MDI ペインが `WaitingForInput` 状態に遷移したとき、
  トースト（バルーン）通知を表示する。
- 通知内容:
  - タイトル: `"amm: 入力待ち"`
  - 本文: `"{ペインの Nickname} が入力待ちです"`
- 実装: `NotifyIcon.ShowBalloonTip(timeout, title, text, ToolTipIcon.Info)` を使用。
  （WinRT 依存なし・WinForms 標準機能）
- 既に amm ウィンドウがフォアグラウンドの場合は通知を表示しない。
- 同一セッションが短時間に連続して `WaitingForInput` になった場合は
  直近 1 回のみ通知する（5 秒間隔で dedup）。

### R-3: アイコン・通知クリックでセッションへジャンプ
- トレイアイコンを **左クリック** または **バルーン通知をクリック** したとき:
  1. amm メインウィンドウを前面に表示する（`BringToForeground`）。
  2. 入力待ちセッションが 1 件の場合 → そのペインをアクティブにする。
  3. 入力待ちセッションが複数の場合 → 最も古く入力待ちになったペインをアクティブにする。
  4. 入力待ちセッションがない場合 → ウィンドウを前面表示するのみ。

### R-4: 右クリックコンテキストメニュー
- トレイアイコンを **右クリック** すると以下のメニューを表示する。

  | 項目 | 動作 |
  |---|---|
  | **amm を表示** | メインウィンドウを前面に表示 |
  | **入力待ちセッション** → サブメニュー | 各ペイン名を列挙、クリックでジャンプ |
  | （区切り線） | |
  | **終了** | amm を終了する |

- 入力待ちセッションがない場合、「入力待ちセッション」はグレーアウト。

### R-5: 既存の attention 可視化（UDR-3af）との関係
- タイトルバーオレンジ表示 + `FlashWindowEx` は変更しない（補完関係）。
- トレイ通知は amm がバックグラウンドにある場合の補完として機能する。
- `HasAttention` フラグの管理は既存コードを再利用する。

## 非対象（Out of scope）

- **最小化してトレイに収納**: メインウィンドウの最小化動作は変更しない。
- **Windows 10/11 ネイティブトースト（WinRT）**: 初期実装は `ShowBalloonTip` で十分。
  リッチ通知（アクションボタン等）は将来検討。
- **通知音**: OS の既定バルーン音に委ねる（カスタム音は追加しない）。
- **複数モニター対応の特別処理**: OS 標準の動作に従う。

## 受け入れ基準

- [ ] amm 起動後にシステムトレイにアイコンが表示される
- [ ] amm 終了後にトレイアイコンが消える
- [ ] バックグラウンドで MDI ペインが入力待ちになるとバルーン通知が出る
- [ ] amm がフォアグラウンドのときはバルーン通知が出ない
- [ ] バルーン通知クリックで amm が前面に来て入力待ちペインがアクティブになる
- [ ] トレイアイコン左クリックで同上の動作
- [ ] トレイアイコン右クリックでコンテキストメニューが表示される
- [ ] 「終了」クリックで amm が正常終了しトレイアイコンが消える
- [ ] ツールチップが入力待ち件数を正しく表示する

## 設計メモ（実装時に詳細化）

### 主要コンポーネント

```
TrayIconManager.cs (新規)
  └─ NotifyIcon               WinForms 標準 (System.Windows.Forms.NotifyIcon)
  └─ ContextMenuStrip         右クリックメニュー
  └─ OnWaitStateChanged()     各 TerminalChildForm の WaitState 変化を購読
       WaitingForInput に遷移 + バックグラウンド → ShowBalloonTip()
  └─ OnBalloonClicked()       入力待ちペインへジャンプ
  └─ UpdateTooltip()          入力待ち件数を反映

MdiParentForm.cs
  └─ TrayIconManager を生成・保持
  └─ 子フォームの WaitStateChanged イベントを TrayIconManager に中継
```

### イベント購読フロー

```
TerminalChildForm
  WaitState → WaitingForInput
    ↓ WaitStateChanged イベント
MdiParentForm
    ↓ 中継
TrayIconManager.OnWaitStateChanged()
  amm がバックグラウンド?
    Yes → NotifyIcon.ShowBalloonTip(5000, "amm: 入力待ち", nickname + " が入力待ちです", Info)
    No  → 何もしない (FlashWindowEx は既存コードが担当)
```

### 実装上の注意

- `NotifyIcon` は `MdiParentForm` の `Dispose` で確実に `Dispose` する（タスクバーに幽霊アイコンを残さない）。
- バルーン通知の dedup: `_lastBalloonShownAt` タイムスタンプを保持し、
  同一セッションについて 5 秒以内の再表示を抑制する。
- `BringToForeground` は `SetForegroundWindow` の制限を考慮し、
  `FlashWindowEx` + `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` の組み合わせを使う。

## 備考

- 現時点では **Draft 置き**。実装コストは中程度（新規クラス 1 本 + イベント配線）。
- 関連: `MdiParentForm.cs`、`TerminalChildForm.cs`（WaitState）、
  `Win32SystemMenu.cs`（既存 Win32 操作の参考）
- 関連 UDR: UDR-amm-20260605T1043-3af（attention 可視化、FlashWindowEx）
