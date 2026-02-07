# 実行計画: ゲームカードのサムネイル表示モード追加

## Goal
- ゲーム登録/編集でサムネイル画像を選択し、保存時に表示用サムネイルを生成して保持する。
- サムネイルが登録されているゲームカードは、文字情報を非表示にし、「画像 + 操作ボタン」のみ表示する。

## Design Check
- 判定: **必要**
- 理由: ゲーム設定モデルとカード描画ロジック（表示モード切替）を変更するため、`docs/design/maui-blazor-architecture.md` の更新が必要。

## Task List
- [x] 1. ゲームモデル/入力モデルにサムネイル情報を追加する。
- [x] 2. SQLite スキーマと永続化処理をサムネイル列対応に更新する（既存データ互換維持）。
- [x] 3. `ThumbnailService` を実装し、画像を長辺 512px の PNG に変換して `%AppData%` 配下へ保存する。
- [x] 4. `GameLibraryService` の登録/更新処理でサムネイル生成を組み込み、失敗時はサムネイル無しで継続できるようにする。
- [x] 5. `Home.razor` の登録/編集モーダルにサムネイル選択UIを追加する。
- [x] 6. `Home.razor` のカード描画を「通常モード / サムネイルモード」に分岐し、サムネイル時は画像+ボタンのみ表示する。
- [x] 7. `app.css` と `docs/design/maui-blazor-architecture.md` を更新し、ビルドで検証する。
- [x] 8. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` に移動する。

## Affected Files
- `src/GameLauncherWithGit/Application/Models/GameCardItem.cs`
- `src/GameLauncherWithGit/Application/Models/GameEditInput.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/ThumbnailService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `src/GameLauncherWithGit/GameLauncherWithGit.csproj`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/archive/2026/02/20260206-thumbnail-card-mode.md`

## Risks
- 画像デコード非対応フォーマットや破損ファイルでサムネイル生成が失敗する可能性がある。
- サムネイルデータURI化の実装次第でカード描画時のメモリ使用量が増える可能性がある。
- 古い端末で画像縮小処理が重い場合、保存処理の体感時間が延びる可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー要望「サムネイル画像登録済みカードは画像+ボタンのみ表示」を受け、計画開始。
- 2026-02-06: `GameCardItem` と `GameEditInput` にサムネイル情報（`ThumbnailPath` / `ThumbnailSourcePath` / `ClearThumbnail`）を追加。
- 2026-02-06: `SqliteGameLibraryStore` に `ThumbnailPath` 列を追加し、既存DBへ `ALTER TABLE` で追記する互換処理を実装。
- 2026-02-06: `ThumbnailService` をプレースホルダーから実装へ差し替え（長辺512px縮小、PNG化、`%AppData%/thumbnails` 保存）。
- 2026-02-06: `GameLibraryService` にサムネイル生成処理を接続し、生成失敗時は既存値/未設定で継続するフォールバックを追加。
- 2026-02-06: `Home.razor` にサムネイル参照/解除UIとカード表示モード切替（サムネあり時は文字情報非表示）を実装。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` で 0 エラーを確認。
