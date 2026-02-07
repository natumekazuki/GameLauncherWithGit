# 実行計画: 設定導線（自動起動・エラーログ確認）の追加

## Goal
- ホーム画面から細かい設定へ遷移できる導線を追加する。
- 「PC起動時の自動起動」の有効/無効を設定できるようにする。
- エラーログ確認の導線（最新ログを開く/ログフォルダを開く）を追加する。

## Design Check
- 判定: **必要**
- 理由: UI操作フローに設定モーダルを追加し、インフラサービス（自動起動・ログ確認）を接続するため、`docs/design/maui-blazor-architecture.md` の更新が必要。

## Task List
- [x] 1. `AutoStartService` を Windows 実装（Runキー）へ更新する。
- [x] 2. ログ確認導線用サービスを追加する（ログ保存先、最新ログ/フォルダを開く）。
- [x] 3. DI登録を更新し、`Home.razor` で設定モーダルを追加する。
- [x] 4. 設定モーダルから自動起動トグルとログ確認操作を実行できるようにする。
- [x] 5. `app.css` に設定導線UIのスタイルを追加する。
- [x] 6. `docs/design/maui-blazor-architecture.md` を更新し、ビルドで検証する。
- [x] 7. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Infrastructure/Services/AutoStartService.cs`
- `src/GameLauncherWithGit/Infrastructure/Abstractions/` 配下（ログ導線用インターフェース）
- `src/GameLauncherWithGit/Infrastructure/Services/` 配下（ログ導線用サービス）
- `src/GameLauncherWithGit/MauiProgram.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/archive/2026/02/20260206-settings-guidance-entrypoints.md`

## Risks
- Runキー登録はパッケージング形態により起動パスの扱いが異なるため、環境依存で失敗する可能性がある。
- ログファイルが存在しない初期状態で「最新ログを開く」操作が失敗する可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー要望「自動起動やエラーログ確認の導線」に対応するため計画開始。
- 2026-02-06: `AutoStartService` を Windows Runキー (`HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run`) の実装へ更新。
- 2026-02-06: `ILogAccessService` / `LogAccessService` を追加し、`%AppData%/logs/app-errors.log` への追記とログ表示導線を実装。
- 2026-02-06: `MauiProgram` に `ILogAccessService` をDI登録。
- 2026-02-06: `Home.razor` に「環境設定」モーダルを追加し、自動起動トグル・最新エラーログを開く・ログフォルダを開く操作を実装。
- 2026-02-06: 通知がエラーのときに `app-errors.log` へ自動追記する処理を追加。
- 2026-02-06: `app.css` に設定導線UI（ユーティリティバー/設定モーダル）とダークテーマ対応スタイルを追加。
- 2026-02-06: `docs/design/maui-blazor-architecture.md` を更新。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` で 0 エラーを確認。
