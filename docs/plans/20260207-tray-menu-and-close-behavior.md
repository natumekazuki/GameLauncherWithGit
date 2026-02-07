# 目的
- ウィンドウを閉じたときにアプリを終了せず、トレイ常駐へ遷移できるようにする。
- トレイメニューから主要操作（今すぐ同期 / 設定導線 / ログ導線 / 終了）を実行可能にする。

# Design Check
- 判定: 必須（Windows連携ロジックの拡張）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - `TrayService` にメニュー操作責務を追加
  - アプリ終了/常駐継続フローを追記

# タスクリスト
- [ ] トレイメニュー操作の抽象インターフェースを追加（今すぐ同期 / 設定導線 / ログ導線 / 終了）
- [ ] `TrayService` に右クリックメニュー表示とコマンド発火を実装
- [ ] `App` 側でウィンドウ閉じる操作を常駐化（閉じる = 非表示、終了は明示操作のみ）
- [ ] トレイメニューの「今すぐ同期」で登録済みリポジトリを即時キュー投入
- [ ] トレイメニューの「設定」「ログ」導線を既存UI/サービスに接続
- [ ] `docs/design/maui-blazor-architecture.md` を実装内容に同期
- [ ] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Infrastructure/Services/TrayService.cs`
- `src/GameLauncherWithGit/Infrastructure/Abstractions/ITrayService.cs`
- `src/GameLauncherWithGit/App.xaml.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/MauiProgram.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-tray-menu-and-close-behavior.md`

# リスク
- Win32メッセージ処理の不整合でトレイメニュー表示が不安定になる可能性
- 閉じる挙動変更で、ユーザーが「終了した」と誤認する可能性
- 「設定」導線の実装方法によってはUI状態管理が複雑化する可能性
