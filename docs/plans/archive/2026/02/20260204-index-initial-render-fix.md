# 実装計画: Index初期描画エラー修正とRazor分離

作成日: 2026-02-04
ステータス: Completed

## Goal
- Index画面の初期描画で発生する未処理例外を解消する。
- `Pages/Index.razor` のロジックを `Pages/Index.razor.cs` へ分離する。
- 例外時に `ILogger` で原因追跡できる状態にする。

## Task List
- [x] 1. 初期描画エラー要因の緩和
  - [x] 起動時DI解決で失敗しうる依存を遅延解決へ変更
  - [x] 初期ロード処理に例外ガードを追加

- [x] 2. Razorコード分離
  - [x] `Index.razor` から `@code` を除去して表示専用にする
  - [x] `Index.razor.cs` にロジック/DI/イベントハンドラを移設

- [x] 3. ログ強化
  - [x] 初期化失敗・ピッカー解決失敗を `ILogger` 出力
  - [x] ユーザー通知文言をログ確認導線に統一

- [x] 4. 検証
  - [x] `dotnet build` 成功
  - [x] `dotnet test` 成功

## Affected Files
- 更新予定
  - `src/GameLauncherWithGit.App/Pages/Index.razor`
- 追加予定
  - `src/GameLauncherWithGit.App/Pages/Index.razor.cs`

## Risks
- Razor/Code-behind 分離時にイベントハンドラやバインディング名の不一致が起きる可能性がある。
- ピッカーサービス遅延解決により、操作時エラーの扱いが変わる可能性がある。

## Design Check
- 判定: **Design Doc 不要**
- 理由: 機能仕様追加ではなく、実装構造の整理と不具合修正が中心のため。

## Notes / Logs
- 2026-02-04: ユーザー報告「Indexの初期描画でエラー」「Razorとrazor.csを分離したい」。
- 2026-02-04: `IPathPickerService` の注入を遅延解決へ変更し、初期描画時のDI失敗が画面全体へ波及しないように修正。
- 2026-02-04: `Index.razor` と `Index.razor.cs` を分離し、UIとロジックを整理。
- 2026-02-04: `dotnet build` / `dotnet test` の成功を確認。
