# 実行計画: Git連携ゲームのブランチ切り替え・新規作成対応

## Goal
- Git リポジトリを関連付けたゲーム編集モーダル上で、現在ブランチの確認・切り替え（ドロップダウン）・新規ブランチ作成を実行できるようにする。

## Design Check
- 判定: 必須（UI操作フローとGit操作仕様の追加）
- 対応:
  - `docs/design/maui-blazor-architecture.md` にブランチ操作UIと処理フローを追記する。

## Task List
- [x] 1. `Home.razor` のゲーム編集モーダルにブランチ操作セクション（一覧、切り替え、作成入力）を追加する。
- [x] 2. `Home.razor` の `@code` にブランチ一覧取得・現在値反映・切り替え・作成処理を実装する。
- [x] 3. リポジトリ未設定/無効時の無効化制御とエラーメッセージ通知を実装する。
- [x] 4. ブランチ切り替え/作成後にGit状態再評価と必要な再描画を行う。
- [x] 5. `wwwroot/css/app.css` に新しいブランチ操作UIのスタイルを追加する。
- [x] 6. `docs/design/maui-blazor-architecture.md` を更新し、仕様同期する。
- [x] 7. `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0` でビルド確認する。

## Affected Files
- `docs/plans/20260225-branch-api-plan.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`

## Risks
- ブランチ切り替え時に未コミット変更があると `git checkout` が失敗しやすく、ユーザー体験が悪化する可能性がある。
- 同一リポジトリを複数ゲームで共有している場合、1ゲームからの切り替えが他ゲームの表示/同期挙動に影響する。
- detached HEAD や upstream 未設定ブランチでの運用時に既存同期ロジック（pull/push前提）が停止しやすくなる可能性がある。

## Notes / Logs
- 2026-02-25: ユーザー要望「Git連携ゲームのブランチ切り替え（ドロップダウン）と新規作成対応」を着手。
- 2026-02-25: `Home.razor` のゲーム編集モーダルへブランチ操作セクションを追加し、ローカルブランチ一覧取得・切り替え・作成（作成後切り替え）を実装。
- 2026-02-25: ブランチ操作後に `RefreshGitStatusesAsync` を実行し、カードのGit可視化状態を再評価するように変更。
- 2026-02-25: `wwwroot/css/app.css` にブランチ操作UI（select/行レイアウト/ダークテーマ）スタイルを追加。
- 2026-02-25: `docs/design/maui-blazor-architecture.md` にブランチ操作UI仕様とテスト観点を追記。
- 2026-02-25: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0` は成功（0 warning / 0 error）。
