# 実行計画: main 差分ベースの安全リファクタリング（3）

## Goal
- `LocalSaveLinkOperator` の重複ロジックを整理し、例外処理と戻り値生成の可読性を向上させる。

## Design Check
- 判定: 不要（挙動変更なしの整理）
- 対象:
  - なし

## Task List
- [x] 1. `ConvertDirectoryToJunctionAsync` のロールバック重複呼び出しを共通化する。
- [x] 2. `CopyDirectoryStrict` の `CopyDirectoryResult` 生成重複を共通化する。
- [x] 3. `CreateJunctionAsync` のキャンセル時プロセス終了処理を補助メソッド化する。
- [x] 4. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-diff-main-refactor-3.md`
- `src/GameLauncherWithGit/Infrastructure/Services/LocalSaveLinkOperator.cs`

## Risks
- 例外処理経路の整理で、ログやエラーメッセージが意図せず変化する可能性がある。

## Notes / Logs
- 2026-02-21: `main...HEAD` 差分の継続整理として `LocalSaveLinkOperator` の重複削減にスコープを限定。
- 2026-02-21: `ConvertDirectoryToJunctionAsync` の失敗経路で重複していたロールバック呼び出しを `RollbackConversion` に集約。
- 2026-02-21: `CopyDirectoryStrict` に `BuildCopyResult` を導入し、結果生成ロジックの重複を削減。
- 2026-02-21: `CreateJunctionAsync` のキャンセル時プロセス終了処理を `TryKillProcess` に分離。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
