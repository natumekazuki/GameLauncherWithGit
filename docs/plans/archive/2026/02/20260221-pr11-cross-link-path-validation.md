# 実行計画: PR#11 複数セーブリンク相互参照バリデーション

## Goal
- 複数セーブリンク間で `LocalPath` / `TargetPath` が相互参照・重複・親子関係になる設定を保存不可にする。

## Design Check
- 判定: 必須（入力制約の追加）
- 対象:
  - `docs/design/game-save-link-manager.md`
- 更新方針:
  - 複数リンク間でも `LocalPath` / `TargetPath` の一致および親子関係を禁止するルールを追記する。

## Task List
- [x] 1. `SaveLinkService.NormalizeInputs` に、他リンクの `Local/Target` との一致・親子関係を検出する検証を追加する。
- [x] 2. 設計書へ複数リンク間パス制約を追記する。
- [x] 3. Windows ターゲットでビルド検証する。

## Affected Files
- `docs/plans/archive/2026/02/20260221-pr11-cross-link-path-validation.md`
- `src/GameLauncherWithGit/Application/Services/SaveLinkService.cs`
- `docs/design/game-save-link-manager.md`

## Risks
- 制約追加により、従来保存できていた一部構成が新規保存時に拒否される可能性がある。

## Notes / Logs
- 2026-02-21: 指摘内容を確認。リンク間 `Local/Target` の交差（例: `A->B`, `B->A`）を保存時に拒否する方針で対応開始。
- 2026-02-21: `NormalizeInputs` に既存リンク端点コレクションを導入し、他リンク `Local/Target` との同一・親子関係を検出して保存を拒否するよう修正。
- 2026-02-21: `docs/design/game-save-link-manager.md` に複数リンク間パス制約を追記。
- 2026-02-21: `dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
