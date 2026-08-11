## MODIFIED Requirements

### Requirement: Level 1 — 許可要求の通知のみ (Attention 表示)
CLI が確認要求 (Notification 系イベント) を発火したとき、amm は該当ペインに `HasAttention` フラグを立て、タイトルバー/ボタンのオレンジ表示とタイトル絵文字 (⚠) で人間に知らせなければならない (SHALL)。回答自体はペイン内で行い、amm は代答しない。

#### Scenario: 非フォアグラウンド時の通知(Windows)
- **WHEN** Windows上でammがフォアグラウンドでない状態でペインが attention 状態になる
- **THEN** タスクバーが `FlashWindowEx` で点滅し、ペインをアクティブ化するか idle 通知が来るまで attention 表示が解除されない

#### Scenario: 非フォアグラウンド時の通知(macOS)
- **WHEN** macOS上でammがフォアグラウンドでない状態でペインが attention 状態になる
- **THEN** Dockアイコンが`request_user_attention(Informational)`相当でバウンスし、ペインをアクティブ化するか idle 通知が来るまで attention 表示が解除されない
