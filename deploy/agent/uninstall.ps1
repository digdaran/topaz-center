#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the Topaz AZS agent service. Use -RemoveState to also delete the local queue and token
  (unsent data will be re-read from Topaz after a reinstall; the server must reset the station first).
#>
param(
    [string] $InstallDir = "$env:ProgramFiles\TopazAgent",
    [string] $ServiceName = "TopazAgent",
    [switch] $RemoveState
)
$ErrorActionPreference = "Stop"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName removed"
}
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
if ($RemoveState) {
    Remove-Item (Join-Path $env:ProgramData "TopazAgent") -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Local state removed"
}
