# 実行計画: PR#11 追加レビュー指摘修正

## Goal
- PR#11 の追加レビュー指摘 2 件を解消し、ゲーム作成時の一貫性とセーブ移行失敗時のロールバック安全性を高める。

## Design Check
- 判定: 必須（作成フローの失敗補償と移行ロールバック仕様の明確化）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - ゲーム新規作成とセーブリンク保存を実質的に一体化し、途中失敗時の補償削除を仕様として追記する。
  - セーブ移行失敗時に `targetPath` 側へ今回追加したファイルをロールバックする方針を追記する。

## Task List
- [x] 1. `Home.razor` の新規作成フローで、`CreateAsync` 後にセーブリンク保存が失敗した場合の補償削除を実装する。
- [x] 2. `LocalSaveLinkOperator` の移行失敗時に `targetPath` 側の追加分をロールバックする処理を実装する。
- [x] 3. `docs/design/game-save-link-manager.md` に失敗補償と `targetPath` ロールバック仕様を反映する。
- [x] 4. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-pr11-additional-review-fixes.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Infrastructure/Services/LocalSaveLinkOperator.cs`
- `docs/design/game-save-link-manager.md`

## Risks
- 作成失敗時の削除処理が別要因で失敗した場合、一部データ（ゲームやサムネイル）が残る可能性がある。
- `targetPath` ロールバックで削除対象判定を誤ると、既存データを誤削除するリスクがある。

## Notes / Logs
- 2026-02-21: PR#11 の未解決スレッドを再確認し、追加指摘 2 件（`Home.razor`, `LocalSaveLinkOperator.cs`）を対応対象として確定。
- 2026-02-21: `Home.razor` に新規作成用の補償削除ヘルパーを追加し、`CreateAsync` 成功後に `ReplaceForGameAsync` が失敗した場合は `DeleteAsync` でロールバックするよう修正。
- 2026-02-21: `LocalSaveLinkOperator` で `CopyDirectoryStrict` が作成したファイル/ディレクトリ差分を追跡し、移行失敗時に `targetPath` 側の追加分を削除するロールバックを追加。
- 2026-02-21: `docs/design/game-save-link-manager.md` の適用フローに `targetPath` 差分ロールバックと新規作成時の補償削除仕様を追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
