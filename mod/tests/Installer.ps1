# Run with Windows PowerShell, with ClusterTruck closed. Only a fresh temporary copy is modified.
param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path $PSScriptRoot) 'install.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("coop-regression-" + [guid]::NewGuid())
$managed = Join-Path $fixture 'Clustertruck_Data/Managed'
function Run-Installer([switch]$Remove, [switch]$ExpectFailure) {
    $options = @()
    if ($Remove) { $options += '-Uninstall' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -GameDir $fixture @options
    if (($LASTEXITCODE -ne 0) -ne $ExpectFailure.IsPresent) { throw "Unexpected installer exit $LASTEXITCODE" }
}
function Check($test, $message) {
    if (!$test) { throw $message }
    Write-Host "PASS $message"
}
[IO.Directory]::CreateDirectory($managed) | Out-Null
try {
    Copy-Item "$GameDir/Clustertruck_Data/Managed/*.dll" $managed
    $game = Join-Path $managed 'Assembly-CSharp.dll'
    $shared = Join-Path $managed '0Harmony.dll'
    $original = (Get-FileHash $game).Hash
    Set-Content $shared 'before'
    Run-Installer -Remove
    Check ((Get-Content $shared) -eq 'before') 'uninstall without manifest'
    Run-Installer
    Check ((Get-FileHash $game).Hash -ne $original) 'assembly patched'
    Run-Installer
    Run-Installer -Remove
    Check ((Get-FileHash $game).Hash -eq $original) 'original game restored'
    Check ((Get-Content $shared) -eq 'before') 'previous dependency restored'
    Check (!(Test-Path "$managed/human.bin")) 'mod asset removed'
    Run-Installer
    Set-Content $game 'updated game'
    Set-Content $shared 'updated library'
    Run-Installer -ExpectFailure
    Run-Installer -Remove
    Check ((Get-Content $game) -eq 'updated game') 'game update preserved'
    Check ((Get-Content $shared) -eq 'updated library') 'third-party update preserved'
} finally {
    if ((Split-Path ([IO.Path]::GetFullPath($fixture)) -Leaf) -match '^coop-regression-[a-f0-9-]{36}$') {
        [IO.Directory]::Delete($fixture, $true)
    }
}
