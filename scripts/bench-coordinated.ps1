<#
.SYNOPSIS
    Lane-change mode benchmark on one scenario: dense-LC (default) vs full-coordination vs parity.

.DESCRIPTION
    Builds Sim.BenchCity in Release, then times the engine on a scenario in all THREE lane-change modes,
    region-parallel, with FCD export off. Prints a small comparison table (best-of-Repeats wall time, RTF,
    steps/sec, stuck-at-end) so you can see each mode's cost/benefit. See docs/BENCHMARK-INSTRUCTIONS.md.

      dense (default)     : aggressive dense LC, no flag        -- believable overtaking, best organic flow
      +informFollower     : --inform-follower                   -- rescues saturated grids, hurts organic flow
      parity              : --parity                            -- deterministic SUMO anchor

    All three run clean region-parallel on every committed scenario (grids and large organic nets alike).
    On organic nets expect dense >= parity > +informFollower on flow; on the saturated grid expect
    +informFollower (and parity) to drain while dense-alone gridlocks.

.PARAMETER Scenario
    Scenario directory (one each of *.net.xml, *.rou.xml, *.sumocfg). Default: the saturated grid.

.PARAMETER Steps
    Step horizon. Default 700.

.PARAMETER Repeats
    Timed runs per mode; the BEST wall time is reported (first run is JIT warm-up, discarded). Default 3.
#>
param(
    [string] $Scenario = "scenarios/_diag/willpass-saturation",
    [int]    $Steps    = 700,
    [int]    $Repeats  = 3
)

$ErrorActionPreference = "Stop"

Write-Host "Building Sim.BenchCity (Release)..." -ForegroundColor Cyan
dotnet build src/Sim.BenchCity -c Release | Out-Null

function Measure-Mode {
    param([string] $Label, [string] $ModeFlag)

    $bestWall = [double]::MaxValue
    $lastOut  = ""
    # Repeats + 1: the first (index 0) is a warm-up and is discarded.
    for ($i = 0; $i -le $Repeats; $i++) {
        $out = & dotnet run -c Release --project src/Sim.BenchCity -- `
            $Scenario --steps $Steps --region --no-fcd $ModeFlag 2>&1 | Out-String
        if ($i -eq 0) { continue }  # warm-up
        $m = [regex]::Match($out, 'wall time\s*:\s*([0-9.]+)')
        if ($m.Success) {
            $w = [double]$m.Groups[1].Value
            if ($w -lt $bestWall) { $bestWall = $w; $lastOut = $out }
        }
    }

    if ($bestWall -eq [double]::MaxValue) {
        Write-Host ("{0,-12} : FAILED (no wall-time line -- did it crash? run the raw command to see)" -f $Label) -ForegroundColor Red
        return $null
    }

    $rtf   = [regex]::Match($lastOut, 'RTF \(sim/wall\)\s*:\s*([0-9.]+)').Groups[1].Value
    $sps   = [regex]::Match($lastOut, 'steps/sec\s*:\s*([0-9.]+)').Groups[1].Value
    $stuck = [regex]::Match($lastOut, 'stuck \(still, at sim end\)\s*:\s*([0-9]+)').Groups[1].Value
    [pscustomobject]@{ Mode = $Label; WallSec = $bestWall; RTF = $rtf; StepsPerSec = $sps; StuckAtEnd = $stuck }
}

Write-Host "Scenario: $Scenario   Steps: $Steps   Repeats: $Repeats (best-of)" -ForegroundColor Cyan
$dense  = Measure-Mode -Label "dense (default)"  -ModeFlag ""
$inform = Measure-Mode -Label "+informFollower"  -ModeFlag "--inform-follower"
$parity = Measure-Mode -Label "parity"           -ModeFlag "--parity"
$coord  = $dense  # back-compat: the default mode is the "coordinated" dense LC

@($dense, $inform, $parity) | Where-Object { $_ -ne $null } | Format-Table -AutoSize

if ($parity -and $coord) {
    $delta = [math]::Round((($coord.WallSec - $parity.WallSec) / $parity.WallSec) * 100.0, 1)
    $sign  = if ($delta -ge 0) { "+" } else { "" }
    Write-Host ("dense (default) wall-time delta vs parity: {0}{1}%  (negative = dense is faster)" -f $sign, $delta) -ForegroundColor Yellow
}
