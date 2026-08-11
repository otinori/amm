## 1. 内部整合性レビュー

- [x] 1.1 `approval-hub` の Requirement「Level 1 — 許可要求の通知のみ (Attention 表示)」を `TerminalChildForm.cs`（`SetAttention`/`ApplyTitleBarTint`/`WaitStateGlyph.For`）と `MdiParentForm.cs`（クイック切替バーのオレンジ背景）で裏取り済み。記述内容はコードと一致しており修正不要
- [x] 1.2 `chat-recording`（`ChatRecorder.cs`/`ChatStats.cs`）と `git-integration`（`GitHelper.cs` のタイムアウト値 3s/5s/5s/3s/10s/30s 等）を再読解し、spec の記述がコードと一致していることを確認済み（テストコードが無いため静的読解のみでの検証である点は留意事項として残る）
- [x] 1.3 `input-history` の不整合（↑/↓キー履歴ナビゲーションは「誤操作の温床」として廃止済みだが `InputHistoryTests.cs` は廃止済みの `NavigateUp`/`NavigateDown` API を今も検証）は、spec 内の該当 Requirement 本文にその事実（テストが検証する契約と実際の UI 呼び出し経路が乖離している旨）を明記する形で既に反映済み
- [x] 1.4 16 capability 間の用語横断チェックを実施。`profile-schema/spec.md` に `profiles.json` を代替ファイル名であるかのように読める記述が残っていた（実際は `profiles.amm` のみ探索、同ファイル内の別 Requirement とも矛盾）ため修正済み。他の主要語彙（`WaitingForInput`/`HasAttention`/`amm-mcp.exe`）と Requirement タイトルの重複は無し
- [x] 1.5 `mcp-gateway`/`mdi-window-control`/`auto-send-idle`/`command-import-export`/`quick-command-register` の元 `req-*.md` が "Draft" 表記のまま実装済みだった件は、ユーザー確認の結果 **今回は修正せず 3.3 の別タスクに委ねる**方針に決定

## 2. 検証

- [x] 2.1 `openspec validate establish-baseline-specs --type change --strict` を再実行し pass を最終確認する（2026-07-11 実施、pass 確認済み）
- [x] 2.2 16 ファイル全てで `### Requirement:` に対応する `#### Scenario:`（正確に4個のハッシュ）が最低1つ以上あることを再確認する（スクリプトチェック済み、全ファイル OK）

## 3. ベースライン確立

- [x] 3.1 ユーザーレビュー・承認を得る（2026-07-11 ユーザー承認済み）
- [ ] 3.2 `openspec archive establish-baseline-specs` を実行し `openspec/specs/<capability>/spec.md` として確立する
- [ ] 3.3 `docs/design/spec/`（`spec.md`/`spec-v2.md`/`req-*.md`）の今後の扱い（アーカイブ・README からのリンク更新・削除）を別 change として起票する
