# ClusterTruck Coop - instalador / desinstalador
#   install.ps1 [-GameDir <pasta do jogo>] [-Uninstall]
# Guarda os originais e hashes em Managed/.clustertruck-coop.
param([string]$GameDir, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$libs = '0Harmony.dll', 'MonoMod.RuntimeDetour.dll', 'MonoMod.Utils.dll', 'Mono.Cecil.dll', 'Mono.Cecil.Rocks.dll', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Pdb.dll'
$ours = @('ClusterTruckCoop.dll', 'human.bin') + $libs  # human.* = modelo dos outros jogadores (Quaternius, CC0)

function Find-ModFile($name) {
  foreach ($d in $PSScriptRoot, "$PSScriptRoot\lib", "$PSScriptRoot\assets", "$PSScriptRoot\ClusterTruckCoop\bin\Release\net35") {
    if (Test-Path "$d\$name") { return "$d\$name" }
  }
  throw "Arquivo do mod nao encontrado: $name"
}

if (-not $GameDir) {
  $candidates = "$PSScriptRoot\..", "$PSScriptRoot\..\ClusterTruck.v1.1", "$PSScriptRoot", "${env:ProgramFiles(x86)}\Steam\steamapps\common\Clustertruck"
  $GameDir = $candidates | Where-Object { Test-Path "$_\Clustertruck.exe" } | Select-Object -First 1
  if (-not $GameDir) { $GameDir = Read-Host 'Pasta do ClusterTruck (onde fica Clustertruck.exe)' }
}
$GameDir = (Resolve-Path $GameDir).Path
$managed = Join-Path $GameDir 'Clustertruck_Data\Managed'
$target = Join-Path $managed 'Assembly-CSharp.dll'
$backup = "$target.coop-backup"
if (-not (Test-Path $target)) { throw "Nao achei $target" }
if (Get-Process Clustertruck -ErrorAction SilentlyContinue) { throw 'Feche o ClusterTruck antes de instalar/desinstalar.' }

$state = Join-Path $managed '.clustertruck-coop'
$manifest = Join-Path $state 'files.json'
$names = @('Assembly-CSharp.dll') + $ours
function Hash($file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
function Patch-Assembly($original, $outputPath) {
  Add-Type -Path (Find-ModFile 'Mono.Cecil.dll')
  $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
  $resolver.AddSearchDirectory($managed)
  $rp = New-Object Mono.Cecil.ReaderParameters
  $rp.AssemblyResolver = $resolver
  $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($original, $rp)
  $coop = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Find-ModFile 'ClusterTruckCoop.dll'), $rp)
  $init = $coop.MainModule.GetType('ClusterTruckCoop.Loader').Methods | Where-Object Name -eq 'Init'
  $awake = $asm.MainModule.GetType('Manager').Methods | Where-Object Name -eq 'Awake'
  if (-not $init -or -not $awake) { throw 'Metodo Manager.Awake ou Loader.Init nao encontrado (versao do jogo diferente?)' }
  foreach ($op in $awake.Body.Instructions) {
    if ($op.OpCode.Code -eq 'Call' -and $op.Operand.FullName -eq $init.FullName) {
      throw 'A DLL ja contem o mod. Verifique os arquivos do jogo antes de instalar.'
    }
  }
  $il = $awake.Body.GetILProcessor()
  $il.InsertBefore($awake.Body.Instructions[0], $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $asm.MainModule.ImportReference($init)))
  $asm.Write($outputPath)
  $asm.Dispose(); $coop.Dispose()
}
# Rename a fresh copy into place so linked files in other installations stay intact.
function Put-File($source, $dest) {
  $tmp = "$dest.$([guid]::NewGuid()).tmp"
  try {
    [IO.File]::Copy($source, $tmp)
    if ([IO.File]::Exists($dest)) { [IO.File]::Replace($tmp, $dest, "$tmp.old"); [IO.File]::Delete("$tmp.old") }
    else { [IO.File]::Move($tmp, $dest) }
  } finally { [IO.File]::Delete($tmp) }
}

function Restore-Install {
  $rows = (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json)
  foreach ($row in $rows) {
    if ($names -notcontains $row.name) { throw 'Manifesto invalido.' }
    if ($row.before -and (Hash (Join-Path $state $row.name)) -ne $row.before) { throw "Backup invalido: $($row.name)" }
  }
  foreach ($row in $rows) {
    $dest = Join-Path $managed $row.name
    if (!(Test-Path -LiteralPath $dest) -or (Hash $dest) -ne $row.after) { continue }
    if ($row.before) { Put-File (Join-Path $state $row.name) $dest }
    else { [IO.File]::Delete($dest) }
  }
  # Detach the journal only after all originals are restored.
  $finished = "$state.restored-$([guid]::NewGuid())"
  [IO.Directory]::Move($state, $finished)
  foreach ($row in $rows) {
    if ($row.before) { [IO.File]::Delete((Join-Path $finished $row.name)) }
  }
  foreach ($file in 'files.json', 'patched.tmp') { [IO.File]::Delete((Join-Path $finished $file)) }
  if (![IO.Directory]::GetFileSystemEntries($finished).Length) { [IO.Directory]::Delete($finished) }
}

$lock = [IO.FileStream]::new("$target.coop-lock", 'OpenOrCreate', 'ReadWrite', 'None', 4096, 'DeleteOnClose')
try {
  if (Test-Path $manifest) {
    $assembly = (Get-Content -Raw $manifest | ConvertFrom-Json) | Where-Object name -eq 'Assembly-CSharp.dll'
    if (!$Uninstall -and $assembly -and (Hash $target) -ne $assembly.after) {
      throw 'O jogo mudou. Execute -Uninstall para preservar a nova DLL, depois instale novamente.'
    }
    Restore-Install
  }
  if (Test-Path $backup) {
    # Legacy installs only tracked the game assembly. Unknown shared files stay in place.
    $probe = "$target.$([guid]::NewGuid()).tmp"
    try {
      Patch-Assembly $backup $probe
      if ((Hash $target) -eq (Hash $probe)) { Put-File $backup $target }
      elseif ((Hash $target) -ne (Hash $backup)) {
        throw 'Backup antigo sem correspondencia. Preserve o backup, mova-o para fora de Managed e verifique os arquivos do jogo antes de reinstalar.'
      }
      [IO.File]::Delete($backup)
    } finally { [IO.File]::Delete($probe) }
  }
  if ($Uninstall) { Write-Host 'Coop removido.'; return }

  $stage = Join-Path $managed ('.coop-stage-' + [guid]::NewGuid())
  [IO.Directory]::CreateDirectory($stage) | Out-Null
  try {
    $patched = Join-Path $stage 'patched.tmp'
    Patch-Assembly $target $patched
    $rows = @(foreach ($name in $names) {
      $dest = Join-Path $managed $name
      $source = if ($dest -eq $target) { $patched } else { Find-ModFile $name }
      $after = Hash $source
      [string]$before = if (Test-Path $dest) { Hash $dest }
      if ($before -eq $after) { continue }
      if ($before) { [IO.File]::Copy($dest, (Join-Path $stage $name)) }
      [pscustomobject]@{ name=$name; before=$before; after=$after }
    })
    ConvertTo-Json -InputObject $rows | Set-Content (Join-Path $stage 'files.json')
    [IO.Directory]::Move($stage, $state)
    try {
      foreach ($row in $rows) {
        $source = if ($row.name -eq 'Assembly-CSharp.dll') { Join-Path $state 'patched.tmp' } else { Find-ModFile $row.name }
        Put-File $source (Join-Path $managed $row.name)
      }
    } catch { [IO.File]::Delete((Join-Path $state 'patched.tmp')); Restore-Install; throw }
    [IO.File]::Delete((Join-Path $state 'patched.tmp'))
  } finally { if ([IO.Directory]::Exists($stage)) { [IO.Directory]::Delete($stage, $true) } }
} finally { $lock.Dispose() }
Write-Host "Coop instalado em $GameDir."
