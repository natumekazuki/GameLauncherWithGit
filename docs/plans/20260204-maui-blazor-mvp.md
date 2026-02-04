# 実装計画: MAUI Blazor 化を含むMVP再計画

作成日: 2026-02-04
ステータス: In Progress

## Goal
- 既存要件を満たす常駐同期アプリを、**Windows 11 向け .NET MAUI Blazor Hybrid** 前提でMVP実装できる状態まで設計・実装する。

## Task List
- [x] 1. 要件・設計ドキュメントをMAUI Blazor前提に更新する
  - [x] `docs/要件定義.md` の技術制約・UI実装方針を明文化
  - [x] `docs/design/maui-blazor-architecture.md` を新規作成（構成図、責務分離、Windows固有機能ブリッジ方針）
  - [x] `.ai_context/` にDIライフサイクルと主要インターフェースを追加

- [ ] 2. ソリューション基盤を作成する（MAUI Blazor）
  - [ ] MAUI Blazorプロジェクトを作成し、Windowsターゲットで起動確認
  - [ ] 設定/ログ保存ディレクトリ（`%AppData%`）の基盤実装
  - [ ] Git実行サービス、監視サービス、通知サービスのDI登録

- [ ] 3. 同期エンジン（MVP）を実装する
  - [ ] FileSystemWatcher + リポジトリ単位デバウンス（既定10秒、設定可変）
  - [ ] 同期フロー `fetch -> pull --rebase -> add -A -> commit(差分時のみ) -> push`
  - [ ] 失敗分類（競合/認証/ネットワーク/権限）と指数バックオフ再試行

- [ ] 4. ランチャーUI（Blazor）を実装する
  - [ ] ゲーム一覧カードUI（先頭「+新規追加」カード、常時「起動」ボタン）
  - [ ] 詳細モーダル（起動/今すぐ同期/編集/削除/ログ）
  - [ ] サムネイル生成（長辺512px、PNG、アスペクト比維持、失敗時は未設定扱い）

- [ ] 5. Windows統合機能を実装する
  - [ ] タスクトレイ常駐（待機/同期中/エラー停止の状態表示）
  - [ ] Windows Toast通知（初回失敗・復旧通知、競合停止通知）
  - [ ] Windows起動時の自動起動設定

- [ ] 6. テストと受け入れ確認を実施する
  - [ ] AC-01〜AC-04 と AC-02a を満たす結合テスト
  - [ ] 競合時停止、オフライン復旧、連続更新時デバウンスの確認
  - [ ] ログ導線と障害解析情報（stdout/stderr/command）を確認

- [ ] 7. 仕上げ（ドキュメント同期・計画クローズ）
  - [ ] `docs/design/` と `.ai_context/` を実装に同期
  - [ ] 計画チェックリストを完了状態へ更新
  - [ ] 完了後に `docs/plans/archive/2026/02/` へ移動

## Affected Files
- 既存更新
  - `docs/要件定義.md`
- 新規作成（予定）
  - `docs/design/maui-blazor-architecture.md`
  - `docs/plans/20260204-maui-blazor-mvp.md`
  - `.ai_context/system_spec.yaml`（未存在なら新規）
  - `.ai_context/coding_rules.md`（必要差分のみ）
- 実装追加（予定）
  - `src/` 配下のMAUI Blazorアプリ一式

## Risks
- MAUI Blazor と Windowsタスクトレイ連携は追加ブリッジ実装が必要で、初期工数が増える
- FileSystemWatcher のイベント欠落/重複により、同期漏れまたは過剰同期のリスクがある
- バイナリ中心のためリポジトリ肥大化が進みやすく、運用ガイド整備が必要
- `pull --rebase` 競合時のユーザー誘導不足で復旧不能になる可能性がある

## Design Check
- 判定: **Design Doc 必須**
- 理由: 新規機能（ゲームランチャー）と実装基盤変更（MAUI Blazor Hybrid）を伴うため。

## Notes / Logs
- 2026-02-04: `docs/要件定義.md` に MAUI Blazor Hybrid 前提を追加。
- 2026-02-04: `docs/design/maui-blazor-architecture.md` を追加し、同期フロー/起動フロー/DI方針を定義。
- 2026-02-04: `.ai_context/system_spec.yaml` と `.ai_context/coding_rules.md` を新規追加。
