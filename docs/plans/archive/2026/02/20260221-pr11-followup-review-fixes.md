# 実行計画: PR#11 フォローアップ指摘修正

## Goal
- セーブリンク編集 UI のイベント束縛不具合を解消し、編集保存時の部分更新を防止する。

## Design Check
- 判定: 必須（編集保存フローの順序・検証責務の変更）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - 編集保存時はセーブリンク入力を先行検証し、検証失敗時はゲーム本体更新を行わない方針を追記する。

## Task List
- [x] 1. `Home.razor` の `for` ループでインデックスをローカル変数へ退避し、各イベントハンドラが正しい行を参照するよう修正する。
- [x] 2. `ISaveLinkService` / `SaveLinkService` に保存前検証 API を追加し、編集保存時にゲーム更新より先に検証するよう修正する。
- [x] 3. 設計書に編集保存時の検証順序を追記する。
- [x] 4. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-pr11-followup-review-fixes.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Application/Abstractions/ISaveLinkService.cs`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`
- `docs/design/game-save-link-manager.md`

## Risks
- 検証 API の追加で呼び出し漏れがあると、画面ごとに挙動が不整合になる可能性がある。
- UI ループの変数変更でボタンの disabled 判定と実行対象がずれる可能性がある。

## Notes / Logs
- 2026-02-21: 指摘内容を確認。`for` ループの `index` キャプチャと、編集保存時の `UpdateAsync -> ReplaceForGameAsync` 順序を修正対象として確定。
- 2026-02-21: `Home.razor` のセーブリンク一覧描画で `currentIndex` を導入し、各ボタン/チェックボックスのコールバックが反復ごとの固定インデックスを参照するよう修正。
- 2026-02-21: `ISaveLinkService`/`SaveLinkService` に `ValidateForGame` を追加し、編集保存では `UpdateAsync` より前に入力検証を実行するよう修正。
- 2026-02-21: `docs/design/game-save-link-manager.md` に編集保存時の先行検証ルールを追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
