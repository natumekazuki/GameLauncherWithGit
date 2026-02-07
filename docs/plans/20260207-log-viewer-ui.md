# 目的
- `app-events.jsonl` の内容をアプリ内で確認できるログ閲覧UIを追加する。
- 障害調査の初動を速めるため、最低限の絞り込み（レベル/キーワード）とコピー導線を提供する。

# Design Check
- 判定: 必須（UI拡張 + ログ参照機能追加）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - `LogAccessService` の責務に「ログ参照」を追記
  - Home の環境設定からログビューア導線を追記

# タスクリスト
- [x] ログ表示用モデル（時刻/レベル/メッセージ/詳細）を追加
- [x] `ILogAccessService` を拡張し、`app-events.jsonl` から直近ログ取得APIを追加
- [x] `LogAccessService` に JSONL 読込・簡易フィルタ（レベル/キーワード）処理を実装
- [ ] `Home.razor` の環境設定に「ログビューア」UIを追加（件数指定、フィルタ、更新、コピー）
- [ ] ログ読込失敗時の通知とフォールバック動作を実装
- [ ] `docs/design/maui-blazor-architecture.md` を実装内容に同期
- [ ] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Models/`（ログ表示モデル追加）
- `src/GameLauncherWithGit/Infrastructure/Abstractions/ILogAccessService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LogAccessService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-log-viewer-ui.md`

# リスク
- ログ件数が多い環境でUI描画が重くなる可能性
- JSONLの破損行がある場合にパース例外で全件表示できなくなる可能性
- UIからコピーできる情報に機微情報が含まれる可能性
