# 実行計画: MSIX配布整備とローカル非インストール実行

## Goal
- ローカル開発時は「インストールなし」で実行できる運用を明確化し、MSIX配布時のみインストールされる構成に整理する。
- Azure Key Vault を利用した証明書発行/登録から MSIX 署名・配布までの手順書を作成する。

## Design Check
- 判定: **必要**
- 理由: 配布方式（MSIX/Unpackaged）と証明書運用はアーキテクチャ上の運用仕様に該当するため、`docs/design/` の更新を必須とする。

## Task List
- [x] 1. 現在の Windows ビルド設定を確認し、ローカル実行（Unpackaged）と配布（MSIX）の設定方針を確定する。
- [x] 2. ローカル実行用コマンド（インストールなし）と MSIX 生成用コマンド（配布用）をスクリプト化する。
- [x] 3. `docs/design/maui-blazor-architecture.md` に配布モデル（Unpackaged と MSIX の使い分け）を追記する。
- [x] 4. Azure Key Vault 証明書の発行/登録/利用手順を `docs/design/` に新規作成する。
- [x] 5. 手順の再現性確認として、ローカル実行コマンドとMSIX生成コマンドを実行し、結果を記録する。
- [ ] 6. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/GameLauncherWithGit.csproj`（必要時）
- `scripts/run-local-unpackaged.ps1`（新規）
- `scripts/publish-windows-msix.ps1`（新規）
- `docs/design/maui-blazor-architecture.md`
- `docs/design/windows-msix-keyvault-signing.md`（新規）
- `docs/plans/20260206-msix-localrun-keyvault.md`

## Risks
- Azure Key Vault だけでは「公開配布で即信頼される証明書」を直接満たせないケースがあり、CA連携またはCA発行証明書のインポートが必要になる。
- 開発PCの Visual Studio 実行方法（F5/発行）によって、MSIX インストール挙動が混在しやすい。
- 証明書のエクスポート可否（ポリシー）により、CI/CDの署名方式が制約される。

## Notes / Logs
- 2026-02-06: 追加要件として、MSIX配布前提・ローカル非インストール実行・Azure Key Vault証明書手順書の作成が指示された。
- 2026-02-06: `src/GameLauncherWithGit/GameLauncherWithGit.csproj` の `WindowsPackageType=None` を確認し、開発時は Unpackaged、配布時は publish パラメーターで `MSIX` を指定する方針を確定。
- 2026-02-06: `scripts/run-local-unpackaged.ps1` と `scripts/publish-windows-msix.ps1` を追加。
- 2026-02-06: 検証コマンドを実行。
  - `pwsh -File scripts/run-local-unpackaged.ps1 -BuildOnly` 成功
  - `pwsh -File scripts/publish-windows-msix.ps1` 成功（MSIX生成）
- 2026-02-06: `docs/design/maui-blazor-architecture.md` に配布モデル節を追加。
- 2026-02-06: `docs/design/windows-msix-keyvault-signing.md` を新規作成し、証明書発行から署名・配布手順を記載。
