# Roda o modelo de projeção v3.
#  -Alvo   eleição reproduzida (TSV por seção, com horário de chegada)
#  -Base   eleição anterior equivalente (1º turno anterior para 1º turno; 2º turno anterior para 2º turno)
#  -Aux    (2º turno) 1º turno do mesmo ano: swing extra e modelo de transferência
#  -Ordem  eleição cuja ordem de chegada serve de guia para a trajetória (pode ser vazia)
param([string]$Alvo, [string]$Base, [string]$Aux = "", [string]$Ordem = "", [string]$Modo, [string]$Saida,
      [int]$B = 100, [int]$Seed = 2026, [string]$Traj = "misto",
      [switch]$SemMunicipio, [switch]$SemTransferencia, [switch]$SemRegressao, [double]$Tau = 0.0025)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not ('Projecao3' -as [type])) { Add-Type -Path (Join-Path $dir 'Projecao3.cs') -ReferencedAssemblies System.Core, System.Linq }
[Projecao3]::trajMode = $Traj; [Projecao3]::tau = $Tau
[Projecao3]::usarMunicipio = -not $SemMunicipio; [Projecao3]::usarTransferencia = -not $SemTransferencia; [Projecao3]::usarRegressao = -not $SemRegressao
$sw = [Diagnostics.Stopwatch]::StartNew()
$json = [Projecao3]::Run($Alvo, $Base, $Aux, $Ordem, $Modo, $B, $Seed)
[IO.File]::WriteAllText($Saida, $json, (New-Object Text.UTF8Encoding $false))
Write-Output "OK em $([int]$sw.Elapsed.TotalSeconds)s -> $Saida"
