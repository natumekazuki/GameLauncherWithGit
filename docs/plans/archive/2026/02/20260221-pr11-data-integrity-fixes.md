# 実行計画: PR#11 データ整合性フォローアップ修正

## Goal
- セーブリンク取得失敗時に既存リンクが誤削除される経路を遮断する。
- ゲーム削除時の `GameSaveLinks` / `Games` 削除を原子的に実行し、不整合を防止する。

## Design Check
- 判定: 必須（保存可否判定と DB 削除トランザクション制御の変更）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - セーブリンク取得失敗時は「未設定」扱いにせず、編集保存を禁止する運用ルールを追記する。
  - ゲーム削除時はリンク削除とゲーム削除を同一トランザクションで実行することを追記する。

## Task List
- [x] 1. `Home.razor` でセーブリンク取得失敗状態を保持し、表示と編集保存で安全側に倒す。
- [x] 2. `SqliteGameLibraryStore.DeleteAsync` を同一トランザクション化する。
- [x] 3. 設計書に取得失敗時の挙動と削除トランザクション方針を追記する。
- [x] 4. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-pr11-data-integrity-fixes.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`
- `docs/design/game-save-link-manager.md`

## Risks
- 取得失敗時に保存を止めることで、一時障害中に他項目編集もブロックされる。
- トランザクション導入により、キャンセル時の例外伝播タイミングが変わる可能性がある。

## Notes / Logs
- 2026-02-21: 指摘内容を確認。`RefreshSaveLinksAsync` の例外時 `Array.Empty` 代入と `DeleteAsync` の非トランザクション削除を修正対象として確定。
- 2026-02-21: `Home.razor` に `_saveLinkLoadFailedGameIds` を追加し、取得失敗ゲームは要約表示を「取得失敗」に変更、編集開始/保存をブロックするよう修正。
- 2026-02-21: `RefreshSaveLinksAsync` で取得失敗時に空配列を上書きせず、既存キャッシュを保持したまま失敗状態のみ記録するよう修正。
- 2026-02-21: `SqliteGameLibraryStore.DeleteAsync` で `GameSaveLinks` 削除と `Games` 削除を同一トランザクションへ統合。
- 2026-02-21: `docs/design/game-save-link-manager.md` に取得失敗時の保存ブロックと削除トランザクション方針を追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
