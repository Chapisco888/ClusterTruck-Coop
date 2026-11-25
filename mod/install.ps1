# ClusterTruck Coop - instalador / desinstalador
#   install.ps1 [-GameDir <pasta do jogo>] [-Uninstall]
# Faz backup do Assembly-CSharp.dll original (Assembly-CSharp.dll.coop-backup) e injeta uma chamada
# ClusterTruckCoop.Loader.Init() no inicio de Manager.Awake. -Uninstall restaura o backup.
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

if ($Uninstall) {
  if (Test-Path $backup) { Remove-Item $target; Move-Item $backup $target }
  foreach ($f in $ours) { Remove-Item (Join-Path $managed $f) -ErrorAction SilentlyContinue }
  Write-Host "Coop removido de $GameDir"
  return
}

# Patch always starts from the pristine backup. Move (not copy) so a hard-linked original is never written through.
if (-not (Test-Path $backup)) { Move-Item $target $backup } else { Remove-Item $target }
try {
  foreach ($f in $ours) { Copy-Item (Find-ModFile $f) $managed -Force }
  Add-Type -Path (Join-Path $managed 'Mono.Cecil.dll')
  $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
  $resolver.AddSearchDirectory($managed)
  $rp = New-Object Mono.Cecil.ReaderParameters
  $rp.AssemblyResolver = $resolver
  $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($backup, $rp)
  $coop = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'ClusterTruckCoop.dll'), $rp)
  $init = $coop.MainModule.GetType('ClusterTruckCoop.Loader').Methods | Where-Object Name -eq 'Init'
  $awake = $asm.MainModule.GetType('Manager').Methods | Where-Object Name -eq 'Awake'
  if (-not $init -or -not $awake) { throw 'Metodo Manager.Awake ou Loader.Init nao encontrado (versao do jogo diferente?)' }
  $il = $awake.Body.GetILProcessor()
  $il.InsertBefore($awake.Body.Instructions[0], $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $asm.MainModule.ImportReference($init)))
  $asm.Write($target)
  $asm.Dispose(); $coop.Dispose()
} catch {
  if (Test-Path $target) { Remove-Item $target }
  Copy-Item $backup $target
  throw
}
Write-Host "Coop instalado em $GameDir. No jogo, aperte F8 para hospedar ou entrar."
