# Plan: ログビューア強化（構造化情報の可視化）

## Goal
- 設定画面のログビューアで、運用時に原因追跡しやすい情報（`repositoryId` / `command` / `exitCode` / `stdout` / `stderr`）を表示・検索・コピーできるようにする。

## Design Doc Check
- 本対応はログ表示仕様と検索仕様の変更を含むため、`docs/design/maui-blazor-architecture.md` のログビューア節を更新する（必須）。

## Task List
- [ ] 現行の構造化ログ（`app-events.jsonl`）に含まれるキーを確認し、UIに出す対象キーを確定する。
- [ ] `LogViewerEntry` を拡張し、追加メタデータ（`RepositoryId` / `Command` / `ExitCode` / `Stdout` / `Stderr` など）を保持できるようにする。
- [ ] `LogAccessService` のレコード変換処理を更新し、上記メタデータを `JsonElement` から抽出する。
- [ ] キーワード検索対象を追加メタデータにも拡張する。
- [ ] `Home.razor` のログビューア表示を更新し、重要メタ情報を行内表示、長文（stdout/stderr）は折りたたみ可能な詳細表示にする。
- [ ] 「コピー」機能の出力フォーマットを更新し、表示中ログの追加メタデータも含める。
- [ ] `docs/design/maui-blazor-architecture.md` を更新し、ログビューアの表示項目・検索仕様・コピー仕様を同期する。
- [ ] ビルドを実行して回帰を確認する（`dotnet build ...`。実行中ロック時は `dotnet msbuild /t:Compile` で代替）。

## Affected Files
- `src/GameLauncherWithGit/Application/Models/LogViewerEntry.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LogAccessService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-log-viewer-ui-enhancement.md`

## Risks
- ログイベントごとにデータキーが揃っていない場合、表示の欠落や誤解を招く可能性がある。
- `stdout` / `stderr` の長文をそのまま描画すると設定モーダルの操作性が低下する可能性がある。
- キーワード検索対象の拡張で件数が多いと体感速度が落ちる可能性がある。

## Notes / Logs
- 2026-02-07: 既存ログビューアは実装済み（表示件数・レベル・キーワード・コピー）。本計画は「構造化情報の拡張表示」にフォーカスする。
