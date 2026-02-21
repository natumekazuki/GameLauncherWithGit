# 実行計画: main 差分ベースの安全リファクタリング（2）

## Goal
- `Home.razor` のセーブリンク編集補助ロジック重複を削減する。
- `SqliteGameLibraryStore` の `gameId` 正規化とセーブリンク削除 SQL 重複を削減する。

## Design Check
- 判定: 不要（挙動変更を伴わない整理）
- 対象:
  - なし

## Task List
- [x] 1. `Home.razor` のインデックス範囲チェックとフォルダ選択処理を共通化する。
- [x] 2. `SqliteGameLibraryStore` の `gameId` 正規化とリンク削除処理を共通ヘルパーへ集約する。
- [x] 3. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-diff-main-refactor-2.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`

## Risks
- 共通化時の引数取り回しミスで、対象インデックスや対象 gameId がずれる可能性がある。

## Notes / Logs
- 2026-02-21: `main...HEAD` 差分の継続整理として、UI補助メソッドとSQLiteストアの重複削減にスコープを限定。
- 2026-02-21: `Home.razor` にインデックス妥当性判定/取得ヘルパーと汎用フォルダ選択ヘルパーを追加し、`PickEditorSaveLocalPathAsync` / `PickEditorSaveTargetPathAsync` の重複を削減。
- 2026-02-21: `SqliteGameLibraryStore` に `NormalizeGameId` と `DeleteSaveLinksByGameIdAsync` を追加し、`Get/Replace/Delete` 系での重複ロジックを集約。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
