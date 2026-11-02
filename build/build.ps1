<#
.SYNOPSIS
    Сборка, тесты и упаковка Kando Haptics в .lplug4.

.DESCRIPTION
    1. Проверяет .NET SDK (нужен 10.x — на нём работает Logi Plugin Service 6.x).
    2. Проверяет PluginApi.dll (Logi Plugin Service) и восстанавливает LogiPluginTool
       из манифеста .config/dotnet-tools.json.
    3. Собирает решение (Debug или Release).
    4. Запускает автоматические тесты.
    5. Упаковывает плагин: logiplugintool pack + verify, проверяет содержимое архива.
    6. Кладёт результат в dist/.

.PARAMETER Configuration
    Release (по умолчанию) или Debug.

.PARAMETER SkipTests
    Не запускать тесты.

.PARAMETER Install
    После упаковки установить пакет через "logiplugintool install".

.PARAMETER LinkTo
    Подключить собранный плагин к Logi Plugin Service через .link (механизм разработчика
    из шаблона SDK): копирует пакет в указанный каталог, пишет
    %LOCALAPPDATA%\Logi\LogiPluginService\Plugins\KandoHaptics.link и перезагружает плагин.
    Каталог должен быть без не-ASCII символов (например, C:\ProgramData\KandoHaptics),
    иначе Logi Options+ не прочитает eventMapping.yaml и вибрации не будет.

.PARAMETER Uninstall
    Только удалить установленный плагин KandoHaptics и выйти.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -Configuration Debug -SkipTests
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -Install
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -LinkTo C:\ProgramData\KandoHaptics
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $Install,
    [string] $LinkTo,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PluginName = 'KandoHaptics'
$Solution = Join-Path $RepoRoot 'KandoHaptics.sln'
$PluginProject = Join-Path $RepoRoot 'src\KandoHaptics.Plugin\KandoHaptics.Plugin.csproj'
$PackageInput = Join-Path $RepoRoot "artifacts\plugin\$Configuration"
$DistDir = Join-Path $RepoRoot 'dist'

function Write-Step([string] $Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Fail([string] $Text) { Write-Host "ОШИБКА: $Text" -ForegroundColor Red; exit 1 }

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "$FilePath $($Arguments -join ' ') завершилась с кодом $LASTEXITCODE" }
}

function Invoke-PluginTool([string[]] $Arguments) {
    Push-Location $RepoRoot
    try { Invoke-Checked 'dotnet' (@('tool', 'run', 'logiplugintool') + $Arguments) }
    finally { Pop-Location }
}

# Версия плагина берётся из LoupedeckPackage.yaml — единственного источника.
function Get-PluginVersion {
    $yaml = Get-Content (Join-Path $RepoRoot 'src\KandoHaptics.Plugin\package\metadata\LoupedeckPackage.yaml') -Raw
    if ($yaml -match '(?m)^version:\s*([0-9][0-9A-Za-z.\-]*)\s*$') { return $Matches[1] }
    Fail 'version не найден в LoupedeckPackage.yaml'
}

# --- 1. .NET SDK -------------------------------------------------------------------------
Write-Step 'Проверка .NET SDK'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail '.NET SDK не найден. Установите .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
}
$sdks = @(& dotnet --list-sdks 2>$null)
if (-not ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 })) {
    Fail ".NET SDK 10.x не найден (есть: $($sdks -join '; ')). Logi Plugin Service 6.x запускает плагины на .NET 10."
}
Write-Host "  .NET SDK: $((& dotnet --version).Trim())"

# --- Удаление ----------------------------------------------------------------------------
Push-Location $RepoRoot
try { Invoke-Checked 'dotnet' @('tool', 'restore') } finally { Pop-Location }

if ($Uninstall) {
    Write-Step "Удаление плагина $PluginName"
    Invoke-PluginTool @('uninstall', $PluginName)
    Write-Host "  Пользовательские настройки в %LOCALAPPDATA%\KandoHaptics сохранены."
    exit 0
}

# --- 2. Зависимости ----------------------------------------------------------------------
Write-Step 'Проверка зависимостей'
$apiDir = if ($env:LOGI_PLUGIN_API_DIR) { $env:LOGI_PLUGIN_API_DIR } else { 'C:\Program Files\Logi\LogiPluginService' }
$pluginApi = Join-Path $apiDir 'PluginApi.dll'
if (-not (Test-Path $pluginApi)) {
    Fail "PluginApi.dll не найден в '$apiDir'. Установите Logi Options+ (Logi Plugin Service) или задайте LOGI_PLUGIN_API_DIR."
}
Write-Host "  PluginApi.dll: $pluginApi ($((Get-Item $pluginApi).VersionInfo.FileVersion))"

$serviceDeps = Join-Path $apiDir 'LogiPluginService.deps.json'
if (Test-Path $serviceDeps) {
    $runtime = (Get-Content $serviceDeps -Raw | ConvertFrom-Json).runtimeTarget.name
    Write-Host "  Logi Plugin Service runtime: $runtime"
    if ($runtime -notmatch 'v10\.') { Write-Warning "Плагин собирается под net10.0, а сервис использует $runtime." }
}
Write-Host "  LogiPluginTool: $(& dotnet tool run logiplugintool -- --help 2>$null | Select-Object -First 1)"

# --- 3. Сборка ---------------------------------------------------------------------------
Write-Step "Сборка ($Configuration)"
Invoke-Checked 'dotnet' @('build', $Solution, '-c', $Configuration, '-nologo', '-warnaserror')

# --- 4. Тесты ----------------------------------------------------------------------------
if ($SkipTests) {
    Write-Warning 'Тесты пропущены (-SkipTests).'
} else {
    Write-Step 'Автоматические тесты'
    Invoke-Checked 'dotnet' @('test', $Solution, '-c', $Configuration, '--no-build', '-nologo')
}

# --- 5. Упаковка -------------------------------------------------------------------------
Write-Step 'Упаковка .lplug4'
foreach ($required in @('bin\KandoHapticsPlugin.dll', 'bin\KandoHaptics.Core.dll', 'metadata\LoupedeckPackage.yaml',
                        'metadata\Icon256x256.png', 'events\DefaultEventSource.yaml', 'events\extra\eventMapping.yaml')) {
    if (-not (Test-Path (Join-Path $PackageInput $required))) { Fail "В $PackageInput нет $required" }
}
# PluginApi.dll предоставляет сервис — в пакете его быть не должно.
if (Test-Path (Join-Path $PackageInput 'bin\PluginApi.dll')) { Fail 'PluginApi.dll не должен попадать в пакет.' }

New-Item -ItemType Directory -Force $DistDir | Out-Null
$version = Get-PluginVersion
$suffix = if ($Configuration -eq 'Debug') { '_debug' } else { '' }
$package = Join-Path $DistDir ("{0}_{1}{2}.lplug4" -f $PluginName, ($version -replace '\.', '_'), $suffix)
if (Test-Path $package) { Remove-Item $package -Force }

Invoke-PluginTool @('pack', "$PackageInput\", $package)
Invoke-PluginTool @('verify', $package)
Push-Location $RepoRoot
try {
    # metadata печатает иконку в base64 — скрываем её, чтобы вывод оставался читаемым.
    & dotnet tool run logiplugintool metadata $package | Where-Object { $_ -notmatch '"icon\d+x\d+": "' }
    if ($LASTEXITCODE -ne 0) { Fail 'logiplugintool metadata завершилась с ошибкой' }
} finally { Pop-Location }

Write-Step 'Содержимое пакета'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
    $entries | ForEach-Object { Write-Host "  $_" }
    foreach ($required in @('bin/KandoHapticsPlugin.dll', 'bin/KandoHaptics.Core.dll', 'metadata/LoupedeckPackage.yaml',
                            'events/DefaultEventSource.yaml', 'events/extra/eventMapping.yaml')) {
        if ($entries -notcontains $required) { Fail "В пакете отсутствует $required" }
    }
} finally { $zip.Dispose() }

$hash = (Get-FileHash $package -Algorithm SHA256).Hash
Write-Host "`nГотово: $package"
Write-Host "SHA256: $hash"

# --- Установка ---------------------------------------------------------------------------
if ($Install) {
    Write-Step 'Установка пакета'
    # Тот же обработчик, что и двойной щелчок по .lplug4. "logiplugintool install" из NuGet
    # (6.4.2.3599) не подключается к сервису 6.4.2.3414, поэтому используем установленный.
    $serviceTool = Join-Path $apiDir 'LogiPluginServiceTool.exe'
    if (Test-Path $serviceTool) {
        $process = Start-Process -FilePath $serviceTool -ArgumentList @('install-plugin', "`"$package`"") -PassThru -Wait
        if ($process.ExitCode -ne 0) { Fail "LogiPluginServiceTool install-plugin завершился с кодом $($process.ExitCode)" }
    } else {
        Invoke-PluginTool @('install', $package)
    }
    Write-Host '  Пакет передан Logi Plugin Service. Результат смотрите в журнале плагина.'
    if ($env:LOCALAPPDATA -match '[^\x00-\x7F]') {
        Write-Warning "Путь профиля ($env:LOCALAPPDATA) содержит не-ASCII символы. Если вибрации нет, подключите плагин через -LinkTo C:\ProgramData\KandoHaptics."
    }
}

# --- Подключение через .link ---------------------------------------------------------------
if ($LinkTo) {
    Write-Step "Подключение через .link: $LinkTo"
    $target = [System.IO.Path]::GetFullPath($LinkTo)
    if ($target -match '[^\x00-\x7F]') {
        Fail "Путь '$target' содержит не-ASCII символы: Logi Options+ не прочитает eventMapping.yaml. Выберите каталог вроде C:\ProgramData\KandoHaptics."
    }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $PackageInput '*') $target -Recurse -Force
    Remove-Item (Join-Path $target 'metadata\PackageHash.bin') -ErrorAction SilentlyContinue
    $pluginsDir = Join-Path $env:LOCALAPPDATA 'Logi\LogiPluginService\Plugins'
    New-Item -ItemType Directory -Force $pluginsDir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $pluginsDir "$PluginName.link"), ($target.TrimEnd('\') + '\'), (New-Object System.Text.UTF8Encoding $false))
    Start-Process "loupedeck:plugin/$PluginName/reload"
    Write-Host "  Готово. Если вибрации нет после первого подключения — перезапустите Logi Options+."
}