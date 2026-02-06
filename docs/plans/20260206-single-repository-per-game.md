# 実行計画: ゲーム紐づけリポジトリを単一化

## Goal
- ゲームに紐づけるリポジトリを「1つのみ」に変更する。
- 登録/編集モーダルでは手入力を廃止し、フォルダ選択のみで設定する。

## Design Check
- 判定: **必要**
- 理由: ゲーム設定モデル（関連リポジトリの複数→単一）変更に該当するため、`docs/design/maui-blazor-architecture.md` の更新が必要。

## Task List
- [x] 1. アプリケーションモデルを単一リポジトリ仕様へ変更する（`GameCardItem` / `GameEditInput`）。
- [x] 2. `GameLibraryService` と `LauncherService` を単一リポジトリ仕様へ更新する。
- [x] 3. SQLite保存ロジックを単一リポジトリ仕様へ更新する（既存データ互換を維持）。
- [x] 4. `Home.razor` の関連リポジトリUIを単一選択仕様へ変更し、手入力を削除する。
- [x] 5. `app.css` を調整し、不要な一覧編集スタイルを整理する。
- [ ] 6. ビルド確認を実施し、`docs/design/maui-blazor-architecture.md` を更新する。
- [ ] 7. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Application/Models/GameCardItem.cs`
- `src/GameLauncherWithGit/Application/Models/GameEditInput.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/LauncherService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-single-repository-per-game.md`

## Risks
- 既存DBに複数リポジトリが保存されている場合、先頭1件のみ採用される。
- 手入力廃止により、Pickerが利用できない環境では設定変更ができない。

## Notes / Logs
- 2026-02-06: ユーザー指示「手入力は不要」「ゲームに紐づけるリポジトリは1つだけ」を反映するため計画開始。
- 2026-02-06: `GameCardItem` / `GameEditInput` の関連リポジトリを単一パス（`RelatedRepositoryPath`）へ変更。
- 2026-02-06: `GameLibraryService` / `LauncherService` を単一リポジトリ仕様へ更新。
- 2026-02-06: `SqliteGameLibraryStore` を単一リポジトリ保存へ更新し、旧JSON配列から先頭1件を移行する互換処理を追加。
- 2026-02-06: `Home.razor` の関連リポジトリ編集を単一選択（フォルダ選択/解除）へ変更し、手入力と一覧編集を削除。
- 2026-02-06: `app.css` の一覧編集スタイルを整理し、単一リポジトリ表示スタイルへ置換。
