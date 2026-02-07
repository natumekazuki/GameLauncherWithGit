# 目的
- `FileSystem.AppDataDirectory/logs` のログ肥大化を防ぐため、保持日数とサイズ上限によるローテーションを実装する。
- 環境設定画面からログ運用設定を保存できるようにする。

# Design Check
- 判定: 必須（設定モデル拡張 + ログ管理ロジック追加）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - `AppSettingsService` の管理対象にログ運用設定を追記
  - `LogAccessService` の責務に起動時メンテナンス（削除/ローテーション）を追記

# タスクリスト
- [x] `AppSettings` に `LogRetentionDays` / `LogMaxFileSizeMb` を追加し正規化範囲を定義
- [x] `Home.razor` の同期・通知設定にログ運用設定入力を追加して保存導線を統合
- [x] `ILogAccessService` にログメンテナンスAPIを追加
- [x] `LogAccessService` に起動時メンテナンス（保持日数削除・サイズ上限ローテーション）を実装
- [x] `Home.razor` 起動初期化でログメンテナンスを実行し失敗時通知を追加
- [ ] `docs/design/maui-blazor-architecture.md` を実装内容へ同期
- [ ] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Models/AppSettings.cs`
- `src/GameLauncherWithGit/Infrastructure/Abstractions/ILogAccessService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LogAccessService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-log-rotation-settings.md`

# リスク
- しきい値設定が小さすぎると必要な調査ログが失われる可能性
- ローテーション中の同時書き込みで例外が出る可能性
- 起動時メンテナンス失敗時に通知ループが発生する可能性
