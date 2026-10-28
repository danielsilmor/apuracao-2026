# Roda o modelo de projeção: alvo (eleição apurada) x base (eleição anterior)
param([string]$Alvo, [string]$Base, [string]$Modo, [string]$Saida, [int]$B = 100, [int]$Seed = 2026,
      [double]$KMun = 10000, [double]$KUF = 20000, [double]$KReg = 100000)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not ('Projecao' -as [type])) {
  Add-Type -Path (Join-Path $dir 'Projecao.cs') -ReferencedAssemblies System.Core, System.Linq
}
[Projecao]::kMun = $KMun; [Projecao]::kUF = $KUF; [Projecao]::kReg = $KReg
$sw = [Diagnostics.Stopwatch]::StartNew()
$json = [Projecao]::Run($Alvo, $Base, $Modo, $Modo, $B, $Seed)
[IO.File]::WriteAllText($Saida, $json, (New-Object Text.UTF8Encoding $false))
Write-Output "OK em $([int]$sw.Elapsed.TotalSeconds)s -> $Saida"
