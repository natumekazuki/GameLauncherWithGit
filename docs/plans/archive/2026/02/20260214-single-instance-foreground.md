# 目的
- 多重起動検知時に新規プロセスを終了するだけでなく、既に常駐しているインスタンスのウィンドウを前面表示できるようにする。

# Design Check
- 判定: 必須（Windows連携ロジックの変更）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - 単一インスタンス制御に「2重起動時の既存ウィンドウ復帰」を追記する

# タスクリスト
- [x] 現行の単一インスタンス判定（`Mutex`）に、既存ウィンドウ前面化処理を追加する
- [x] 既存ウィンドウ特定ロジック（対象プロセスのトップレベルウィンドウ探索）を実装する
- [x] 復帰失敗時のフォールバック（従来どおり即終了）とログ出力を追加する
- [x] `docs/design/maui-blazor-architecture.md` を実装内容へ同期する
- [x] Windowsターゲットでビルド検証する

# 変更対象ファイル
- `src/GameLauncherWithGit/App.xaml.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/archive/2026/02/20260214-single-instance-foreground.md`

# リスク
- ウィンドウ列挙条件が不適切だと既存インスタンスを前面化できない可能性
- フォアグラウンド制御はOS制約の影響を受けるため、環境によっては前面化が不安定な可能性

# Notes / Logs
- `App.xaml.cs` に既存インスタンス前面化処理を追加し、`Process.GetProcessesByName` + `EnumWindows` で対象ウィンドウを探索するようにした。
- 多重起動時は `ShowWindow(SW_SHOW)` / `ShowWindow(SW_RESTORE)` / `SetForegroundWindow` を順に実行し、失敗時は `Trace.WriteLine` にログを残して従来どおり終了する。
- 実行コマンド: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false`
- 結果: ビルド成功（0 warnings / 0 errors）
