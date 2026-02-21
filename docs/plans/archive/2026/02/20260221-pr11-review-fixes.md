# 実行計画: PR#11 レビュー指摘修正

## Goal
- PR#11 のレビュー指摘 2 件を解消し、保存時の例外漏れと危険なパス組み合わせを防止する。

## Design Check
- 判定: 必須（入力バリデーションと保存時エラーハンドリングのロジック修正）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - セーブリンクのパス制約（親子関係禁止）を仕様として追記する。

## Task List
- [x] 1. `Home.razor` の `SaveEditorAsync` で、`BuildEditorSaveLinkInputs` 例外を既存通知フローで捕捉できるよう修正する。
- [x] 2. `SaveLinkService.NormalizeInputs` に親子パス（`local` と `target` の祖先/子孫関係）禁止バリデーションを追加する。
- [x] 3. `docs/design/game-save-link-manager.md` にパス制約仕様を反映する。
- [x] 4. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/20260221-pr11-review-fixes.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`
- `docs/design/game-save-link-manager.md`

## Risks
- パス判定ロジックの実装ミスで、本来有効なパスが誤って弾かれる可能性がある。
- バリデーションメッセージ変更で既存のUI通知文と整合が崩れる可能性がある。

## Notes / Logs
- 2026-02-21: PR#11 のインラインレビューコメント 2 件を確認。
  - `SaveEditorAsync` が `BuildEditorSaveLinkInputs` 例外を try/catch 外で発生させる。
  - `localPath` と `targetPath` の親子関係を許可しており、移行処理が破綻し得る。
- 2026-02-21: `Home.razor` の `BuildEditorSaveLinkInputs` 呼び出しを `try` ブロック内へ移動し、UI通知フローで例外を捕捉するよう修正。
- 2026-02-21: `SaveLinkService` に `IsAncestorOrDescendantPath` 判定を追加し、`local/target` 親子パスを拒否するよう修正。
- 2026-02-21: `docs/design/game-save-link-manager.md` に親子パス禁止制約を追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
