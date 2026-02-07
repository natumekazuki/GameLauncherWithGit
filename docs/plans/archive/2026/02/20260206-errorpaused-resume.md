# ErrorPaused再開導線の実装計画

## Goal
- `ErrorPaused` のリポジトリに対し、ユーザーが Home 画面から手動で同期再開できるようにする。
- 再開操作時はデバウンス待ちなしで同期を即時実行する。

## Task List
- [x] `ISyncOrchestrator` に手動再開 API を追加する。
- [x] `SyncOrchestrator` に即時再開フローを実装する（キュー制御との整合を含む）。
- [x] `Home` に ErrorPaused 判定と「再開」ボタンを追加する。
- [x] 設計ドキュメントを更新し、ビルド確認する。

## Affected Files
- `src/GameLauncherWithGit/Application/Abstractions/ISyncOrchestrator.cs`
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-errorpaused-resume.md`

## Risks
- 即時再開と既存デバウンスキューの競合で二重実行が発生する可能性。
- UI再描画タイミング次第で、状態表示が一時的に遅れる可能性。

## Design Check
- 同期制御仕様の変更を含むため、`docs/design/maui-blazor-architecture.md` の更新を必須とする。

## Notes / Logs
- `ResumeRepositorySyncAsync` を追加し、手動再開時はデバウンスをスキップして即時実行するようにした。
- Home でリポジトリ状態（`IRepositoryStateStore`）を参照し、`ErrorPaused` 時のみ「再開」ボタンを表示するようにした。
- `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` でビルド成功（警告0/エラー0）。
