# 実行計画: MAUI Blazor アプリ初期実装（ブートストラップ）

## Goal
- `docs/要件定義.md` と `docs/design/maui-blazor-architecture.md` に基づき、MVP開発を開始できる最小構成（ソリューション/プロジェクト/DI骨格/UI骨格）を作成する。

## Design Check
- 判定: **必要**
- 理由: 新規機能実装（初期コード作成）に該当するため、`docs/design/maui-blazor-architecture.md` と実装差分を確認し、差分が出た場合は実装前または同一作業内で更新する。

## Task List
- [x] 1. MAUI Blazor Hybrid のソリューション/プロジェクトを作成する（Windowsターゲットを含む）。
- [x] 2. 設計書の責務に沿ったフォルダ骨格を作成する（Domain/Application/Infrastructure/Platforms/Windows/UI）。
- [x] 3. DI登録の初期実装を追加する（主要サービスのインターフェースと仮実装を配置）。
- [x] 4. ランチャーUIの最小画面（ゲームカード一覧プレースホルダー）を作成する。
- [x] 5. ビルドを実行し、初期状態でコンパイル可能なことを確認する。
- [x] 6. 実装差分がある場合、`docs/design/maui-blazor-architecture.md` を更新する。
- [x] 7. 計画を完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `GameLauncherWithGit.sln`（新規予定）
- `src/GameLauncherWithGit/` 配下（新規予定）
- `docs/design/maui-blazor-architecture.md`（必要時更新）
- `docs/plans/20260206-bootstrap-maui-blazor.md`

## Risks
- .NET SDK/Workload のバージョン差異でテンプレート生成物が設計想定とずれる可能性
- Windows 固有機能（Tray/Toast/自動起動）は初期段階ではダミー実装になり、後続タスクで置換が必要
- 初期骨格の責務分割が粗いと、後続で大きな再配置が発生する可能性

## Notes / Logs
- 2026-02-06: リポジトリには現時点で実装コードが存在せず、ドキュメントのみを確認。
- 2026-02-06: `dotnet new maui-blazor` の `-n/-o` 指定でテンプレート解決エラーが発生したため、ルート生成後に `src/GameLauncherWithGit` へ移動して構成を整備。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` を実行し、0エラーでビルド成功を確認。
- 2026-02-06: `App.xaml.cs` で `Application` 名前解決の衝突が発生したため、`Microsoft.Maui.Controls.Application` の完全修飾で解消。
