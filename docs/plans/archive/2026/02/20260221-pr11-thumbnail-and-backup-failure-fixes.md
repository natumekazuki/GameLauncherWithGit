# 実行計画: PR#11 サムネイル副作用とバックアップ削除失敗修正

## Goal
- セーブリンク検証失敗時に不要サムネイルが生成される副作用を防ぐ。
- ジャンクション変換成功後のバックアップ削除失敗を非致命扱いにし、不要なロールバックを防ぐ。

## Design Check
- 判定: 必須（更新順序とエラーハンドリング方針の変更）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - 編集保存ではセーブリンク検証をサムネイル生成より先に実施する。
  - バックアップ掃除失敗は警告扱いとし、成功済み変換は維持する方針を追記する。

## Task List
- [x] 1. `GameLibraryService.UpdateWithSaveLinksAsync` でセーブリンク検証を先行実行する。
- [x] 2. `LocalSaveLinkOperator` でバックアップ削除失敗を警告ログ化し、変換成功を維持する。
- [x] 3. 設計書へ方針を追記する。
- [x] 4. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-pr11-thumbnail-and-backup-failure-fixes.md`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LocalSaveLinkOperator.cs`
- `docs/design/game-save-link-manager.md`

## Risks
- バックアップディレクトリが一時的に残るケースが増え、手動メンテナンスが必要になる可能性がある。

## Notes / Logs
- 2026-02-21: 指摘内容を確認。検証前サムネイル生成とバックアップ削除失敗時の過剰ロールバックを修正対象として確定。
- 2026-02-21: `UpdateWithSaveLinksAsync` で `NormalizeForGame` をサムネイル生成前に実行するよう順序を修正。
- 2026-02-21: `LocalSaveLinkOperator` でバックアップ削除を `TryDeleteBackupDirectory` に分離し、削除失敗は警告ログのみで処理継続するよう修正。
- 2026-02-21: `docs/design/game-save-link-manager.md` にバックアップ削除失敗時の扱いと、検証先行によるサムネイル副作用防止方針を追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
