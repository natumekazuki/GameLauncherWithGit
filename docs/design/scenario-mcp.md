# シナリオ MCP

## 認証と二権限

Windows アプリ内で公式 C# MCP SDK 2.4.0 の Streamable HTTP を使用する。`http://127.0.0.1:<Port>/mcp` の各 HTTP 要求に `Authorization: Bearer <APIキー>` が必要で、欠落・不正キーは 401。Host は loopback と実際の待受 port、Origin があれば同 port の loopback HTTP に限定する。CORS、外部待受、旧 SSE transport は提供しない。

StructureKey はフロー構造・接続・出現位置・タグ・範囲・分類・画像一覧 metadata のみを返す。本文・抜粋・話者・画像本体・thumbnail は値のマスクではなく JSON property 自体を省く。FullKey は本文・話者と画像本体も取得できる。権限は毎要求のキーだけから決まり、tool 引数、URI、対象 ID、MCP session ID によって昇格しない。stateless のため権限を保持する session や再送 stream は作らない。構造キーによる本文・話者の検索は、一致有無から内容を推測できるため拒否する。

これは API 経由の認可であり、同一ユーザーのファイル・shell・SSH 到達を隔離する仕組みではない。設定ファイルを読める者はキーを取得できる。鍵の配送やクライアント側の秘密保管はこのアプリの機能ではない。

## 共通 snapshot と参照

MCP は共通 `GetScenarioProjectionAsync` の型別 allowlist DTO を使用する。同一 snapshot/selection の対象 ID、Version、SelectionVersion は権限や export の形式によって採番し直さない。ID は取得した資料版と組にして使用し、訂正・削除後に古い参照を別本文へ付け替えない。

外部の作品・取得回・分類・範囲等の64bit整数IDは入出力とも十進文字列とし、JSON number にしない。返却された `source` と `version` をコピーして次の要求へ使用できる。受信連番・UTF16位置/長さ・revision counter は数値を保つ。入力境界で正の整数文字列を InvariantCulture で解決し、内部DB/storeのlong型は維持する。例えば `"9007199254740993"` のようなIDも丸めない。

SelectionVersion は順序付き `occurrenceIds` と `rangeIds` の結果scopeであり、世代counterや検索条件そのものではない。異なる条件でも両値列が同じなら同scope。全Versionと合わせて値列比較する。内部完全snapshotを外部DTOへ埋め込まず、画像metadataと画像本体を分離する。区間・分類等で選択に穴が開いた場合は共通readerがread-time cutを入れ、保存済みのcutを変えず架空の経路接続を防ぐ。

`scenario_games` は捕捉作品 ID 一覧だけを返す。`scenario_snapshot` は選択された資料、`scenario_export_json` は同じ JSON の出力、`scenario_by_id` は collection と ID、`scenario_by_source` は元出現参照を受け取る。直接参照も同じ認可・selection 境界を通し、版不一致や選択外の対象はエラーとする。本文断片による対象指定は使用しない。JSON export はサーバー側ファイルを書かない。ZIP はローカル UI の共通二種 writer を使用し、MCP 用の別 ZIP writer は持たない。

resource template `scenario://material/{gameId}/{revision}/{matchingRevision}` は全体 selection の資料、`scenario://item/{gameId}/{revision}/{matchingRevision}/{collection}/{id}` はその中の対象を返す。filtered selection は tools の selection 引数を使用する。

## 画像境界

画像本体は Full のみ。作品・selection・資料版・selection版・出現 ID・画像 ID の組で取得し、実 store の画像 gate 内で現在の版、選択内出現、その出現への画像関連を再確認する。別作品・範囲外・無関係画像・旧版は返さない。ファイル path は公開せず、thumbnail 専用や raw ファイル取得の迂回口は設けない。

## 設定と寿命

アプリのデータ directory に `scenario-mcp.json` がない場合は無効。`Enabled`、`Port`、`StructureKey`、`FullKey` を読み、二つの異なる 256-bit hexadecimal キーが揃う時だけ起動する。設定が不正または port を使用できない場合は MCP を起動せず、アプリ本体の起動は維持する。設定内容・HTTP header・秘密を console、log、エラー本文へ出力しない。

`scripts/configure-scenario-mcp.ps1 -DataDirectory <絶対path> -Action Enable` で二つの暗号学的乱数キーを作成する。既存設定への上書きは拒否する。アプリを終了して `-Action Rotate` を使うと両キーを交換し、`-Action Disable` で無効化する。port を変える場合は Rotate の `-Port`、または停止中の設定編集を使用する。いずれも次のアプリ起動から有効。script はキーを表示しない。通常データ directory はアプリの実行形態で異なるため、使用中の directory を指定する。テストは `GAMELAUNCHER_DATA_DIRECTORY` による隔離先だけを使う。

クライアントは Streamable HTTP と Bearer header の手動設定に対応するものを使う。接続 URL は `/mcp`、header は `Authorization: Bearer <選んだキー>`。キーを prompt、コマンド履歴、repository、共有設定、問い合わせ記録へ貼り付けない。OAuth discovery/認可サーバーは提供しないため、OAuth 専用クライアントには対応しない。全資料を読む必要のないクライアントには StructureKey のみを設定する。

アプリ起動から終了までの listener とし、トレイ終了では資料 store の終了前に要求受付を止める。window Destroying でも終了処理する。独立常駐 service、OS/network 設定変更は不要。

Windows build/publish は .NET と ASP.NET Core runtime を self-contained で同梱する。配布先への追加 ASP.NET runtime 導入は不要になるが、容量増と runtime 更新時の再配布が伴う。[Windows 配布運用](windows-msix-keyvault-signing.md#5-運用上の注意)に従う。

## 一次資料

- [公式 C# SDK transport](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md)
- [公式 C# SDK resources](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/resources/resources.md)
- [.NET の配布形式](https://learn.microsoft.com/dotnet/core/deploying/)
