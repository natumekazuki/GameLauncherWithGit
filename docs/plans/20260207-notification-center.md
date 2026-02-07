# Plan: 通知センター実装（右下通知の履歴化）

## Goal
- 右下通知ドックのメッセージを履歴として保持し、後から確認できる通知センターを追加する。
- 通知履歴をコピー/クリアできるようにし、トラブル時の情報共有を容易にする。

## Design Doc Check
- 本対応はUI仕様（通知表示と履歴操作）の追加を伴うため、`docs/design/maui-blazor-architecture.md` の更新が必須。

## Task List
- [ ] 通知履歴モデルを定義する（時刻、種別、メッセージ）。
- [ ] `Home.razor` の `SetNotification` を拡張し、通知発火時に履歴へ追加する。
- [ ] 通知センターUIを追加する（設定モーダル内に一覧表示、最新優先）。
- [ ] 通知履歴の操作を追加する（全件コピー、全件クリア）。
- [ ] 履歴件数の上限を設定してメモリ肥大化を防ぐ（例: 200件）。
- [ ] 既存の右下通知ドック表示と競合しないことを確認する。
- [ ] `docs/design/maui-blazor-architecture.md` を更新し、通知センター仕様を同期する。
- [ ] ビルド確認を実施する。

## Affected Files
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-notification-center.md`

## Risks
- 通知履歴の追加で設定モーダルの情報量が増え、視認性が下がる可能性がある。
- 通知履歴の無制限保持はメモリ使用量増加のリスクがある。
- コピー内容のフォーマットが不適切だとログ共有の実用性が下がる可能性がある。

## Notes / Logs
- 2026-02-07: 現在は `_message` の単一表示のみで、履歴保持はしていない。
