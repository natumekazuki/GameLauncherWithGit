# 起動前同期のコミット条件変更計画

## Goal
- 起動前同期で `fetch` 後にリモート先行状態を判定し、リモートが先行している場合はローカル自動コミットを実行しない。

## Task List
- [x] `LauncherService` の起動前同期順序を `fetch -> remote ahead判定 -> (必要時のみadd/commit) -> pull` に変更する。
- [x] 判定ロジック（`git rev-list --left-right --count HEAD...@{upstream}`）を追加する。
- [x] 設計ドキュメントを更新する。
- [x] ビルド確認を行う。

## Affected Files
- `src/GameLauncherWithGit/Application/Services/LauncherService.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-launcher-remote-ahead-commit-rule.md`

## Risks
- upstream 未設定ブランチで比較コマンドが失敗する可能性。
- 判定結果パース失敗時のフォールバック方針によってコミット可否が意図とずれる可能性。

## Design Check
- 起動前同期仕様変更のため、`docs/design/maui-blazor-architecture.md` の更新を必須とする。

## Notes / Logs
- `LauncherService` を修正し、`fetch` 後に remote ahead 判定を行ってからコミット可否を決定するようにした。
- upstream 未設定時は remote ahead `0` 扱いで継続するようにした。
- `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` は実行中プロセスによるロックで失敗。
- `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` でビルド成功（警告あり / エラー0）。
