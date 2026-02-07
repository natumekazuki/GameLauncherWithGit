# 目的
- ゲームカードにピン留め操作を追加し、よく使うゲームを先頭固定できるようにする。
- 並び順を `ピン留め` → `最終プレイ` → `タイトル` に統一する。

# Design Check
- 判定: 必須（状態項目追加 + 永続化 + UI導線追加）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - `GameLibraryStore` の並び順仕様にピン留め優先を追記
  - Home のカード操作にピン留めトグル導線を追記

# タスクリスト
- [x] `GameCardItem` にピン留め状態（`IsPinned`）を追加
- [x] `SqliteGameLibraryStore` に `IsPinned` カラムのマイグレーションと並び順（ピン優先）を実装
- [x] `IGameLibraryService` / `GameLibraryService` にピン留め更新APIを追加
- [x] `Home.razor` にピン留めトグルボタンを追加し、更新後に再読込する
- [x] `app.css` にピン留め状態の視覚スタイルを追加
- [x] `docs/design/maui-blazor-architecture.md` を実装内容へ同期
- [x] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Models/GameCardItem.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`
- `src/GameLauncherWithGit/Application/Abstractions/IGameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-game-pin-order.md`

# リスク
- カード操作ボタン増加でUIが詰まる可能性
- 既存DBに新カラム追加時の移行不整合リスク
- ピン留めと検索の併用時に意図しない並び順に見える可能性

# Notes / Logs
- SQLite は `IsPinned` カラムを後方互換で追加するマイグレーションを実装済み。
- ビルド検証は実行中ロック回避のため `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 --no-dependencies -p:UseAppHost=false` を使用。
