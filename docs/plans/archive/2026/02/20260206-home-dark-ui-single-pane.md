# 実行計画: ホーム画面の単一ペイン化とダークUI化

## Goal
- サイドバーを廃止し、1画面前提のシンプルなレイアウトに変更する。
- ホーム画面をゲームランチャー向けの暗め配色へ変更する。
- 文言「ゲーム一覧」「ゲーム起動時に関連リポジトリの同期を実行します。」をUIから削除する。
- エラー/通知メッセージを画面右下の固定領域に表示する。

## Design Check
- 判定: **必要**
- 理由: レイアウト構成（サイドバー廃止）と通知表示方式（固定領域化）が変わるため、`docs/design/maui-blazor-architecture.md` を更新する。

## Task List
- [x] 1. `MainLayout` を単一ペイン構成へ変更し、サイドバーを非表示化する。
- [x] 2. `Home.razor` から指定文言を削除し、通知表示を右下固定領域へ移す。
- [x] 3. `Home.razor` の保存/参照エラーを固定通知領域へ集約する。
- [x] 4. `app.css` をダークテーマへ調整し、カード/モーダル/通知の配色を更新する。
- [x] 5. `docs/design/maui-blazor-architecture.md` を更新し、ビルドで検証する。
- [x] 6. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Components/Layout/MainLayout.razor`
- `src/GameLauncherWithGit/Components/Layout/MainLayout.razor.css`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/archive/2026/02/20260206-home-dark-ui-single-pane.md`

## Risks
- ダークテーマでコントラスト不足があると可読性が低下する可能性がある。
- 通知を右下固定にすると、モバイル幅で操作ボタンと重なる可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー要望（サイドバー削除、ダーク配色、指定文言削除、右下固定通知）に基づき計画開始。
- 2026-02-06: `MainLayout` を単一ペインへ変更し、`NavMenu` 非表示構成に更新。
- 2026-02-06: `Home.razor` から「ゲーム一覧」「ゲーム起動時に関連リポジトリの同期を実行します。」を削除。
- 2026-02-06: `Home.razor` の操作結果/エラー表示を右下固定通知ドックへ集約し、保存/参照エラーも同領域へ表示するよう変更。
- 2026-02-06: `app.css` にダークテーマ上書きスタイルを追加（カード、モーダル、ボタン、通知ドック）。
- 2026-02-06: `docs/design/maui-blazor-architecture.md` に単一ペインUIと通知ドックの構成を追記。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` で 0 エラーを確認。
