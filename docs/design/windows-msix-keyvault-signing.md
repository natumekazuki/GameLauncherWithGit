# Windows 配布運用: MSIX と Azure Key Vault 証明書

更新日: 2026-02-08
対象: `src/GameLauncherWithGit`（.NET MAUI Blazor Hybrid / Windows 11）

## 1. 目的
- ローカル開発時は「インストールなし（Unpackaged）」で実行する。
- 配布時のみ「MSIX」を生成し、Azure Key Vault 管理の証明書で署名する。

## 2. ローカルでインストールされる理由
- Windows アプリがインストールされるのは、`MSIX` としてビルド/発行し、そのパッケージを実行した場合。
- `WindowsPackageType=None` の `dotnet run` / `dotnet build` は Unpackaged 実行であり、通常はインストールされない。
- Visual Studio の発行プロファイルや `dotnet publish` で `WindowsPackageType=MSIX` を使うと、配布用パッケージ経路になる。

## 3. このリポジトリの運用ルール
- ローカル実行（非インストール）:
  - `pwsh -File scripts/run-local-unpackaged.ps1`
- ローカル確認用ビルド（GUI起動なし）:
  - `pwsh -File scripts/run-local-unpackaged.ps1 -BuildOnly`
- MSIX 発行（配布用）:
  - `pwsh -File scripts/publish-windows-msix.ps1`

## 4. Azure Key Vault 証明書の発行から登録まで

### 4.1 前提
- Azure CLI ログイン済み
- Key Vault 作成権限がある
- 配布先PCに証明書を配布できる運用がある

### 4.2 Key Vault と証明書を作成
```powershell
# 1) 変数
$ResourceGroup = "rg-gamelauncher-prod"
$Location = "japaneast"
$VaultName = "kv-gamelauncher-prod"
$CertName = "msix-code-signing"

# 2) リソース作成
az group create --name $ResourceGroup --location $Location
az keyvault create --name $VaultName --resource-group $ResourceGroup --location $Location
```

`certificate-policy.json`（例: Key Vault で自己署名を発行、PFX エクスポート可能）
```json
{
  "issuerParameters": {
    "name": "Self"
  },
  "x509CertificateProperties": {
    "subject": "CN=MonochromeMemory",
    "validityInMonths": 12,
    "ekus": [
      "1.3.6.1.5.5.7.3.3"
    ],
    "keyUsage": [
      "digitalSignature"
    ]
  },
  "keyProperties": {
    "exportable": true,
    "keyType": "RSA",
    "keySize": 2048,
    "reuseKey": false
  },
  "secretProperties": {
    "contentType": "application/x-pkcs12"
  }
}
```

証明書発行:
```powershell
az keyvault certificate create `
  --vault-name $VaultName `
  --name $CertName `
  --policy "@certificate-policy.json"
```

### 4.3 証明書をダウンロード（PFX / CER）
```powershell
# 公開証明書（CER）
az keyvault certificate download `
  --vault-name $VaultName `
  --name $CertName `
  --file ".\codesign.cer"

# PFX（秘密鍵付き、secret から取得）
az keyvault secret download `
  --vault-name $VaultName `
  --name $CertName `
  --file ".\codesign.pfx" `
  --encoding base64
```

### 4.4 配布/ビルド用マシンへ証明書登録
```powershell
# PFX を個人ストアへ登録（署名用）
Import-PfxCertificate `
  -FilePath ".\codesign.pfx" `
  -CertStoreLocation "Cert:\CurrentUser\My"

# 自己署名の場合は信頼ストアにも登録（配布先PC）
Import-Certificate `
  -FilePath ".\codesign.cer" `
  -CertStoreLocation "Cert:\CurrentUser\TrustedPeople"

Import-Certificate `
  -FilePath ".\codesign.cer" `
  -CertStoreLocation "Cert:\CurrentUser\Root"
```

### 4.5 MSIX を署名付きで発行
```powershell
pwsh -File scripts/publish-windows-msix.ps1 `
  -PackageCertificateKeyFile ".\codesign.pfx"
```

補足:
- Key Vault からエクスポートした PFX はパスワード空のケースがある。必要な場合のみ `MSIX_CERT_PASSWORD` または `-PackageCertificatePassword` を指定する。
- `PackageCertificateThumbprint` を使う場合は、事前に証明書ストアへ登録しておく。
- `Platforms/Windows/Package.appxmanifest` の `Identity Publisher` は証明書 Subject と一致させる。
- APPX0107 が出る場合の主因は「Code Signing EKU不足」または「証明書 Subject と Publisher 不一致」。

事前確認（PFX）:
```powershell
$cert = Get-PfxCertificate -FilePath ".\codesign.pfx"
$cert | Format-List Subject,HasPrivateKey,EnhancedKeyUsageList
```

### 4.6 署名とインストール確認
```powershell
$msix = Get-ChildItem ".\artifacts\msix\AppPackages" -Recurse -Filter *.msix | Select-Object -First 1
Get-AuthenticodeSignature $msix.FullName | Format-List

# インストール検証
Add-AppxPackage $msix.FullName
```

### 4.7 0x800B0109（ルート証明書未信頼）対処
配布先PCで `0x800B0109` が出る場合は、インストール対象のMSIXから署名証明書を直接抽出し、`Thumbprint` 一致で信頼登録を確認する。

```powershell
# インストール対象MSIX（絶対パス推奨）
$msix = "C:\path\to\GameLauncherWithGit_1.0.0.1_x64.msix"

# 1) MSIX署名証明書を取得
$sig = Get-AuthenticodeSignature $msix
$thumb = $sig.SignerCertificate.Thumbprint
$sig | Format-List Status,StatusMessage

# 2) MSIXから証明書をエクスポート
$cer = "$env:TEMP\gamelauncher-signer.cer"
Export-Certificate -Cert $sig.SignerCertificate -FilePath $cer -Force | Out-Null

# 3) 管理者PowerShellで LocalMachine へ登録
Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

# 4) Thumbprint一致確認（両方に存在すること）
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $thumb | Select-Object Subject,Thumbprint
Get-ChildItem Cert:\LocalMachine\Root         | Where-Object Thumbprint -eq $thumb | Select-Object Subject,Thumbprint

# 5) 再インストール
Add-AppxPackage -Path $msix -ForceApplicationShutdown
```

## 5. 運用上の注意
- .NET 9 の MAUI Windows MSIX 発行では `-r` 指定を避け、`RuntimeIdentifierOverride=win10-x64`（または `win10-arm64` / `win10-x86`）を使用する。
- 本番配布で警告を抑えるには、公開信頼チェーンを持つコード署名証明書（CA発行）を使う。
- Key Vault で自己署名を使う場合、配布先PCへの信頼証明書配布が必須。
- 証明書ローテーション時は、失効/更新手順と `Publisher` の整合を維持する。
- CI/CD で利用する場合は、Key Vault アクセス権を最小化（Managed Identity / RBAC 最小権限）。

## 6. 参考（公式ドキュメント）
- Azure Key Vault 証明書作成（Azure CLI）: https://learn.microsoft.com/cli/azure/keyvault/certificate
- Azure Key Vault から secret ダウンロード（PFX 取得）: https://learn.microsoft.com/cli/azure/keyvault/secret
- Import-Certificate: https://learn.microsoft.com/powershell/module/pki/import-certificate
- SignTool（MSIX 署名/検証）: https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool
