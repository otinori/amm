## 1. データモデル・木操作関数

- [x] 1.1 `tilingRoot`/`zoomedPane`/`TILE_DIVIDER_PX` のモジュールレベル状態を追加
- [x] 1.2 木の探索・置換ヘルパー(`findLeafForPane`/`findParent`/`firstLeaf`/`forEachLeaf`/`replaceNode`)を実装
- [x] 1.3 `insertPaneIntoTree`/`removePaneFromTree`/`swapPanesInTree`/`dockNewPane` を実装

## 2. レイアウト計算・ディバイダードラッグ

- [x] 2.1 `createDividerEl`/`wireDividerDrag`(ディバイダードラッグでの比率変更+継続fit/pty_resize)を実装
- [x] 2.2 `positionPane`/`positionDivider`/`layoutNode`/`hideAllDividers`/`layoutTiles` を実装
- [x] 2.3 `resetToBalancedGrid`/`buildBalancedTree`/`removeAllDividerEls`(「均等グリッドへリセット」)を実装

## 3. ドラッグ&ドロップによるドッキング

- [x] 3.1 タイトルバー `mousedown` ハンドラを、自由移動からドッキング操作(ドラッグ中のドロップゾーン判定・mouseup時のswap/insert)へ置き換え
- [x] 3.2 `findPaneUnderPoint`/`dockZoneAt`/`showDockOverlay`/`hideDockOverlay` を実装
- [x] 3.3 `.dock-overlay` のCSSを追加

## 4. ズーム(全画面表示トグル)

- [x] 4.1 `toggleZoom` を実装し、タイトルバーへ「⛶」アイコンを追加(旧 `.resize-handle` の位置を置き換え)
- [x] 4.2 `.pane-zoom` のCSSを追加、`.resize-handle` のCSSを削除

## 5. 永続化・起動シーケンス

- [x] 5.1 `serializeTree`/`deserializeTree`/`collectTreeDisplayIds` を実装し、`saveLayout`/`loadLayout` をツリー形式へ作り替え
- [x] 5.2 起動時IIFEを「保存済みツリーの葉が要求するペインを先にすべて作成 → ツリー再構築 → 不一致時は均等分割ツリーへフォールバック」の2段階シーケンスへ変更
- [x] 5.3 `closePane`(`removePaneFromTree`+`layoutTiles`呼び出し)、`amm-pane-opened`/`btn-add`(`dockNewPane`呼び出し)、`tray-jump-pane-maximize`(`toggleZoom`呼び出し)、`window resize`(`layoutTiles`呼び出し)を更新
- [x] 5.4 `btn-restore` を、保存済みツリーの葉集合が現在の生存ペイン集合と一致する場合のみ再適用する形に作り替え

## 6. 旧コードの削除・ボタン変更

- [x] 6.1 旧 `tileLayout`/`cascadeLayout`/`maximizePaneToDesk` を削除
- [x] 6.2 `index.html` から「カスケード」ボタンを削除し、「タイル」ボタンを「均等グリッドへリセット」に変更

## 7. 実機検証(Windows実機、CDP経由) — クローズ(2026-07-26、対象機能が存在しないため検証不能)

後続コミット`7520d68`によりBSPタイリングツリー実装自体が全面的に置き換えられ、本セクションが検証対象とする`.tile-divider`/ドッキングゾーン/ツリーシリアライズ復元は現行コードに存在しない。詳細は`proposal.md`冒頭の注記と`UDR-amm-20260726T0950-c31`を参照。以下は当時のチェックリストの記録として残す(実施しない)。

- [ ] 7.1 3ペイン以上開いた状態でタイトルバードラッグ→別ペインの上下左右/中央へドロップし、それぞれ狙い通りにドッキング/入れ替えされることを確認
- [ ] 7.2 `.tile-divider` をドラッグしてリサイズし、両側のペインの `term.cols/rows` が追従する(`pty_resize` 呼び出し)ことを確認
- [ ] 7.3 「⛶」アイコンで全画面化→復帰が木構造を保ったまま機能することを確認
- [ ] 7.4 ペインを閉じたとき兄弟が正しく繰り上がることを確認
- [ ] 7.5 アプリ再起動後に木構造(分割方向・比率・ペイン配置)が復元されることを確認
- [ ] 7.6 「均等グリッドへリセット」ボタンの動作確認
- [ ] 7.7 「カスケード」ボタンが無くなっていることを確認
- [ ] 7.8 既存の他機能(承認オーバーレイ・クイック切替バー・システムメニュー・ドラッグ&ドロップでのファイル投入等)に回帰がないことを一通り確認
