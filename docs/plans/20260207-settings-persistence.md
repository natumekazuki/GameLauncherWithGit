# 目的
- `settings.json` を導入し、同期関連の主要設定を永続化する。
- 環境設定UIから設定を編集・保存できるようにし、再起動後も反映される状態にする。

# Design Check
- 判定: 必須（同期制御ロジックと通知抑制ロジックの設定化）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - 永続化対象設定と反映先サービスを明記
  - `settings.json` の役割を「予定」から「実装済み」に更新

# タスクリスト
- [ ] `AppSettings` モデルと `IAppSettingsService` を追加し、`%AppData%/GameLauncherWithGit/settings.json` 読込/保存を実装
- [ ] `SyncOrchestrator` のデバウンス秒・再試行秒（初期/上限）を設定値参照へ変更
- [ ] `NotificationService` の通知抑制秒を設定値参照へ変更
- [ ] `Home.razor` の環境設定モーダルへ数値設定UIを追加（読込・バリデーション・保存）
- [ ] DI登録を更新し、起動時に既存設定を適用
- [ ] `docs/design/maui-blazor-architecture.md` を実装内容に同期
- [ ] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Models/AppSettings.cs`（新規）
- `src/GameLauncherWithGit/Application/Abstractions/IAppSettingsService.cs`（新規）
- `src/GameLauncherWithGit/Application/Services/AppSettingsService.cs`（新規）
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/NotificationService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/MauiProgram.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-settings-persistence.md`

# リスク
- 保存タイミング次第で UI 表示値と実適用値がずれる可能性
- 不正値入力で同期制御が過度に短周期/長周期になる可能性
- 既存ユーザー（`settings.json` 未作成）向けのデフォルト互換を崩す可能性
