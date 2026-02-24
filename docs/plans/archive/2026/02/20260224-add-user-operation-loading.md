# 実行計画: ユーザー起点の遅延操作ローディング導入

## Goal
- 処理内容を問わず、遅延が発生し得るユーザー操作に対して一貫したローディング表示を導入し、フリーズ誤認を防ぐ。

## Design Check
- 判定: 必須（UI操作時の状態遷移と表示仕様の変更）
- 対応:
  - `docs/design/maui-blazor-architecture.md` に「ユーザー起点遅延操作の共通ローディング表示」仕様を追記する。

## Task List
- [x] 1. `Home.razor` のユーザー起点 `async` 操作を分類し、ローディング対象を定義する。
- [x] 2. `Home.razor` に共通ローディング状態（表示可否・メッセージ）と補助メソッドを実装する。
- [x] 3. 既存の主要ユーザー操作（起動、保存、削除、設定保存、手動更新、再開、外部オープン、コピー等）へ共通ローディング適用を行う。
- [x] 4. `Home.razor` のマークアップへ全画面ローディングオーバーレイを追加し、操作中メッセージを表示する。
- [x] 5. `wwwroot/css/app.css` にローディングオーバーレイ/スピナーのスタイルを追加する。
- [x] 6. `docs/design/maui-blazor-architecture.md` を更新し、仕様同期を行う。
- [x] 7. ビルドでコンパイル確認し、計画チェックを更新する。

## Affected Files
- `docs/plans/archive/2026/02/20260224-add-user-operation-loading.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`

## Risks
- ローディング対象を過剰に広げると、短時間処理でもオーバーレイが点滅し体感を悪化させる可能性がある。
- 既存の個別フラグ（`_isSaving`, `_isDeleting` など）との競合で、ボタン有効/無効状態が不整合になる可能性がある。
- バックグラウンド更新（定期リフレッシュ）まで対象化すると、ユーザー操作がないのにローディングが出るリスクがある。

## Notes / Logs
- 2026-02-24: ユーザー要望「遅延が入る可能性があるユーザー操作に起因する処理へローディング追加」を着手。
- 2026-02-24: `Home.razor` に共通ローディングスタック（メッセージ付き）を導入し、ユーザー起点の主要 `async` 操作へ適用。
- 2026-02-24: `wwwroot/css/app.css` に全画面ローディングオーバーレイ/スピナーのスタイルを追加。
- 2026-02-24: `docs/design/maui-blazor-architecture.md` の更新日とUI責務/テスト観点へローディング仕様を反映。
- 2026-02-24: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0` は成功（0 error）。全体 `dotnet build GameLauncherWithGit.sln` は既存不具合 `PathPickerService.cs:92 BuildErrorDetail 未定義` で失敗。
- 2026-02-24: 完了済み計画のため `docs/plans/archive/2026/02/` へ移動。
