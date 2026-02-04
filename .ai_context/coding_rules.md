# Coding Rules

## 基本方針
- すべての実装は Windows 11 / .NET 8 / MAUI Blazor Hybrid 前提で行う。
- UI（Razor）から OS 依存 API を直接呼ばない。必ずサービス経由にする。
- 同期処理はキャンセル可能な `async/await` を使用し、UI スレッドをブロックしない。

## DI ルール
- `Singleton` / `Scoped` / `Transient` は `system_spec.yaml` の定義に従う。
- ライフサイクルが不一致になる注入（例: Singleton -> Scoped の直接依存）を禁止する。

## Git 実行ルール
- Git コマンドは必ず作業ディレクトリを明示して実行する。
- 実行結果として `exit code` / `stdout` / `stderr` を必ず記録する。
- `git commit` は差分がある場合のみ実行する。

## 監視・同期ルール
- FileSystemWatcher イベントは直接同期せず、必ずデバウンスキューを経由する。
- リポジトリ単位で同時実行は 1 件に制限する。
- 同期中の追加イベントは「再実行フラグ」で吸収する。

## エラー処理ルール
- `pull --rebase` 競合時は自動同期を停止し、ユーザー通知する。
- ネットワーク断は指数バックオフで再試行し、通知は初回失敗と復旧時のみ行う。
- 例外メッセージのみでなく、コマンド文脈（repo, command, output）をログに含める。

## ドキュメント同期
- 機能追加・仕様変更時は `docs/design/` と `.ai_context/` を同時更新する。
