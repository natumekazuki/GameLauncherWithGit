# 実行計画: セーブリンク解除時のデータ復元フロー実装

作成日: 2026-02-23
対象: `src/GameLauncherWithGit`

## Goal
- セーブリンク設定を削除した際に、実ファイルシステム上のジャンクションも安全に解除する。
- 解除時のデータ流れを明確化し、`TargetPath` の実データを `LocalPath` の通常フォルダへ復元する。
- 失敗時に中途半端な状態（ローカル消失・リンク切断）を残さない。

## Design Doc Check
- [x] `docs/design/game-save-link-manager.md` にリンク解除仕様（データ移動・失敗時挙動）を追記する。
  - 想定フロー: `TargetPath` -> 一時復元フォルダへコピー -> ジャンクション削除 -> `LocalPath` へリネーム
  - 失敗時: 解除途中で失敗したらジャンクション再作成を試みる（ベストエフォート）

## Task List
- [x] 1. `ILocalSaveLinkOperator` / `LocalSaveLinkOperator` にリンク解除 API を追加する。
- [x] 2. `LocalSaveLinkOperator` に「同期先データのローカル復元付き解除」処理を実装する。
- [x] 3. `ISaveLinkService` / `SaveLinkService` に「編集前後差分から解除対象を特定して解除実行」処理を追加する。
- [x] 4. `GameLibraryService.UpdateWithSaveLinksAsync` で、保存前に解除を実行するフローへ変更する。
- [x] 5. UI 文言を最小限更新し、削除保存でリンク解除が走ることを明示する。
- [x] 6. ビルドで回帰確認し、計画ファイルを更新する。

## Affected Files
- `docs/design/game-save-link-manager.md`
- `docs/plans/20260223-save-link-unlink-flow.md`
- `src/GameLauncherWithGit/Infrastructure/Abstractions/ILocalSaveLinkOperator.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LocalSaveLinkOperator.cs`
- `src/GameLauncherWithGit/Application/Abstractions/ISaveLinkService.cs`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- （必要時）`src/GameLauncherWithGit/Infrastructure/Models/*.cs`

## Risks
- 解除時に対象フォルダが大容量だと処理時間が長くなり、UI 体感が悪化する。
- 解除直前/直後に外部プロセスがローカルパスを掴むと失敗しやすい。
- DB 保存失敗時にファイルシステム側だけ解除済みになる可能性がある（エラーメッセージで明示し、手動復旧可能性を残す）。

## Notes / Logs
- 2026-02-23: 既存実装ではリンク定義削除時にジャンクション解除処理が存在しないことを確認。
- 2026-02-23: 設計書 `docs/design/game-save-link-manager.md` の未確定事項に「解除機能をMVPに含めるか」が残っていることを確認。
- 2026-02-23: `ILocalSaveLinkOperator.RemoveJunctionWithRestoreAsync` と `JunctionRemoveResult` を追加し、解除時の「target -> 一時復元 -> local 通常化」処理を実装。
- 2026-02-23: `SaveLinkService.UnlinkRemovedLinksAsync` を追加し、編集保存時の旧定義との差分から解除対象を抽出して実行するよう変更。
- 2026-02-23: `GameLibraryService.UpdateWithSaveLinksAsync` でリンク保存前に解除処理を実行する順序へ変更。
- 2026-02-23: `Home.razor` のセーブリンク編集欄に「削除保存時にリンク解除が走る」注記を追加。
- 2026-02-23: `dotnet build GameLauncherWithGit.sln` は既存の別要因（`PathPickerService` の未解決シンボル、実行中プロセスのDLLロック）で失敗。今回変更のコンパイル確認は `dotnet msbuild ... /t:CoreCompile /p:TargetFramework=net9.0-windows10.0.19041.0` で実施し成功。
