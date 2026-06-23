# src/libs/ — DLL を生成するプロジェクト群

共有 DLL（UI/OS 非依存の共有ロジック、DTO/インターフェース、C++ ネイティブ等）の置き場。

- 依存方向は **apps → libs** の一方向のみ。libs から apps を参照しない。
- 1 サブフォルダ = 1 プロジェクト = 1 成果物（dll）。フォルダ名と成果物名を一致させる。

現状、本リポジトリは共有ロジックを `src/apps/Amm/Core/` 内に保持しており、
独立した共有 DLL は未分離。再利用が必要になった時点でここへ切り出す
（例: `App.Core` / `App.Contracts` / `Native.Interop`）。
