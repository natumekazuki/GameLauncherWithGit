# 目的
- Home 画面で `+` / `-` ボタンからゲームカードの表示サイズを変更できるようにする。
- カードサイズ設定を `settings.json` に保存し、再起動後も復元する。

# Design Check
- 判定: 必須（UI操作追加 + 設定モデル拡張）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - Home のユーティリティ操作にカードサイズ変更導線を追記
  - `AppSettingsService` 管理項目にカードサイズ設定を追記

# タスクリスト
- [ ] `AppSettings` にカードサイズ設定値（例: `GameCardSizePercent`）を追加し正規化範囲を定義
- [ ] `Home.razor` に `+` / `-` ボタンを追加し、カードサイズを即時反映する状態管理を実装
- [ ] `Home.razor` の設定保存処理にカードサイズ設定を統合し永続化する
- [ ] `app.css` を更新し、カード幅・高さ・余白をカードサイズ設定に追従させる
- [ ] `docs/design/maui-blazor-architecture.md` を実装内容へ同期
- [ ] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Models/AppSettings.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-card-size-controls.md`

# リスク
- サイズ下限/上限の設計次第でUIが崩れる可能性
- 変更頻度が高い操作で `settings.json` 書き込み頻度が増える可能性
- 既存テーマとの組み合わせで可読性が低下する可能性
