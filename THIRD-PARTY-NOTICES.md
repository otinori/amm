# Third-Party Notices

本製品（amm）は以下の第三者ソフトウェアを含みます。各ライセンスの全文は配布物または下記リンクを参照してください。

## xterm.js — MIT License
ターミナルエミュレータ（`src/apps/Amm/public/xterm.js`, `xterm-addon-fit.js`, `xterm-addon-search.js`, `xterm.css`）。
- https://github.com/xtermjs/xterm.js
- Copyright (c) 2017-2024 The xterm.js authors

## Microsoft Edge WebView2 — Microsoft Software License Terms
WebView2 ランタイム（Windows 版のGUIレンダリングに使用。Tauri経由でシステムランタイムを利用し、NuGetパッケージへの直接依存はない）。
- https://developer.microsoft.com/microsoft-edge/webview2/

<!-- REVIEW NEEDED: 2026-08-10のレガシー.NET版削除に伴い、System.Text.Encoding.CodePages / System.Management.Automation
     (いずれも旧.NET版・旧バイナリ版Amm.PowerShellのNuGet依存で、現在は該当コードごと削除済み)の2エントリを本セッションで除去した。
     一方、現行Tauri実装が実際に使用するRust crates(Cargo.toml)・npm packages(package.json)の第三者通知は
     このファイルにまだ反映されていない可能性がある。公開前に `cargo license` 等で依存ライセンス一覧を再監査し、
     本ファイルを実際の依存関係と突き合わせて更新することを推奨する。 -->

## openspec CLI 生成スキル定義ファイル — MIT License
`.claude/skills/openspec-*/SKILL.md`・`.agent/skills/openspec-*/SKILL.md`・`.codex/skills/openspec-*/SKILL.md`・`.gemini/skills/openspec-*/SKILL.md`・`.github/skills/openspec-*/SKILL.md` は openspec CLI が生成したスキル定義ファイルで、生成元ツール自身のライセンス（MIT、各ファイルの frontmatter に明記）を内包しています（リポジトリ本体の Apache-2.0 とは別人格の著作物です）。
