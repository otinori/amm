# src/publish/ — 発行プロファイル（出力先ではない）

各 app の `dotnet publish` 条件を `*.pubxml` として **リポジトリ管理**し、CI とローカルで発行条件を一致させる。

- 発行**物**は `artifacts/publish/<app>/<RID>/` に staging する（ここではない）。
- app ごとにサブフォルダを切る（例: `src/publish/Amm/win-x64-singlefile.pubxml`）。

> 注意: `.gitignore` の `**/publish/` は dotnet publish 出力を無視する規則のため、
> このフォルダだけは `!src/publish/` で追跡例外にしている。
