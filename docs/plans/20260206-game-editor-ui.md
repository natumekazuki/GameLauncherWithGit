# 実行計画: ゲーム登録/編集UIの実装（SQLite永続化）

## Goal
- ゲーム一覧の「+ 新規追加」から登録フォームを開き、タイトル/実行ファイル/関連リポジトリを登録できるようにする。
- 既存ゲームの編集を可能にし、`LauncherService` が参照する `ExecutablePath` / `RelatedRepositoryPaths` をUI経由で更新できるようにする。
- ゲーム設定データを SQLite に保存し、アプリ再起動後も保持する。

## Design Check
- 判定: **必要**
- 理由: ランチャーUIの操作フロー追加に加えて永続化方式をSQLiteに変更するため、`docs/design/maui-blazor-architecture.md` の更新を行う。

## Task List
- [x] 1. アプリケーション層に登録/編集用モデルとサービス契約（追加・更新）を追加する。
- [x] 2. SQLite 永続化層（DB初期化、テーブル作成、CRUD）を実装する。
- [ ] 3. `GameLibraryService` を SQLite 利用へ切り替え、関連リポジトリ複数値を保存/読込できるようにする。
- [ ] 4. `Home.razor` に登録/編集モーダルを追加し、保存・キャンセル・バリデーションを実装する。
- [ ] 5. `Home.razor` / `app.css` を更新して、カード上に設定ボタンと関連リポジトリ表示を追加する。
- [ ] 6. ビルド確認を実施し、`docs/design/maui-blazor-architecture.md` の実装ステータスを更新する。
- [ ] 7. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Application/Models/` 配下（新規）
- `src/GameLauncherWithGit/Application/Abstractions/IGameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Infrastructure/` 配下（SQLite関連新規）
- `src/GameLauncherWithGit/GameLauncherWithGit.csproj`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-game-editor-ui.md`

## Risks
- SQLite スキーマ変更時の移行処理が未整備だと既存データ互換性に影響する。
- パス入力を自由記述にすると存在しないパスを保存できる（後続で `PathPickerService` 実装が必要）。
- モーダルUIを自前実装するため、キーボード操作/アクセシビリティ対応は追加改善が必要。

## Notes / Logs
- 2026-02-06: ユーザー指示「進めて」に基づき、次スコープとしてゲーム登録/編集UIを開始。
- 2026-02-06: 追加指示「データはSQLite使って」を受け、永続化方式をSQLite前提に計画更新。
- 2026-02-06: `GameEditInput` を追加し、`IGameLibraryService` に `CreateAsync` / `UpdateAsync` を追加。
- 2026-02-06: `IGameLibraryStore` と `SqliteGameLibraryStore` を追加し、SQLite初期化/CRUD（`Games` テーブル）を実装。
- 2026-02-06: `Microsoft.Data.Sqlite` を `GameLauncherWithGit.csproj` に追加、DIに `IGameLibraryStore` を登録。
