# 目的
- PR コメント `r2788635326` の指摘に対応し、`branch.<name>.merge` の解決を `git config --get` ベースへ統一して誤った upstream 選択を防ぐ。

# Design Check
- 判定: 必須（同期・起動前同期ロジックの挙動修正）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - pull の追跡設定解決が「有効値1件（--get）」であることを明記する

# タスクリスト
- [ ] `SyncOrchestrator` の merge 取得を `--get-all` から `--get` に変更
- [ ] `LauncherService` の merge 取得を `--get-all` から `--get` に変更
- [ ] 不要化した複数候補処理（先頭採用ロジック）を削除
- [ ] `docs/design/maui-blazor-architecture.md` を実装へ同期
- [ ] ビルド検証を実施して結果を記録

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Application/Services/LauncherService.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260210-fix-merge-config-get.md`

# リスク
- `--get-all` 前提の挙動に依存した環境では、pull先の解決結果が変化する可能性
- 追跡設定が未設定のリポジトリで、従来同様 pull スキップ条件が正しく維持されるか要確認
