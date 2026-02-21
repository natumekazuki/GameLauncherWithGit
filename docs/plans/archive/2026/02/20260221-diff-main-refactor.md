# 実行計画: main 差分ベースの安全リファクタリング

## Goal
- `main` との差分のうち、セーブリンク関連のアプリ層ロジックで重複を削減し、可読性を高める。

## Design Check
- 判定: 不要（挙動変更を伴わないコード整理）
- 対象:
  - なし

## Task List
- [x] 1. `GameLibraryService` の更新処理で重複している「既存ゲーム読み込み + 入力正規化 + サムネイル算出」ロジックを共通化する。
- [x] 2. `SaveLinkService` の入力正規化ロジックを責務ごとに分割し、検証コードの見通しを改善する。
- [x] 3. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-diff-main-refactor.md`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`

## Risks
- 振る舞いを変えない前提のリファクタリングで、例外メッセージや検証順序を意図せず変えてしまうリスクがある。

## Notes / Logs
- 2026-02-21: `main...HEAD` 差分を確認し、挙動変更を避けるためアプリ層2ファイルの重複整理にスコープを限定。
- 2026-02-21: `GameLibraryService` に更新準備コンテキスト（既存ゲーム取得 + 入力正規化）と更新モデル構築を分離し、`UpdateAsync` / `UpdateWithSaveLinksAsync` の重複を削減。
- 2026-02-21: `SaveLinkService` の `NormalizeInputs` を「単一入力正規化」「リンク間競合検証」「保存モデル変換」に分割し、責務を明確化。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
