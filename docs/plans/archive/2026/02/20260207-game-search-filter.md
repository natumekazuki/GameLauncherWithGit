# 目的
- Home 画面でゲームタイトルを即時検索できるようにし、ゲーム数増加時の操作性を改善する。

# Design Check
- 判定: 必須（UI導線追加）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - Home ユーティリティバーに検索入力導線を追記
  - UI責務にカード絞り込み表示を追記

# タスクリスト
- [x] `Home.razor` のユーティリティバーに検索入力を追加
- [x] タイトル部分一致でカードを絞り込む表示ロジックを実装
- [x] 0件時の案内表示を追加
- [x] `app.css` に検索入力用スタイルを追加
- [x] `docs/design/maui-blazor-architecture.md` を実装内容へ同期
- [x] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-game-search-filter.md`

# リスク
- 検索とカードサイズ変更操作が同一バーで混雑する可能性
- 件数が多い場合に毎描画のフィルタ処理コストが増える可能性

# Notes / Logs
- ビルド検証はアプリ起動中のロック回避のため `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 --no-dependencies -p:UseAppHost=false` を使用。
