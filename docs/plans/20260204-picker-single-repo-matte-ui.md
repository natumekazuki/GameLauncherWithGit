# 実装計画: ピッカー導入・関連リポジトリ単一化・黒マットUI

作成日: 2026-02-04
ステータス: In Progress

## Goal
- ゲーム編集UIでファイル/フォルダ選択をピッカー操作に変更する。
- 「関連リポジトリID(カンマ区切り)」を廃止し、1件の関連リポジトリフォルダ選択へ変更する。
- 画面全体を黒基調のマットデザインへリニューアルする。

## Task List
- [x] 1. 設計ドキュメント更新（実装前）
  - [x] `docs/design/maui-blazor-architecture.md` に「パス選択サービス」と「関連リポジトリ単一化」を追記
  - [x] `.ai_context/system_spec.yaml` のモデル仕様（ゲームと関連リポジトリ表現）を同期

- [ ] 2. パス選択機能をサービス化
  - [ ] `Infrastructure/Abstractions` にピッカー抽象（ファイル/画像/フォルダ）を追加
  - [ ] `Infrastructure/Services` にWindows対応実装を追加（UIからOS APIを直接呼ばない）
  - [ ] `MauiProgram.cs` にDI登録を追加

- [ ] 3. ドメインモデルと保存フローを単一リポジトリ仕様へ変更
  - [ ] `GameDraft` の `RelatedRepositoryIdsCsv` を単一フォルダパスへ置換
  - [ ] `GameItem` の関連リポジトリ表現を単一値へ置換
  - [ ] `GameLibraryService` のバリデーション/保存/クローン処理を更新

- [ ] 4. UIをピッカー連携へ変更
  - [ ] `Pages/Index.razor` の編集モーダルに「参照」ボタンを追加（exe / サムネイル / リポジトリ）
  - [ ] 手入力欄をピッカー主体に変更（必要最小限の直接編集のみ許容）
  - [ ] 詳細モーダル表示も単一リポジトリ仕様に合わせる

- [ ] 5. 黒マットUIへリデザイン
  - [ ] `wwwroot/css/app.css` を黒基調・低彩度・マット質感へ刷新
  - [ ] カード、モーダル、ボタン、ステータスバッジ、入力欄の配色/コントラストを調整
  - [ ] モバイル幅での可読性を維持

- [ ] 6. テスト更新と検証
  - [ ] 既存 `GameLibraryService` テストを新モデル仕様へ更新
  - [ ] `dotnet build` / `dotnet test` を実行し成功確認

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
