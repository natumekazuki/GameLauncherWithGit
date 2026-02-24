# 実行計画: 実行ファイル未設定許容と起動ボタン無効化

## Goal
- ゲーム新規追加/編集で実行ファイルパス未設定でも保存可能にする。
- 実行ファイルパス未設定のゲームは起動ボタンを無効化し、誤操作を防ぐ。

## Design Check
- 判定: 必須（保存バリデーション仕様とUI操作可否の変更）
- 対応:
  - `docs/design/maui-blazor-architecture.md` の UI 責務に仕様追記する。

## Task List
- [x] 1. `Home.razor` の保存前バリデーションから実行ファイル必須チェックを除去する。
- [x] 2. `Home.razor` のゲームカード起動ボタンに未設定時 `disabled` 判定を追加する。
- [x] 3. `GameLibraryService.NormalizeInput` の実行ファイル必須チェックを除去し、空文字保存を許容する。
- [x] 4. `docs/design/maui-blazor-architecture.md` に仕様変更を反映する。
- [x] 5. Windows ターゲットでビルド確認する。

## Affected Files
- `docs/plans/archive/2026/02/20260224-allow-empty-executable-and-disable-launch.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `docs/design/maui-blazor-architecture.md`

## Risks
- 既存データに空パスが混在すると、起動不可カードが増える可能性がある。
- 実行ファイル未設定でも保存できるため、登録直後に起動できない状態が仕様として増える。

## Notes / Logs
- 2026-02-24: ユーザー要望「実行ファイルパスなしで保存可能。未設定時は起動ボタンdisabled」を着手。
- 2026-02-24: `Home.razor` で実行ファイル未設定時の表示を「未設定」に統一し、起動ボタンを `disabled` に変更。
- 2026-02-24: `SaveEditorAsync` の実行ファイル必須チェックを削除。
- 2026-02-24: `GameLibraryService.NormalizeInput` の実行ファイル必須チェックを削除し、空文字保存を許容。
- 2026-02-24: `docs/design/maui-blazor-architecture.md` へ仕様反映。
- 2026-02-24: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0` 成功（0 error）。
- 2026-02-24: 完了済み計画のため `docs/plans/archive/2026/02/` へ移動。
