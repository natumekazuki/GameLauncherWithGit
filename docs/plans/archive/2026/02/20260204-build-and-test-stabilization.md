# 実装計画: ビルド安定化とテスト追加

作成日: 2026-02-04
ステータス: Completed

## Goal
- リポジトリ内の主要資源を整理し、`dotnet build` を成功させる。
- 主要アプリケーションサービスに対する自動テストを追加し、`dotnet test` で通過させる。

## Task List
- [x] 1. 資源棚卸しをドキュメント化する
  - [x] 主要ディレクトリ（`src/`, `docs/`, `.ai_context/`）と既存計画ファイルを確認
  - [x] 現在のビルド失敗要因を再現し、再現手順を計画ファイルに記録

- [x] 2. ビルド失敗要因を解消する
  - [x] SDK/ワークロード起因の不整合を解消（必要なら `global.json` 追加）
  - [x] MAUIプロジェクト設定を是正（必要なPackageReference/ビルド設定の明示化）
  - [x] `dotnet build` 成功を確認

- [x] 3. テストプロジェクトを追加する
  - [x] テストプロジェクトを新規作成し、ソリューションへ追加
  - [x] テスト対象サービス（候補: `GameLibraryService`）の依存をモック化
  - [x] 正常系/異常系のユニットテストを追加

- [x] 4. 検証とドキュメント同期
  - [x] `dotnet test` 成功を確認
  - [x] 変更内容を `docs/design/` / `.ai_context/` への反映要否を判定
  - [x] 本計画ファイルを進捗更新し、完了時にアーカイブ方針を提案

## Affected Files
- 追加予定
  - `docs/plans/archive/2026/02/20260204-build-and-test-stabilization.md`
  - `global.json`（必要な場合）
  - `tests/` 配下のテストプロジェクト一式
- 更新予定
  - `src/GameLauncherWithGit.App/GameLauncherWithGit.App.sln`
  - `src/GameLauncherWithGit.App/GameLauncherWithGit.App.csproj`
  - `docs/design/maui-blazor-architecture.md`（必要な場合のみ）
  - `.ai_context/system_spec.yaml`（必要な場合のみ）

## Risks
- ローカル環境の .NET SDK / MAUI workload 組み合わせ差異により、再現性が低下する可能性がある。
- MAUIアプリ本体への直接参照では、テスト実行ターゲットがWindows依存になり実行コストが上がる。
- ビルド設定修正時に既存の実行ターゲット（ARM64/x64）の挙動が変わる可能性がある。

## Design Check
- 判定: **現時点では不要（要再判定）**
- 理由: 現時点の主目的はビルド安定化とテスト追加であり、機能仕様の追加は想定していない。
- 追記条件: アーキテクチャ分割（例: Coreライブラリ新設）まで踏み込む場合は、`docs/design/` の更新を実施する。

## Notes / Logs
- 2026-02-04: `dotnet build src/GameLauncherWithGit.App/GameLauncherWithGit.App.sln` で `MSB3073 (XamlCompiler.exe exit code 1)` を確認。
- 2026-02-04: `dotnet build --no-restore` でも同様に `XamlCompiler.exe` 失敗を再現。
- 2026-02-04: `-p:RuntimeIdentifier=win10-x64` 指定時、`obj/*.tmp へのアクセス拒否` も発生（再現あり）。
- 2026-02-04: サンドボックス実行では `obj/*.tmp` 削除が拒否されるため、`dotnet build` / `dotnet test` は通常権限実行に切り替え。
- 2026-02-04: `src/GameLauncherWithGit.App/GameLauncherWithGit.App.csproj` を更新し、`TargetFrameworks` 化と MAUI 明示 `PackageReference` を追加。
- 2026-02-04: `ThumbnailService` の `ImageFormat` 型競合をエイリアスで解消し、`dotnet build` 成功を確認。
- 2026-02-04: `tests/GameLauncherWithGit.App.Tests` を追加し、`GameLibraryService` のユニットテスト（6件）を実装。
- 2026-02-04: `dotnet test src/GameLauncherWithGit.App/GameLauncherWithGit.App.sln --no-build` で 6/6 件成功を確認。
- 2026-02-04: 今回変更はビルド設定とテスト追加に限定され、機能仕様・アーキテクチャ変更がないため `docs/design/` / `.ai_context/` の更新は不要と判定。
