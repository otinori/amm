# Changelog

All notable changes to this project are documented here (Keep a Changelog / SemVer).

形式は [Keep a Changelog](https://keepachangelog.com/)、バージョンは [SemVer](https://semver.org/lang/ja/)。
バージョンの正本は `Directory.Build.props`、Git タグは `v<SemVer>` に一致させる。

## [Unreleased]

## [1.2.0.0] - 2026-06-30

### Added
- 統計情報メニューをサブメニュー化（記録オンオフ + 表示をまとめて配置）
- ターミナル本体（WebView2）の右クリックメニューにもチャット記録・統計情報トグルを追加
- MDI 切り替えボタンにフォーカスがある状態でも Ctrl+S 等の送信ショートカットを有効化
- ログ・統計情報のファイル名先頭にコマンド名（プロファイル名）を付与
- 「新しい名前でコマンドを追加」画面でチャット記録・統計情報のオンオフを選択可能に

### Changed
- 統計情報 (Stats) の既定値を OFF → ON に変更

### Fixed
- ターミナルへ直接タイプして送信した場合にチャット記録・統計情報が発火せず `.amm`
  フォルダが作成されない不具合を修正

## [1.0.0] - 2026-06-23

v1.0.0.0 として新規リリース。
