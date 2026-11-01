#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs (or updates) the Topaz AZS agent as a Windows service.

.DESCRIPTION
  - copies the agent next to this script into -InstallDir;
  - finds the Topaz database if -DatabasePath is not given: the path from a previous install,
    then "Program Files (x86)\TopazAZS-*\TopazAZS.fdb", otherwise asks for it;
  - defaults: server https://topaz.08u.ru, Firebird password of a standard Topaz install
    (asked again if Firebird rejects it); on update the previous server/DB settings are kept;
  - writes appsettings.json (station name, server, Topaz DB path) - no secrets in it;
  - stores secrets (Firebird password, enrollment key) as environment variables of the
    service only (registry: HKLM\SYSTEM\CurrentControlSet\Services\TopazAgent\Environment);
  - restricts the state folder (%ProgramData%\TopazAgent) to SYSTEM and Administrators;
  - registers the service: automatic (delayed) start, restart on failure;
  - runs "TopazAgent.exe check" and starts the service.
  The agent opens no listening ports; it only makes outbound HTTPS requests.

.EXAMPLE
  .\install.ps1 -StationName "АЗС-5 Северная" -EnrollmentKey <key from server .env>

.EXAMPLE
  .\install.ps1 -StationName "АЗС-5 Северная" -EnrollmentKey <key> `
      -ServerUrl https://other.example.com -DatabasePath "D:\Topaz\TopazAZS.fdb" -FirebirdPassword <password>
#>
param(
    [Parameter(Mandatory)] [string] $StationName,
    [string] $ServerUrl,
    [string] $EnrollmentKey,
    [string] $DatabasePath,
    [string] $FirebirdHost = "127.0.0.1",
    [int] $FirebirdPort = 3050,
    [string] $FirebirdUser = "SYSDBA",
    [string] $FirebirdPassword,
    [string] $TimeZone,
    [string] $InstallDir = "$env:ProgramFiles\TopazAgent",
    [string] $ServiceName = "TopazAgent"
)

$ErrorActionPreference = "Stop"
$DefaultServerUrl = "https://topaz.08u.ru"
# Password of a standard Topaz install (the same on all our stations). Kept only in the service
# environment in the registry, never in appsettings.json or logs.
$DefaultFirebirdPassword = "electro"
$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source "TopazAgent.exe"))) { throw "TopazAgent.exe not found next to install.ps1" }

function Get-PreviousSetting {
    <# Reads Section.Key from a previous appsettings.json; $null if absent. #>
    param([string] $SettingsPath, [string] $Section, [string] $Key)
    if (-not $SettingsPath -or -not (Test-Path $SettingsPath -PathType Leaf)) { return $null }
    $value = (Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json).$Section.$Key
    if ($value) { return [string] $value } else { return $null }
}

function Resolve-ServerUrl {
    <# Explicit value > previous install > default; adds https:// when the scheme is missing. #>
    param([string] $Given, [string] $PreviousSettings, [string] $Default)
    $url = $Given
    if (-not $url) { $url = Get-PreviousSetting $PreviousSettings "Server" "Url" }
    if (-not $url -or $url -like "*topaz.example.com*") { $url = $Default }
    $url = $url.Trim().TrimEnd("/")
    if ($url -notmatch "^[a-zA-Z][a-zA-Z0-9+.-]*://") { $url = "https://$url" }
    return $url
}

function Test-FirebirdLoginFailure {
    <# True when "TopazAgent.exe check" output says Firebird rejected the user/password. #>
    param([string[]] $CheckOutput)
    return [bool] ($CheckOutput | Where-Object { $_ -like "*FAIL*Topaz DB*" -and ($_ -match "user name and password are not defined|login|password") })
}

function Resolve-DatabasePath {
    <# Picks the Topaz DB path: explicit value > previous install > standard folders > ask the operator. #>
    param(
        [string] $Given,
        [string] $PreviousSettings,
        [string[]] $SearchRoots,
        [bool] $LocalFirebird = $true
    )
    if ($Given) { return $Given }

    $prev = Get-PreviousSetting $PreviousSettings "Firebird" "Database"
    if ($prev) { Write-Host "Topaz database (from the previous install): $prev"; return $prev }

    if ($LocalFirebird) {
        $found = @($SearchRoots | Where-Object { $_ } | Select-Object -Unique |
            ForEach-Object { Get-ChildItem -Path (Join-Path $_ "TopazAZS-*\TopazAZS.fdb") -File -ErrorAction SilentlyContinue } |
            Sort-Object FullName -Unique | Sort-Object LastWriteTime -Descending)
        if ($found.Count -eq 1) {
            Write-Host "Topaz database found: $($found[0].FullName)"
            return $found[0].FullName
        }
        if ($found.Count -gt 1) {
            Write-Host "Several Topaz databases found:"
            for ($i = 0; $i -lt $found.Count; $i++) {
                $f = $found[$i]
                Write-Host ("  {0}. {1}  ({2:N0} MB, changed {3:yyyy-MM-dd HH:mm})" -f ($i + 1), $f.FullName, ($f.Length / 1MB), $f.LastWriteTime)
            }
            while ($true) {
                $answer = Read-Host "Номер базы Топаза [1]"
                if (-not $answer) { $answer = "1" }
                $n = 0
                if ([int]::TryParse($answer, [ref] $n) -and $n -ge 1 -and $n -le $found.Count) { return $found[$n - 1].FullName }
                Write-Host "Enter a number from 1 to $($found.Count)."
            }
        }
        Write-Host "Topaz database not found in: $(($SearchRoots | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'TopazAZS-*' }) -join ', ')"
    }

    while ($true) {
        $path = (Read-Host "Путь к базе Топаза (например, C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb)").Trim().Trim('"').Trim("'")
        if (-not $path) { throw "Topaz database path is required (-DatabasePath)." }
        if (-not $LocalFirebird -or (Test-Path $path -PathType Leaf)) { return $path }
        Write-Host "File not found: $path"
    }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$previousSettings = $(if ($existing) { Join-Path $InstallDir "appsettings.json" })
$ServerUrl = Resolve-ServerUrl -Given $ServerUrl -PreviousSettings $previousSettings -Default $DefaultServerUrl
Write-Host "Server: $ServerUrl"
$localFirebird = $FirebirdHost -in @("127.0.0.1", "localhost", "::1", ".", $env:COMPUTERNAME)
$DatabasePath = Resolve-DatabasePath -Given $DatabasePath `
    -PreviousSettings $previousSettings `
    -SearchRoots @(${env:ProgramFiles(x86)}, $env:ProgramFiles) -LocalFirebird $localFirebird
Write-Host "Using Topaz database: $DatabasePath"

# 1. Stop the running service (update scenario). The local queue is kept in ProgramData.
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping $ServiceName..."
    Stop-Service $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
}

# 2. Copy binaries; keep an existing datasets.json if it was customized.
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem $source -File | Where-Object { $_.Name -notin @("install.ps1", "uninstall.ps1") } | ForEach-Object {
    $target = Join-Path $InstallDir $_.Name
    if ($_.Name -eq "datasets.json" -and (Test-Path $target)) {
        Copy-Item $_.FullName (Join-Path $InstallDir "datasets.json.new") -Force
        Write-Host "datasets.json kept; new version saved as datasets.json.new"
    } elseif ($_.Name -ne "appsettings.json") {
        Copy-Item $_.FullName $target -Force
    }
}

# 3. appsettings.json (no secrets).
$settingsPath = Join-Path $InstallDir "appsettings.json"
$settings = Get-Content (Join-Path $source "appsettings.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$settings.Server.Url = $ServerUrl
$settings.Station.Name = $StationName
if ($TimeZone) { $settings.Station.TimeZone = $TimeZone }
$settings.Firebird.DataSource = $FirebirdHost
$settings.Firebird.Port = $FirebirdPort
$settings.Firebird.Database = $DatabasePath
$settings.Firebird.User = $FirebirdUser
$json = $settings | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($settingsPath, $json, (New-Object Text.UTF8Encoding $false))

# 4. State folder: SYSTEM + Administrators only (holds the queue and the DPAPI-protected token).
$stateDir = Join-Path $env:ProgramData "TopazAgent"
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
icacls $stateDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null

# 5. Service.
$exe = Join-Path $InstallDir "TopazAgent.exe"
if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Topaz AZS agent" `
        -Description "Reads Topaz AZS database (read-only) and sends data to the central service over HTTPS." `
        -StartupType Automatic | Out-Null
}
sc.exe config $ServiceName start= delayed-auto | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

# 6. Secrets as service-only environment variables (not visible to other processes/users).
$envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$vars = @{}
$current = (Get-ItemProperty -Path $envKey -Name Environment -ErrorAction SilentlyContinue).Environment
if ($current) { foreach ($line in $current) { $k, $v = $line -split "=", 2; $vars[$k] = $v } }
$pwKey = "TOPAZ_AGENT_Firebird__Password"
if ($FirebirdPassword) { $vars[$pwKey] = $FirebirdPassword }
elseif (-not $vars[$pwKey]) { $vars[$pwKey] = $DefaultFirebirdPassword; Write-Host "Firebird password: standard Topaz password" }
if ($EnrollmentKey) { $vars["TOPAZ_AGENT_Server__EnrollmentKey"] = $EnrollmentKey }
function Save-ServiceEnvironment {
    Set-ItemProperty -Path $envKey -Name Environment -Type MultiString -Value ($vars.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
}
Save-ServiceEnvironment

# 7. Check (with the same secrets) and start. If Firebird rejects the password, ask for it (3 tries).
$checkOk = $false
for ($attempt = 1; $attempt -le 4; $attempt++) {
    Write-Host "`nChecking configuration..."
    foreach ($kv in $vars.GetEnumerator()) { [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, "Process") }
    $checkOutput = @(& $exe check)
    $checkOk = $LASTEXITCODE -eq 0
    foreach ($kv in $vars.GetEnumerator()) { [Environment]::SetEnvironmentVariable($kv.Key, $null, "Process") }
    $checkOutput | ForEach-Object { Write-Host $_ }
    if ($checkOk -or $attempt -eq 4 -or -not (Test-FirebirdLoginFailure $checkOutput)) { break }
    $sec = Read-Host "Firebird не принял пароль. Пароль Firebird для $FirebirdUser" -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    if (-not $plain) { break }
    $vars[$pwKey] = $plain
    Save-ServiceEnvironment
}

Start-Service $ServiceName
Write-Host "`nService $ServiceName started. Logs: $stateDir\logs"
if (-not $checkOk) { Write-Warning "Check reported problems (see above). The service retries automatically; fix the cause and watch the logs." }
