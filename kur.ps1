# PC Terminali: derle ve bu kullanıcı için kur (yönetici izni gerekmez).
# Kullanım:  powershell -ExecutionPolicy Bypass -File .\kur.ps1
$ErrorActionPreference = 'Stop'
$proje = $PSScriptRoot
$hedef = Join-Path $env:LOCALAPPDATA 'Programs\PCTerminali'
$eskiHedef = Join-Path $env:LOCALAPPDATA 'Programs\TemizlikPaneli'
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host 'Derleniyor...'
$yayin = Join-Path $proje 'yayin'
if (Test-Path $yayin) { Remove-Item $yayin -Recurse -Force }
& $dotnet publish (Join-Path $proje 'PCTerminali.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $yayin -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız.' }

Get-Process PCTerminali, TemizlikPaneli -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$hedef*" -or $_.Path -like "$eskiHedef*" } | Stop-Process -Force
New-Item -ItemType Directory -Force $hedef | Out-Null
Copy-Item (Join-Path $yayin '*') $hedef -Recurse -Force
$exe = Join-Path $hedef 'PCTerminali.exe'

# Eski adla yapılmış kurulumu ve kısayolları kaldır
if (Test-Path $eskiHedef) { Remove-Item $eskiHedef -Recurse -Force }
$shell = New-Object -ComObject WScript.Shell
foreach ($klasor in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $eski = Join-Path $klasor 'PC Temizlik Paneli.lnk'
    if (Test-Path $eski) { Remove-Item $eski -Force }
    $lnk = $shell.CreateShortcut((Join-Path $klasor 'PC Terminali.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $hedef
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Disk analizi, uygulama/oyun kaldırma, temizlik ve medya indirme'
    $lnk.Save()
}
Write-Host "Kuruldu: $exe"
