# 実行計画: main差分セーブリンク解除リファクタリング

作成日: 2026-02-23
対象: `src/GameLauncherWithGit`

## Goal
- `main...saveLinkRemove` 差分のうち、セーブリンク解除系の複雑化した分岐を整理して可読性と保守性を高める。
- 既存の不具合修正挙動（ロールバック・安全判定）は維持する。

## Design Doc Check
- [x] 本作業は仕様変更ではなく実装整理（リファクタリング）であるため、`docs/design/` の更新は不要。

## Task List
- [x] 1. `SaveLinkService` の解除/ロールバック処理を責務単位で分割し、メインフローの見通しを改善する。
- [x] 2. `LocalSaveLinkOperator` のパス正規化・妥当性判定重複を共通ヘルパー化する。
- [x] 3. 例外メッセージとログ形式の一貫性を保ちながら不要重複を削減する。
- [x] 4. Windowsターゲットでビルド確認を実施する。
- [x] 5. 計画ファイルを更新してアーカイブへ移動する。

## Affected Files
- `docs/plans/20260223-diff-main-refactor-save-link-remove.md`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LocalSaveLinkOperator.cs`
- （必要時）`src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- （必要時）`src/GameLauncherWithGit/Infrastructure/Models/JunctionRemoveResult.cs`

## Risks
- 分岐整理時にロールバック対象判定（`DidChangeLocalPath`）の意味を壊すと再発する。
- ヘルパー化でエラーメッセージが変わると既存UI通知文言に影響する。

## Notes / Logs
- 2026-02-23: `main...HEAD` 差分はセーブリンク解除機能とその追加修正で構成され、`LocalSaveLinkOperator` の分岐が大きく増加していることを確認。
- 2026-02-23: `SaveLinkService` の `UnlinkRemovedLinksAsync` から「解除対象抽出」「失敗例外組み立て」を分離し、メインループの責務を簡素化。
- 2026-02-23: `LocalSaveLinkOperator` に `TryNormalizeDistinctPaths` を追加し、`Ensure/Remove/Restore` の入力検証重複を集約。
- 2026-02-23: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 /p:UseAppHost=false` 成功（0 warning / 0 error）。
