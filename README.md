# GameLauncherWithGit

## Windows での UI 確認

Windows、.NET 9 の MAUI 開発環境、Git 2.38.0 以降が必要です。初回はサブモジュールも取得します。

```powershell
git submodule update --init --recursive
dotnet build src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0
dotnet run --project src/GameLauncherWithGit/GameLauncherWithGit.csproj -f net9.0-windows10.0.19041.0 --no-build
```

起動時には、その Windows ユーザーの登録済みリポジトリを監視・同期します。確認には既存データのない別の Windows ユーザープロファイルや仮想環境を使い、実際のセーブデータやリモートを登録しないでください。

「新規追加」でタイトルだけを入力して保存すると、リポジトリや実行ファイルを使わず一覧の表示を確認できます。環境設定、ゲーム編集、通知などを開き、必要な状態へスクロールしたうえで Windows の画面キャプチャ機能を使って撮影できます。共有前にパスや個人情報が画像に含まれていないことを確認してください。
