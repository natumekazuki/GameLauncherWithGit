# 実行計画: Issue #16 リモートリポジトリ選択不可

## Goal
- Issue #16「リモートリポジトリ選択不可」を再現・原因特定し、期待仕様に沿って「再読込」で選択候補が表示される状態へ修正する。

## Design Check
- 判定: 必須（ゲーム編集モーダルのGit操作フロー変更の可能性があるため）
- 対応:
  - `docs/design/maui-blazor-architecture.md` のブランチ操作仕様（ローカル限定/リモート含む）を実装と一致させる。

## Task List
- [x] 1. 仕様確定: Issue文の「リモートリポジトリ」が「リモートブランチ」を指すのかをユーザー確認する。
- [x] 2. ベース確定: `#16` ブランチへ `main` の PR #15（GitBranchChanger）を取り込む方針を確定する。
- [x] 3. 修正実装: 再読込時の取得元を要件に合わせて修正する（例: `refs/remotes` を含める場合は命名整形・重複除去を実装）。
- [ ] 4. UI/状態遷移確認: 再読込・切り替え・作成後の選択状態と通知文言の整合性を確認する。
- [x] 5. ドキュメント同期: `docs/design/maui-blazor-architecture.md` を更新する。
- [x] 6. 検証: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0` を実行してビルド確認する。
- [x] 7. レビュー修正: リモート名に `/` を含む構成でのリモート追跡ブランチ名解決を修正する。
- [x] 8. レビュー修正: `origin/*` 形式のローカルブランチがある場合の候補重複を除外する。
- [x] 9. レビュー修正: 妥当なブランチ名（`feature/HEAD`, `foo->bar`）を誤除外しないようリモート候補フィルタを修正する。

## Affected Files
- `docs/plans/20260226-issue16-remote-repository-selection.md`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`

## Risks
- `#16` ブランチは現時点で `main` より遅れており、Issue対象機能（PR #15）が未反映のため、先にベース同期しないと誤修正になる可能性がある。
- リモート参照（`refs/remotes`）を表示対象に含める場合、`origin/HEAD -> origin/main` などのシンボリック参照除外が必要。
- リモート追跡ブランチを選択可能にすると、`checkout` 時のdetached HEADやローカル未作成ブランチへの遷移設計が必要になる。

## Notes / Logs
- 2026-02-26: `gh issue view 16` で本文を確認。「リポジトリ切り替え機能で再取得ボタンを押下してもリモートリポジトリが表示されない」。
- 2026-02-26: 現在の `#16` ブランチにはブランチ切替UI自体が未反映。`main` 側の追加コミット（PR #15: `c4e536c` ほか）に機能実装が存在することを確認。
- 2026-02-26: `main` の `LoadEditorBranchesAsync` は `refs/heads`（ローカルブランチ）取得のみで、Issue記述の「リモート」表示要件とは不一致の可能性を確認。
- 2026-02-26: ユーザー確認により、Issue記述の「リモートリポジトリ」は「リモートブランチ（`origin/*`）」を指すことを確定。
- 2026-02-26: `#16` ブランチへ `main` を Fast-forward マージして、PR #15 のブランチ切替基盤を取り込み。
- 2026-02-26: `Home.razor` を更新し、ブランチ再読込時に `fetch --all --prune` + `refs/remotes` 読み込みを追加。`*/HEAD` 除外、remote-only 表示、remote選択時の `checkout -b --track` を実装。
- 2026-02-26: `docs/design/maui-blazor-architecture.md` を更新し、ローカル+リモート追跡ブランチ仕様へ同期。
- 2026-02-26: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0` を実行し、0 warning / 0 error で成功。
- 2026-02-26: レビュー指摘（P2）に対応。`foo/bar` のように `/` を含むリモート名で `foo/bar/main` から `main` を正しく解決できるよう、`git remote` 結果を使った最長一致解決へ変更。
- 2026-02-26: レビュー指摘（P3）に対応。表示済みローカル候補（`branchNames`）と同名のリモート候補を除外し、`origin/main` などの値衝突で切替経路が誤る問題を修正。
- 2026-02-26: レビュー指摘（P2）に追加対応。`/HEAD`・`->` の文字列判定による誤除外を廃止し、`<remote>/HEAD` のシンボリック参照だけを除外する方式へ変更。
