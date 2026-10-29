# svnop をビルドしてインストーラと zip を作る
#   出力: dist\app (実行ファイル一式), dist\svnop-setup-<version>.exe, dist\svnop-<version>-win-x64.zip
#   必要: .NET 10 SDK, Inno Setup 6 以降 (iscc)
#   -Version を省略すると svnop\Directory.Build.props の値を使う (CI ではタグから渡す)
param(
    [string]$Version = '',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content "$root\svnop\Directory.Build.props")).Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "invalid version: $Version" }
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'

Write-Host "svnop $Version"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }

foreach ($proj in "$root\svnop\svnop.csproj", "$root\svnop\agentw\svnopw.csproj") {
    dotnet publish $proj -c $Configuration -o $app "-p:Version=$Version" -p:DebugType=none -nologo -v q
    if ($LASTEXITCODE) { throw "build failed: $proj" }
}
Copy-Item "$root\msys2\svn-op-pinentry.py", "$root\README.md", "$root\README.en.md", "$root\LICENSE" $app

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup (iscc.exe) not found' }

& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$app" "$PSScriptRoot\svnop.iss"
if ($LASTEXITCODE) { throw 'installer build failed' }

Compress-Archive -Path "$app\*" -DestinationPath (Join-Path $dist "svnop-$Version-win-x64.zip")
Get-ChildItem $dist -File
