# 実装計画: ピッカー導入・関連リポジトリ単一化・黒マットUI

作成日: 2026-02-04
ステータス: Completed

## Goal
- ゲーム編集UIでファイル/フォルダ選択をピッカー操作に変更する。
- 「関連リポジトリID(カンマ区切り)」を廃止し、1件の関連リポジトリフォルダ選択へ変更する。
- 画面全体を黒基調のマットデザインへリニューアルする。

## Task List
- [x] 1. 設計ドキュメント更新（実装前）
  - [x] `docs/design/maui-blazor-architecture.md` に「パス選択サービス」と「関連リポジトリ単一化」を追記
  - [x] `.ai_context/system_spec.yaml` のモデル仕様（ゲームと関連リポジトリ表現）を同期

- [x] 2. パス選択機能をサービス化
  - [x] `Infrastructure/Abstractions` にピッカー抽象（ファイル/画像/フォルダ）を追加
  - [x] `Infrastructure/Services` にWindows対応実装を追加（UIからOS APIを直接呼ばない）
  - [x] `MauiProgram.cs` にDI登録を追加

- [x] 3. ドメインモデルと保存フローを単一リポジトリ仕様へ変更
  - [x] `GameDraft` の `RelatedRepositoryIdsCsv` を単一フォルダパスへ置換
  - [x] `GameItem` の関連リポジトリ表現を単一値へ置換
  - [x] `GameLibraryService` のバリデーション/保存/クローン処理を更新

- [x] 4. UIをピッカー連携へ変更
  - [x] `Pages/Index.razor` の編集モーダルに「参照」ボタンを追加（exe / サムネイル / リポジトリ）
  - [x] 手入力欄をピッカー主体に変更（必要最小限の直接編集のみ許容）
  - [x] 詳細モーダル表示も単一リポジトリ仕様に合わせる

- [x] 5. 黒マットUIへリデザイン
  - [x] `wwwroot/css/app.css` を黒基調・低彩度・マット質感へ刷新
  - [x] カード、モーダル、ボタン、ステータスバッジ、入力欄の配色/コントラストを調整
  - [x] モバイル幅での可読性を維持

- [x] 6. テスト更新と検証
  - [x] 既存 `GameLibraryService` テストを新モデル仕様へ更新
  - [x] `dotnet build` / `dotnet test` を実行し成功確認

## Affected Files
- 更新予定
  - `src/GameLauncherWithGit.App/Pages/Index.razor`
  - `src/GameLauncherWithGit.App/wwwroot/css/app.css`
  - `src/GameLauncherWithGit.App/Application/Models/GameDraft.cs`
  - `src/GameLauncherWithGit.App/Application/Models/GameItem.cs`
  - `src/GameLauncherWithGit.App/Application/Services/GameLibraryService.cs`
  - `src/GameLauncherWithGit.App/MauiProgram.cs`
  - `tests/GameLauncherWithGit.App.Tests/UnitTest1.cs`
  - `docs/design/maui-blazor-architecture.md`
  - `.ai_context/system_spec.yaml`
- 追加予定
  - `src/GameLauncherWithGit.App/Infrastructure/Abstractions/IPathPickerService.cs`
  - `src/GameLauncherWithGit.App/Infrastructure/Services/PathPickerService.cs`

## Risks
- Windowsフォルダピッカーはウィンドウハンドル初期化に依存し、実装を誤ると例外が発生する。
- 既存モデル変更により、将来の同期機能（複数リポジトリ前提）へ再調整が必要になる可能性がある。
- 黒基調テーマでコントラスト不足が起こると可読性が低下する。

## Design Check
- 判定: **Design Doc 必須**
- 理由: 画面操作仕様とモデル仕様（関連リポジトリ表現）の変更を伴うため。

## Notes / Logs
- 2026-02-04: ユーザー要求を受領（ピッカー導入 / 関連リポジトリ単一化 / 黒マットUI）。
- 2026-02-04: 関連リポジトリフォルダは「任意」で確定。
- 2026-02-04: 実装前に `docs/design/` と `.ai_context/` を更新。
- 2026-02-04: `IPathPickerService` / `PathPickerService` を追加し、exe/画像/フォルダのOSピッカー連携を実装。
- 2026-02-04: `GameDraft` / `GameItem` を単一 `RelatedRepositoryPath`（任意）へ変更。
- 2026-02-04: `Index.razor` 編集モーダルをピッカー操作中心へ変更し、詳細モーダルに関連リポジトリ表示を追加。
- 2026-02-04: `wwwroot/css/app.css` を黒基調マットテーマへ全面更新。
- 2026-02-04: `GameLibraryService` テストを更新し、`dotnet build` / `dotnet test` 成功を確認。
