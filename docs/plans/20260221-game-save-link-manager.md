# 実行計画: ゲーム別セーブシンボリックリンク管理

## Goal
- ゲームごとにセーブデータのリンク設定を管理し、ローカル保存先を OneDrive 配下へ切り替えられるようにする。
- 1ゲームあたり複数のセーブフォルダ（例: `SaveData` と `Profiles`）を同時に管理できるようにする。
- 既存データを壊さない安全なリンク作成フロー（移行/ロールバック/状態検証）を提供する。

## Design Check
- 判定: **必須**
- 理由: 新規機能（リンク管理）であり、データモデル・Windows ファイルシステム操作・UI フローに変更が入るため。
- 対象:
  - `docs/design/game-save-link-manager.md`（新規）
  - `docs/design/maui-blazor-architecture.md`（実装反映時に更新）
- 更新方針:
  - ゲーム設定に「セーブリンク（複数）」を追加する。
  - `GameLibrary` と独立した `SaveLink` 管理サービスを追加する。
  - Windows でのリンク作成方式（シンボリックリンク/ジャンクション）と失敗時のハンドリングを明確化する。

## Task List
- [x] 1. 既存実装の調査（現行の単一関連リポジトリ設計、SQLite スキーマ、UI 編集フロー、Watcher 連携）を実施する。
- [x] 2. セーブリンク管理の設計ドラフトを作成する（`docs/design/game-save-link-manager.md`）。
- [x] 3. ユーザー要件を確定する（リンク方式の優先順位、既存フォルダ移行ポリシー、自動修復タイミング）。
- [ ] 4. ドメイン/アプリケーションモデルを拡張する（`GameSaveLink`、`SaveLinkStatus`、入力モデル）。
- [ ] 5. SQLite 永続化を追加する（`GameSaveLinks` テーブル、マイグレーション、CRUD）。
- [ ] 6. Windows 向けリンク操作サービスを実装する（作成/検証/解除/ロールバック）。
- [ ] 7. `Home.razor` のゲーム編集UIに「セーブリンク複数管理」を追加する（追加/編集/削除/適用）。
- [ ] 8. 起動前チェック/手動適用フローにリンク状態検証を組み込む。
- [ ] 9. 設計ドキュメントと実装の同期を取り、`docs/design/maui-blazor-architecture.md` を更新する。
- [ ] 10. Windows ターゲットでビルドと手動動作確認を行う。

## Affected Files
- `docs/plans/20260221-game-save-link-manager.md`
- `docs/design/game-save-link-manager.md`
- `docs/design/maui-blazor-architecture.md`
- `src/GameLauncherWithGit/Application/Models/*`（セーブリンク関連モデル追加）
- `src/GameLauncherWithGit/Application/Services/*`（ゲーム保存リンク管理サービス追加/連携）
- `src/GameLauncherWithGit/Infrastructure/Abstractions/*`（リンク操作・ストア抽象）
- `src/GameLauncherWithGit/Infrastructure/Services/*`（SQLite ストア・Windows リンク操作実装）
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `src/GameLauncherWithGit/MauiProgram.cs`

## Risks
- OneDrive 同期中ファイルを読了する起動前処理に時間がかかり、起動待ちが長くなる可能性がある。
- 既存セーブフォルダを移行する際、ファイル競合や中断でデータ不整合が起きる可能性がある。
- OneDrive 同期中ファイルを同時操作すると、移行や検証で一時的なロック競合が発生する可能性がある。
- ゲームごと複数リンクを扱うことで UI 複雑度が上がり、誤操作リスクが増える可能性がある。

## Notes / Logs
- 2026-02-21: ユーザー要望「ゲームごとにシンボリックリンク管理」「ローカルセーブを OneDrive へリンク」「セーブフォルダ複数対応」を受領。
- 2026-02-21: 現行実装は `RelatedRepositoryPath` 単一前提であり、セーブリンクの専用モデルは未実装であることを確認。
- 2026-02-21: 要件確定。リンク方式は `ジャンクションのみ`。別端末同時起動は原則しない運用とし、起動前にリンク先配下の全ファイルを読了して OneDrive 実体化を待つ。
- 2026-02-21: 起動前の読了処理で 1 件でも失敗した場合は、ゲーム起動をブロックする方針で確定。
