# 再開ロードマップ（2026-02-06時点）

## 1. このドキュメントの目的
- 開発を一時中断しても、次回すぐ再開できるように「残タスク」「優先順」「次に作る候補」をまとめる。

## 2. 現在地（実装済みの要点）
- ゲーム登録/編集（SQLite永続化、単一リポジトリ紐づけ、サムネイル生成保存）
- 起動前同期（`fetch -> remote ahead判定 -> 条件付きcommit -> pull --rebase --autostash`）
- 監視同期（10秒デバウンス、`fetch/pull/add/commit/push`、`ErrorPaused` 遷移）
- Git未導入時の起動ブロック表示
- 右下通知ドック、ダークUI、サイドバー廃止
- 設定導線（自動起動トグル、最新エラーログ/ログフォルダを開く）
- `settings.json` 永続化（デバウンス秒、再試行初期/上限秒、通知抑制秒）
- Windows連携の実装開始
  - トレイ状態表示（待機/同期中/エラー停止）
  - Windows通知（重複抑制 + フォールバック）

## 3. 残タスク（優先順）
### P0: 運用上の必須
- （完了）バックオフ再試行、通知抑制、閉じる時の常駐継続、トレイメニューは実装済み

### P1: 使い勝手向上
- ログの構造化（repo / command / stdout / stderr / exit code を追いやすく）
- ログ閲覧UI（最低限のフィルタとコピー導線）

### P2: 拡張候補
- リポジトリごとの詳細設定（除外、デバウンス秒数、一時停止）
- 同期履歴タイムライン（最新成功/失敗、所要時間）
- 競合時のガイド強化（再開手順をUI化）

## 4. すぐ再開するための実施順（推奨）
1. `P1` の「ログ閲覧UI」を先に実装
2. ログ構造化の不足フィールド（repo / command / stdout / stderr / exit code）を補強
3. `P2` の同期履歴タイムラインへ拡張

## 5. 再開時の確認コマンド
```powershell
dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false
pwsh -File scripts/run-local-unpackaged.ps1
```

## 6. 再開時の手動チェック観点
- ファイル変更後、10秒デバウンスで同期が1回にまとまる
- 競合発生時に `ErrorPaused` へ遷移し、通知/ログが残る
- 再開ボタンで同期再開できる
- 起動前同期で remote ahead 時に不要コミットしない
- 自動起動トグルがWindows再ログイン後も反映される

## 7. 次の計画ファイル候補（着手時に作成）
- `docs/plans/YYYYMMDD-log-viewer-ui.md`
- `docs/plans/YYYYMMDD-log-structure-enhancement.md`
- `docs/plans/YYYYMMDD-sync-history-timeline.md`
