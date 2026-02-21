# 実行計画: main 差分ベースの安全リファクタリング（4）

## Goal
- `LauncherService` のステータス更新失敗分岐を整理し、可読性を向上する。
- `PathPickerService` の Windows ピッカー実行パターンを共通化する。

## Design Check
- 判定: 不要（挙動変更なしの整理）
- 対象:
  - なし

## Task List
- [x] 1. `LauncherService.LaunchAsync` の失敗分岐（Error ステータス更新 + return）を共通化する。
- [x] 2. `PathPickerService` の Windows 実行/例外処理重複を補助メソッド化する。
- [x] 3. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-diff-main-refactor-4.md`
- `src/GameLauncherWithGit/Application/Services/LauncherService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/PathPickerService.cs`

## Risks
- 共通化時の引数渡しミスで、例外メッセージやログ文言がずれる可能性がある。

## Notes / Logs
- 2026-02-21: `main...HEAD` 差分の継続整理として、ランチャーとWindowsピッカーの重複削減にスコープを限定。
- 2026-02-21: `LauncherService` に `SetErrorStatusAndReturnAsync` を追加し、失敗時ステータス更新 + return の重複を削減。
- 2026-02-21: `PathPickerService` に `ExecuteWindowsPickerAsync` / `GetRequiredWindowHandle` を追加し、Windowsピッカーの実行・例外処理重複を共通化。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
