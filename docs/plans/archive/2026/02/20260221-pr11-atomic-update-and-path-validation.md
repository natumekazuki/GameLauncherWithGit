# 実行計画: PR#11 原子更新と相対パス拒否

## Goal
- 編集保存時の「ゲーム更新 + セーブリンク保存」を原子的に実行し、部分適用を防止する。
- セーブリンク入力で相対パスを拒否し、意図しない絶対化を防ぐ。

## Design Check
- 判定: 必須（更新トランザクションの責務追加と入力検証ルール変更）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - 編集保存時は単一トランザクションで `Games` と `GameSaveLinks` を更新する方針を追記する。
  - セーブリンク入力は絶対パス（fully qualified）のみ許可するルールを追記する。

## Task List
- [x] 1. `IGameLibraryStore` / `SqliteGameLibraryStore` にゲーム更新+セーブリンク置換のトランザクション API を追加する。
- [x] 2. `IGameLibraryService` / `GameLibraryService` に原子的編集保存 API を追加する。
- [x] 3. `Home.razor` の編集保存で新 API を使用し、部分更新経路を除去する。
- [x] 4. `SaveLinkService.NormalizeAbsolutePath` で相対パスを拒否する。
- [x] 5. 設計書更新と Windows ターゲットビルド検証を行う。

## Affected Files
- `docs/plans/archive/2026/02/20260221-pr11-atomic-update-and-path-validation.md`
- `src/GameLauncherWithGit/Infrastructure/Abstractions/IGameLibraryStore.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`
- `src/GameLauncherWithGit/Application/Abstractions/IGameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/game-save-link-manager.md`

## Risks
- トランザクション API 追加により既存更新ロジックとの重複が増える可能性がある。
- 絶対パス判定を厳格化すると、これまで通っていた入力が保存不可になる可能性がある。

## Notes / Logs
- 2026-02-21: 指摘内容を確認。編集保存の部分適用と相対パス受け入れを修正対象として確定。
- 2026-02-21: `IGameLibraryStore` に `UpsertWithSaveLinksAsync` を追加し、`SqliteGameLibraryStore` で `Games` upsert と `GameSaveLinks` 置換を同一トランザクションで実行するよう実装。
- 2026-02-21: `IGameLibraryService` / `GameLibraryService` に `UpdateWithSaveLinksAsync` を追加し、編集保存の原子的 API を提供。
- 2026-02-21: `Home.razor` の編集保存フローを `UpdateWithSaveLinksAsync` 呼び出しへ変更し、`UpdateAsync` 後の別書き込み経路を除去。
- 2026-02-21: `SaveLinkService` に `NormalizeForGame` を追加し、`NormalizeAbsolutePath` で `Path.IsPathFullyQualified` を使用して相対パスを拒否。
- 2026-02-21: `docs/design/game-save-link-manager.md` に編集保存トランザクション方針と絶対パス必須ルールを追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
