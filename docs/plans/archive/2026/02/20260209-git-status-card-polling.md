# 目的
- ゲームカードごとに Git 状態を色で判別できるようにし、視認性を上げる。
- 手動で Git 状態を再取得できる「Gitステータス更新」導線を追加する。
- 自動更新は既存の監視同期と同じ秒数（`SyncDebounceSeconds`）でポーリング実行する。

# Design Check
- 判定: 必須（UI表示仕様追加 + 自動同期ロジック追加）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - Home カードに Git 状態の視覚表現（枠/背景色）を追加
  - Home で手動更新ボタンと定期ポーリング更新を追加

# タスクリスト
- [x] `Home.razor` に Git 状態ポーリング基盤を追加（手動更新 + 自動更新）
- [x] `Home.razor` に Git 状態判定ロジックを追加（clean/dirty/ahead/behind/diverged/error）
- [x] `Home.razor` のゲームカードへ Git 状態クラスを適用
- [x] `app.css` に Git 状態別のカード枠/背景色を追加
- [x] `docs/design/maui-blazor-architecture.md` を実装仕様へ同期
- [x] Windows ターゲットでビルド検証

# 変更対象ファイル
- `docs/plans/20260209-git-status-card-polling.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`

# リスク
- 複数リポジトリを短周期でポーリングすると Git コマンド実行数が増え、UI応答が低下する可能性
- `git status --porcelain --branch` の出力差異により ahead/behind 判定を誤る可能性
- 既存のゲーム状態バッジ（同期中/エラー）と Git 色表現が競合し、意図が伝わりにくくなる可能性

# Notes / Logs
- 実行コマンド: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false`
- 結果: 失敗（起動中 `GameLauncherWithGit` プロセスによる DLL ロック）
- 実行コマンド: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false -p:BuildProjectReferences=false`
- 結果: 成功（0 warnings / 0 errors）
