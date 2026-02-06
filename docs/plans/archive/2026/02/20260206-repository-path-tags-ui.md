# 実行計画: 関連リポジトリのタグ一覧UI（並び替え/削除）

## Goal
- ゲーム登録/編集モーダルで、関連リポジトリをテキストエリアではなく「一覧（タグ）」で管理できるようにする。
- 一覧上で各リポジトリの削除と順序変更（上へ/下へ）を可能にする。

## Design Check
- 判定: **必要**
- 理由: ランチャー設定UIの操作モデル変更（入力方式と編集操作）に該当するため、`docs/design/maui-blazor-architecture.md` の実装ステータス更新を行う。

## Task List
- [x] 1. `Home.razor` の関連リポジトリ入力モデルを「行文字列」から「リスト操作」に置き換える。
- [x] 2. モーダル内に関連リポジトリ一覧（タグ/行）を表示し、削除ボタンを実装する。
- [x] 3. 一覧項目の順序変更（上へ/下へ）操作を実装する。
- [x] 4. 既存の「フォルダ追加」操作と統合し、重複チェックを維持する。
- [x] 5. `app.css` を更新して、一覧UIと操作ボタンのスタイルを追加する。
- [x] 6. ビルド確認を実施し、`docs/design/maui-blazor-architecture.md` の実装ステータスを更新する。
- [x] 7. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-repository-path-tags-ui.md`

## Risks
- 並び替え操作と保存処理の同期を誤ると、表示順と保存順がずれる可能性がある。
- ボタン操作が増えるため、モーダルの操作性が下がる可能性がある。
- 今後ドラッグ&ドロップへ拡張する場合、現在の実装から再設計が必要になる可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー指示「進めて」に基づき、関連リポジトリの一覧編集UIを次スコープとして開始。
- 2026-02-06: `Home.razor` の関連リポジトリ編集をリストモデルへ置き換え。
- 2026-02-06: 一覧内の削除/上へ/下へ操作を実装。
- 2026-02-06: `フォルダ追加` での重複除外を維持したままリストへ追加。
- 2026-02-06: `app.css` に一覧UI（アイテム行、操作ボタン）スタイルを追加。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` で 0 エラーを確認。
