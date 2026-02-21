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
- [x] トレイメニュー操作の抽象インターフェースを追加（今すぐ同期 / 設定導線 / ログ導線 / 終了）
- [x] `TrayService` に右クリックメニュー表示とコマンド発火を実装
- [x] `App` 終了導線を常駐化（`WM_CLOSE` を `TrayService` で捕捉し、閉じる = 非表示 / 明示終了のみ停止）
- [x] トレイメニューの「今すぐ同期」で登録済みリポジトリを即時キュー投入
- [x] トレイメニューの「設定」「ログ」導線を既存UI/サービスに接続
- [x] `docs/design/maui-blazor-architecture.md` を実装内容に同期
- [x] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Infrastructure/Services/TrayService.cs`
- `src/GameLauncherWithGit/Application/Abstractions/ISettingsPanelService.cs`
- `src/GameLauncherWithGit/Application/Services/SettingsPanelService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/MauiProgram.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-tray-menu-and-close-behavior.md`

# リスク
- Win32メッセージ処理の不整合でトレイメニュー表示が不安定になる可能性
- 閉じる挙動変更で、ユーザーが「終了した」と誤認する可能性
- 「設定」導線の実装方法によってはUI状態管理が複雑化する可能性

# Notes / Logs
- `TrayService` で `WM_TRAY_CALLBACK` / `WM_COMMAND` / `WM_CLOSE` を処理するため、`GWLP_WNDPROC` フックを実装した。
- 設定導線は `ISettingsPanelService` イベントを介して `Home.razor` の設定モーダルを開く方式を採用した。
- 実行コマンド: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false`
- 結果: ビルド成功（0 warnings / 0 errors）
- 追加修正: `WM_TRAY_CALLBACK` の `lParam` を下位16bitで判定するよう修正し、トレイ右クリックイベント取りこぼしを解消。
- 2026-02-21: 回帰確認として `TrayService` のメニューコマンド分岐（今すぐ同期/設定/ログ/終了）と `WM_CLOSE` 常駐化ロジックを静的確認。
- 2026-02-21: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
