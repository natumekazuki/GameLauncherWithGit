# Plan: アプリアイコン/トレイアイコンのブランド化

## Goal
- デフォルトアイコンを廃止し、ゲームランチャーらしい独自アイコンへ変更する。
- アプリ本体アイコン（MAUI AppIcon）とトレイアイコン（待機/同期中/エラー停止）を同一デザイン言語で統一する。

## Design Doc Check
- 本対応はUI資産とWindows固有表示（トレイ）の仕様追加を含むため、`docs/design/maui-blazor-architecture.md` を更新する（必須）。

## アイコン方針（今回の提案）
- ベース: ダーク背景 + シアン/ライム系アクセントでランチャー感を出す。
- モチーフ: シンプルなゲームパッド形状（視認性重視、16px相当でも判読可能な線形）。
- トレイ状態差分:
  - 待機: 標準カラー
  - 同期中: 青系アクセント
  - エラー停止: 赤系アクセント

## Task List
- [ ] `Resources/AppIcon` の `appicon.svg` / `appiconfg.svg` を独自デザインへ更新する。
- [ ] Windowsトレイ用の `.ico` を作成する（待機/同期中/エラー停止の3種）。
- [ ] トレイアイコン資産をプロジェクトへ組み込み、出力ディレクトリへ配置されるよう `csproj` を更新する。
- [ ] `TrayService` でシステム既定アイコン (`LoadIcon`) をやめ、作成した `.ico` を状態別に読み込む実装へ変更する。
- [ ] 既存のトレイ挙動（メニュー、状態遷移、終了動作）に回帰がないことを確認する。
- [ ] `docs/design/maui-blazor-architecture.md` にアイコン資産方針とトレイ状態別アイコン仕様を追記する。
- [ ] ビルドしてアイコン資産の解決エラーがないことを確認する。

## Affected Files
- `src/GameLauncherWithGit/Resources/AppIcon/appicon.svg`
- `src/GameLauncherWithGit/Resources/AppIcon/appiconfg.svg`
- `src/GameLauncherWithGit/Resources/TrayIcon/*.ico`（新規）
- `src/GameLauncherWithGit/GameLauncherWithGit.csproj`
- `src/GameLauncherWithGit/Infrastructure/Services/TrayService.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-app-tray-icon-branding.md`

## Risks
- トレイ用 `.ico` の読み込み失敗時にアイコンが表示されない可能性があるため、フォールバックが必要。
- 透過/解像度不整合があるとトレイで視認性が落ちる可能性がある。
- AppIcon変更はOSキャッシュの影響で即時反映されない場合がある。

## Notes / Logs
- 2026-02-07: 現状は `MauiIcon` がテンプレート設定、`TrayService` は `LoadIcon(IDI_*)` に依存している。
