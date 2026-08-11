# AGENTS.md — マルチエージェント共通ポリシー

本リポジトリで作業する全 AI エージェント（**OpenAI Codex CLI** / **Claude Code** / **Cursor** / **Continue** / GitHub Copilot 等）が **会話開始時に最初に読む** ファイル。プロジェクトの現状・開発ループ・GitHub Actions 規約等のマルチエージェント協働ポリシーを集約している。

各エージェント固有の追加指示がある場合は、以下の派生ファイルを参照:
- `CLAUDE.md` — Claude Code 向け（本書のミラー + 個別追記）

---

## 1. このプロジェクトについて

プロジェクトの概要・現状・主要資産はリポジトリルートの `README.md` を参照（本書はマルチエージェント協働ポリシーに特化）。

---

## 2. 補助ルール

- **commit**: user の明示指示がある場合のみ。通常は変更を検知したら `git status` の結果を伝える
- **設計書の整合**: SPEC / PROTOCOL / Repo の参照整合が崩れる編集をしたら、必ず他ファイルへの波及を grep で確認
- **hooks / MCP server**: Phase 1 PoC では未実装。skill はプロンプトレベル運用
- **macOSで自ウィンドウを前面化する際は`Window.Activate()`/`set_focus()`系だけに頼らない**: 他アプリがフォアグラウンドの状態から`window.show()`/`unminimize()`/`set_focus()`(Tauriの`native_ui.rs`/`lib.rs`が現在使用)のようなプログラム的アクティベーションだけで前面化を試みると、近年のmacOSのフォーカススティール防止強化により失敗することがある(過去のAvalonia版PoCで実機確認、Finderが前面なら成功しClaude Desktopが前面だと失敗、という非対称性まで確認済み)。`osascript -e 'tell application id "..." to activate'`(Apple Events/Launch Services経由、NSApplicationの自己アクティベートとは別の正規経路)への切り替えで解消する。詳細と実装例は`reference/mac-avalonia-poc-lessons/README.md`/`TrayIconManager.cs`参照。
- **macOSでユーザー指定の実行ファイルパスを直接execする機能(カスタムエディタ等)はSIP保護バイナリでクラッシュしうる**: `UseShellExecute=false`相当の直接exec(`editor_bridge.rs`の`Command::new(custom_editor_path).spawn()`等)で、Apple純正アプリ(TextEdit等)の`Contents/MacOS/`配下バイナリを直接指定するとmacOSのLaunch Constraintsにより起動された側がクラッシュする(amm側のバグではない、過去のAvalonia版PoCで実機確認)。この機能を実機テストする際はサードパーティ製アプリかプレーンなシェルスクリプトを使うこと。詳細は`reference/mac-avalonia-poc-lessons/README.md`/`EditorBridge.cs`参照。

---

## 3. 開発ループ（設計 → 製造 → テスト → 振り返り）

本リポジトリでの AI 支援開発はフェーズごとにサブブランチを切り、作業ブランチに PR でマージするループで回す。
**AI がブランチ作成・コミット・PR 作成まで担当し、人間が各フェーズ PR をレビュー・承認してマージする。**

```
claude/<task>-design ──PR──→ claude/<task>   ← CI 動かない（main 向けではないため）
claude/<task>-impl   ──PR──→ claude/<task>   ← CI 動かない
claude/<task>-test   ──PR──→ claude/<task>   ← CI 動かない
retro: コミットを作業ブランチに直接積む
                                  │
                            PR → main         ← CI ここだけ（1回）
```

### 3.1 ブランチ規約

| ブランチ | 役割 |
|---|---|
| `claude/<task>` | 作業ブランチ。最終的に `main` へ PR |
| `claude/<task>-design` | 設計サブブランチ |
| `claude/<task>-impl` | 製造サブブランチ |
| `claude/<task>-test` | テストサブブランチ |

**⚠️ サブブランチはスラッシュではなくハイフン区切り。** `claude/<task>` ブランチが存在する間、`claude/<task>/design` はgitのref名前空間衝突（`refs/heads/claude/<task>`というファイルと`refs/heads/claude/<task>/design`というディレクトリを同時に持てない）で作成不可（ローカル・リモートどちらでも失敗する）。`claude/<task>-design`のようにハイフンで繋いだ兄弟ブランチ名を使うこと（`terminal-poc-design`が実際の前例）。

- サブブランチの PR ターゲットは **`main` ではなく作業ブランチ** → CI は動かない
- `retro:` コミットは作業ブランチに直接積む（プロセス改善はレビュー待ち不要）
- 作業ブランチの `main` 向け PR が唯一の CI トリガー

### 3.2 フロー（AI の動き）

```
1. claude/<task> 作業ブランチを作成
2. claude/<task>-design を作成 → design: コミット → PR（→作業ブランチ）作成
   └─ 人間がレビュー・承認 → マージ → 3へ
3. claude/<task>-impl を作成 → impl: コミット → PR（→作業ブランチ）作成
   └─ 人間がレビュー・承認 → マージ → 4へ
4. claude/<task>-test を作成 → test: コミット → /check-pr → PR（→作業ブランチ）作成
   └─ 人間がレビュー・承認 → マージ → 5へ
5. 作業ブランチに retro: コミット（1改善=1コミット）→ main へ最終 PR 作成
   └─ 人間がレビュー → CI 確認 → マージ
```

### 3.3 コミット規約（フェーズプレフィックス）

コミットメッセージの先頭にフェーズプレフィックスを付ける:

| プレフィックス | フェーズ | 含む成果物の原則 |
|---|---|---|
| `design:` | 設計 | spec / 設計書 の新規・更新 |
| `impl:` | 製造 | ソースコード実装。設計書の同期修正も同一コミットに含める |
| `test:` | テスト | テストコード・バグ修正。ソース変更・設計書更新も同一コミットに含める |
| `retro:` | レトロスペクティブ | AGENTS.md / CLAUDE.md / skill の改善。**1改善対策 = 1コミット** |

**フェーズ内の複数コミットは OK。**

#### 前フェーズ成果物の修正ルール

製造・テストフェーズで前フェーズの成果物を修正する場合は **カレントフェーズのコミットに含める**。

- 製造中に仕様の曖昧さを発見 → `impl:` コミットで設計書を同時修正
- テスト中にバグを発見・修正 → `test:` コミットでソース・設計書を同時修正

#### コミット例

```
✅ 正しい例
design: add spec for auto-send feature
impl: implement auto-send timer (update spec.md to reflect final API)
impl: fix build error in auto-send
test: add unit tests for auto-send
test: fix off-by-one bug found in testing (update spec.md precondition)
retro: AGENTS.md §4 add concurrency checklist
retro: check-pr skill add commit prefix validation

❌ 間違った例
feat: implement auto-send     ← フェーズ不明
impl: fix bug + add tests     ← フェーズ混在（test: を別コミットに）
fix: off-by-one               ← フェーズ不明
```

### 3.4 問題・課題の蓄積ルール

作業中（設計/製造/テストいずれのフェーズでも）に以下のような事象が発生したら、
**AI エージェントが**気づいた時点で即座に（現在のツール呼び出し列を中断してでも、次の作業に移る前に）
**`tasks/retro-pending.md`** に追記する。人間が「これも記録して」等とリクエストした場合も、
人間自身がファイルを編集するのではなく **AI が代行して追記する**。
レトロスペクティブで「何があったか」を思い出す手間をなくすため。

**蓄積対象:**
- ビルドエラー・CI 失敗（特に原因が知識不足やパターン見落としの場合）
- 修正を 2回以上やり直したもの
- ユーザーが驚いた・想定外だった副作用
- 「最初からそうすればよかった」と感じたアプローチの変更
- 時間がかかりすぎた操作や調査

**書式:**
```markdown
- [phase: impl] <何が起きたか 1行> → <なぜ起きたか 1語>
```

例:
```markdown
- [phase: impl] codeql.yml に concurrency 未設定で16並列実行 → パターン見落とし
- [phase: impl] CommonProgramMenuFolder が WiX v5 に存在せず2往復 → 知識不足
- [phase: impl] dependabot.yml 追加直後に9PR発生しユーザーが驚いた → 副作用未告知
```

`tasks/retro-pending.md` は `/retro` 実行後にクリアする。gitignore せず追跡する（チーム共有のため）。

### 3.5 各フェーズの完了条件

**設計フェーズ（design サブブランチ PR マージで完了）:**
- 実装に必要な仕様が spec に記録されている
- 既存 spec との矛盾がないことを確認済み

**製造フェーズ（impl サブブランチ PR マージで完了）:**
- ローカルビルドが通る（`cargo build --manifest-path src/apps/Amm/src-tauri/Cargo.toml`）
- 実装の意図が設計書と整合している

**テストフェーズ（test サブブランチ PR マージで完了）:**
- `/check-pr` を実行して全項目 ✅（← **PR 作成前の必須ゲート**）
- 実機テストが必要な変更（UI / hooks 等）はユーザーに確認依頼済み

**レトロスペクティブ（作業ブランチへの retro: コミット完了で完了）:**
- `/retro` を実行して問題を洗い出した
- 改善対策を 1件1コミットで AGENTS.md / skills に反映した
- main への最終 PR を作成した（AI の担当はここまで）

### 3.6 運用上の注意

- PR が煩雑に感じたら 1ブランチ方式（サブブランチなし）に戻すことを検討する
- 設計が不要な小規模タスク（typo 修正・設定変更等）はサブブランチを省略し `impl:` から始めてよい
- **ドキュメントのみの変更**（`src/` に変更がない OpenSpec change・仕様書の棚卸し等）もサブブランチ（design/impl/test）は省略してよいが、`claude/<task>` 作業ブランチと `main` への PR は省略しない（CI トリガー・レビュー記録を残すため）。コミットプレフィックスは `impl:` を流用する
- 専用の CLI ツール（`openspec` 等）が用意されている操作は、素朴なファイル操作（`mv`/`rm` 等）で代替する前に `<tool> --help` でサブコマンドの有無を確認する（例: `openspec archive` を使わず手動 `mv` してしまい、正本 spec への反映を取りこぼしかけた事例あり）
- 外部ツール（ビルドツール・リンカ等）のエラーメッセージが環境要因（ランタイム依存関係の欠落等）を疑いたくなるほど情報不足なときは、環境側の仮説を実装する前に、まず `-v`/`--verbose` 相当のフラグで一次情報（実際の stdout/stderr）を取得できないか確認する。ラッパーツールが下位ツールの出力を既定ログレベルで握りつぶしているだけ、というケースがある（例: `cargo tauri build` 経由の WiX `light.exe` が実際には `error LGHT0204` を返していたのに、既定(info)ログレベルでは `failed to run light.exe` としか見えず、windows-latest の .NET Framework 3.5 欠落という誤った仮説で CI 1往復（約35分）を無駄にした事例、2026-08-05）
- ある設定（手動ステージング処理・ファイル配置等）を「もう不要」と判断して削除する際は、その設定の存在を前提にしている他の設定（グロブパターン・ディレクトリの空判定等）がないか確認してから削除する。片方だけ消すと別の場所で新しいエラーが発生し、原因調査をもう一往復することになる（例: `resources/` への手動ステージング停止で `bundle.resources` の `resources/*` グロブが空になり `glob pattern ... didn't match any files` で失敗した事例、2026-08-05）
- ユーザーの「見た目のバグ」報告を鵜呑みにする前に、どのビルド（debug/release）・何回目の起動かを確認する（debug ビルドはコンソールが出る等、仕様通りの挙動を誤診断しやすい）。ユーザーが同じ症状を2回言い直す（「動かない」→「だから動かない、〇〇も効かない」）ときは、こちらの最初の解釈（操作ミス/仕様通り）を疑い、客観的な生死確認（無関係な軽量コマンドが応答するか等）を最優先で行う
- 「これは仕様通りです/前からこうでした」とユーザーに説明する前に、実装コードだけでなく①プロジェクト自身の spec、②移植元があれば移植元の原実装、の両方を確認してから判断する。ネストした自己診断（Bash ツール経由の再現テスト等）は本体の実行経路と別のシェル層を経由しうるため、それ単体を鵜呑みにしない
- Rust 側が `app.emit(...)` しているイベントは、対応する `listen(...)` が実在するか `grep` で確認する（「バックエンドは動いているのにフロントが何もしない」系の配線漏れは emit 行だけを見て安心すると見逃す）
- 非同期 Rust コードで競合状態を疑うべき観点: ①複数インスタンスを入れ替えるループ（accept ループ等）は「次の待受を先に用意してから今の処理を渡す」順序になっているか、②`tokio::select!` で片方のブランチが先に副作用（状態登録等）を起こす必要がある場合は既定のランダム順に頼らず `biased;` で順序を固定する、③バックエンドの状態管理をフロントエンドの非同期完了通知に依存させない（フロントエンド側の後始末を待たない存在チェックは古い状態を見て競合状態を生みやすい）、④自分が spawn した子プロセスの終了確認は `kill(pid, 0)` ではなく `try_wait()`/`wait()` を使う（`kill(pid, 0)` はゾンビ状態（終了済みだが親が `wait()` していない）でも生存扱いを返す）

---

## 4. GitHub Actions 規約

### 4.1 新規ワークフロー作成時の必須事項

新規 `.github/workflows/*.yml` を作成する際は以下を **必ず** 含めること:

**concurrency ブロック（必須）**:
```yaml
concurrency:
  group: <workflow-prefix>-${{ github.ref }}
  cancel-in-progress: true
```

- `<workflow-prefix>` は他ワークフローと重複しないプレフィックスを使う
  - 例: `ci-`, `codeql-`, `release-`, `prerelease-`
- 既存ファイル（`ci.yml`, `codeql.yml` 等）のパターンを必ず参照してから作成する

**確認コマンド**（作成後に実行）:
```bash
grep -L "concurrency:" .github/workflows/*.yml
# → 出力があれば concurrency が欠落しているファイルがある
```

### 4.2 副作用のある設定変更の事前告知ルール

以下の設定ファイルを追加・変更するときは、**コミット前に** ユーザーへ副作用を伝えて確認を取ること:

| ファイル | 追加直後の副作用 | 告知必須 |
|---|---|---|
| `.github/dependabot.yml` | 全エコシステムの更新 PR が即時大量発生（10件前後） | ✅ |
| `.github/workflows/codeql.yml` | 全 PR / push でスキャン実行（windows-latest: 20〜30分） | ✅ |
| `.github/workflows/*.yml`（新規） | push 直後から全ブランチへ即時適用 | ✅ |

告知文の例:
> 「`dependabot.yml` を追加すると、直後に NuGet / Actions の更新 PR が一度に複数発生します。続けますか？」

### 4.3 YAML 作成時の既知の落とし穴

- **クォート無しの値に含まれる ` #`（スペース+ハッシュ）はコメント開始とみなされる**: `with:` ブロック等のプレーンスカラー値に `#` を書くと（例: PR 番号を `(PR #${{ ... }})` のように埋め込む）、` #` 以降が丸ごと切り捨てられる。GitHub Actions 自体はエラーを出さず黙って値が短縮されるだけなので気づきにくい。`#` を含む値は必ずダブルクォートで囲む（`name: "amm ... (PR #${{ ... }})"`）。ブロックスカラー（`|`）の中の `#` はコメント扱いされないため対象外（2026-08-05、`pr-prerelease.yml` のリリースタイトルが実際に切り詰められて発覚した実例あり）

---

## 5. クロスプラットフォーム移植チェックリスト

Windows専用実装を新しいプラットフォーム（macOS、将来的にはLinux）へ移植する際、`#[cfg(windows)]`の局所的な付け忘れはコンパイルを通してしまい実行時にしか顕在化しない。add-macos-support実施時（2026-07-29〜30）に見つかったバグの大半は、対象プラットフォームで実際に動かして初めて発覚したものだった。新しいプラットフォームへ着手する際は、以下を横断的に確認する。

### 5.1 横断 grep で探すべきパターン

| 検索対象 | 理由 |
|---|---|
| `LOCALAPPDATA`/`USERPROFILE`/`COMSPEC`/`PROGRAMDATA`/`SystemRoot`/`PATHEXT` | Windows 専用環境変数への素朴な依存。`#[cfg(windows)]` が付いていない箇所は無言でおかしなデフォルト値へフォールバックする |
| `cmd.exe`/`notepad.exe` 等の Windows 専用外部コマンド名 | 呼び出し文字列の構築ロジックそのものに埋め込まれているとキーワード grep 一発では見つかりにくい。「この機能はどの OS コマンドを前提に組み立てているか」という観点でのファイル精読が別途必要 |
| `\\`（バックスラッシュのパス区切り） | 文字列リテラルに紛れ込みやすい。`std::path::Path`/`PathBuf` を経由せず素朴に文字列結合していないか |
| `current_exe()` の隣接パス前提 | Windows のフラットインストール構成では正しいが、macOS の `.app` バンドル（`Contents/MacOS/` と `Contents/Resources/` が別ディレクトリ）では成立しない |

### 5.2 コード以外も対象にする

- UI のラベル文言・alert 文言等「人間が読むだけのハードコード文字列」（`amm.exe` のような拡張子付き表記、バックスラッシュ区切りパス表記等）にも同種の Windows 専用表記が紛れ込みやすい。実機でダイアログ・アラートを一通り目視するフェーズが横断 grep と相互補完的に有効
- 配布用の設定・データファイル（既定プロファイル `profiles.amm` 等）自体が Windows 専用内容を無条件に両 OS へ同梱していないか確認する

### 5.3 同一パターンの複製に注意

同じ設計パターン（Windows 専用処理の代替実装等）が複数箇所に**独立して実装**されているコードベースでは、1 箇所を `cfg` 分岐して満足せず、関数名・実装が酷似する他の箇所がないか横断 grep で確認する（例: プロセスグループ終了処理が `gateway.rs` と `pty.rs` に別々に実装されていた、`LOCALAPPDATA` 読み取りが 4 ファイルにコピペされていた、等の実例あり）。

### 5.4 対象プラットフォームで実際に動かすまで気づけないもの

- `#[cfg]` 漏れは「コンパイルが通る」だけでは検出できない。対象プラットフォームで実際に `cargo test`/実機検証を行って初めて発覚する
- 隣接する 2 つの分岐ロジック（例: デフォルトシェル解決とシェル起動コマンドの後処理ラップ）のうち片方だけ `cfg` 修正して満足しない。機能的に対になっている箇所は必ずセットで見直す

### 5.5 macOS 固有の実機検証・ビルドの注意点

- `osascript`/`System Events` 経由で UI へ非 ASCII 文字（日本語等）を入力する自動テストは、`keystroke` 直接タイプでは現在のキーボードレイアウトに無い文字が化ける。クリップボード経由の貼り付け（`set the clipboard to` → `Cmd+V`）に切り替える
- `cliclick`/`System Events` は Retina の論理ポイント座標系を使う。スクリーンショットのピクセル解像度をそのまま座標に使うとズレる
- 署名済み `.app` バンドルを既存ディレクトリへ `cp -R` する処理は「マージ」であり「置き換え」ではない。コピー前に必ず宛先を削除するか `rsync --delete` 相当を使う
- DMG バンドリングの Finder 装飾 AppleScript ステップは、Automation 権限が無い非対話的なローカルセッションでは失敗しうる（`.app` 自体の生成には影響しない）。ただし**GitHub Actions の hosted `macos-latest` ランナー上ではこの制約は発生しない**（2026-08-05 実機 CI 確認済み。当初「CI では `.dmg` が作れない」と想定していたのは誤りだった）

---

*最終更新: 2026-08-10 / 公開準備に伴いUDR自動検知ポリシー・UDRサマリ・UDR YAMLテンプレート（旧§2〜4）を全面撤去し、以降のセクションを§2〜5へ繰り上げ*
