#Requires -Version 5.1
# Notlar'ı derler, %LOCALAPPDATA%\Programs\Notlar'a kurar, Claude Code'a MCP olarak kaydeder ve başlatır.
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Host "Lütfen kur.ps1'i yönetici OLMAYAN bir PowerShell'de çalıştır."
    exit 1
}
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\Notlar'
$appDir = Join-Path $target 'app'
$mcpDir = Join-Path $target 'mcp'
$mcpExe = Join-Path $mcpDir 'NotlarMcp.exe'

Write-Host '> Çalışan Notlar süreçleri kapatılıyor...'
Get-Process NotlarApp, NotlarMcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Wait-Process -Name NotlarApp, NotlarMcp -Timeout 5 -ErrorAction SilentlyContinue

Write-Host '> Eski kurulum temizleniyor...'
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
if (Test-Path $mcpDir) { Remove-Item $mcpDir -Recurse -Force }

Write-Host '> Derleniyor...'
dotnet publish (Join-Path $root 'src\Notlar.App\Notlar.App.csproj') -c Release -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'NotlarApp derlenemedi.' }
dotnet publish (Join-Path $root 'src\Notlar.Mcp\Notlar.Mcp.csproj') -c Release -o $mcpDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'NotlarMcp derlenemedi.' }

Write-Host '> Claude Code MCP kaydı...'
cmd /c "claude mcp remove notlar --scope user >nul 2>&1"
claude mcp add notlar --scope user -- $mcpExe
if ($LASTEXITCODE -ne 0) { throw 'MCP kaydı başarısız.' }
# Başka bir istemci (ör. ChatGPT, Gemini) aynı sunucuyu kendi adıyla yazsın isterse,
# --yazar argümanıyla ayrı bir MCP kaydı eklenebilir, ör.:
#   claude mcp add notlar-chatgpt --scope user -- $mcpExe --yazar ChatGPT

Write-Host '> CLAUDE.md yönergesi...'
$claudeDir = Join-Path $env:USERPROFILE '.claude'
New-Item -ItemType Directory -Force -Path $claudeDir | Out-Null
$claudeMd = Join-Path $claudeDir 'CLAUDE.md'
$line = '- Kullanıcı "not al", "not et", "kaydet" dediğinde notu `notlar` MCP sunucusunun `not_ekle` aracıyla ekle.'
if (-not (Test-Path $claudeMd) -or -not (Select-String -Path $claudeMd -SimpleMatch 'not_ekle' -Quiet)) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::AppendAllText($claudeMd, [Environment]::NewLine + $line + [Environment]::NewLine, $utf8)
}

Write-Host '> Başlatılıyor...'
Start-Process (Join-Path $appDir 'NotlarApp.exe')
Write-Host 'Tamam. Tray ikonuna tıkla; Claude Code oturumunu yeniden başlatınca "not al: ..." diyebilirsin.'
