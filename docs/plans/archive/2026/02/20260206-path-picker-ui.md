# 実行計画: パス選択UIの実装（実行ファイル/関連リポジトリ）

## Goal
- 登録/編集モーダルで、実行ファイルパスと関連リポジトリパスを「参照」操作で入力できるようにする。
- `PathPickerService` を実装し、Windows で実ファイル/フォルダを選択できるようにする。

## Design Check
- 判定: **必要**
- 理由: UI操作フロー（入力方法）の追加とWindows固有機能（Picker）実装に該当するため、`docs/design/maui-blazor-architecture.md` の実装ステータス更新を行う。

## Task List
- [x] 1. `IPathPickerService` を拡張し、関連リポジトリフォルダ選択メソッドを追加する。
- [x] 2. `PathPickerService` を実装し、実行ファイル選択・サムネイル選択・リポジトリフォルダ選択をWindowsで動作させる。
- [x] 3. `Home.razor` の登録/編集モーダルに「実行ファイル参照」「リポジトリ追加参照」ボタンを追加し、入力欄へ反映する。
- [x] 4. `app.css` を更新し、モーダル内の参照操作UIスタイルを調整する。
- [x] 5. ビルド確認を実施し、`docs/design/maui-blazor-architecture.md` の実装ステータスを更新する。
- [x] 6. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Infrastructure/Abstractions/IPathPickerService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/PathPickerService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-path-picker-ui.md`

## Risks
- Unpackaged 実行時のPicker初期化（Windowハンドル取得）に失敗すると、参照操作が機能しない。
- フォルダ選択APIはWindows依存のため、将来マルチOS展開時に分岐実装が必要。
- ユーザーキャンセル時の状態管理を誤ると入力欄が不正更新される可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー指示「進めて」に基づき、PathPicker UI 連携を次スコープとして開始。
- 2026-02-06: `IPathPickerService` に `PickRepositoryDirectoryPathAsync` を追加。
- 2026-02-06: `PathPickerService` を実装し、ファイル選択（exe/thumbnail）とWindowsフォルダ選択を追加。
- 2026-02-06: `Home.razor` のモーダルに `参照` / `フォルダ追加` ボタンを接続。
- 2026-02-06: `app.css` にモーダル内インライン入力・Pickerボタン用スタイルを追加。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` で 0 エラーを確認。
- 2026-02-06: `docs/design/maui-blazor-architecture.md` にパス選択UIの実装反映を追記。
