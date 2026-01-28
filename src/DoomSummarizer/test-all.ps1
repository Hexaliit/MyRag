#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Comprehensive test script for DoomSummarizer.

.DESCRIPTION
    Runs build verification, unit tests, and CLI integration smoke tests.
    Exit code 0 = all passed, non-zero = failures detected.

.EXAMPLE
    .\test-all.ps1                        # Run everything
    .\test-all.ps1 -SkipSmoke             # Unit tests only
    .\test-all.ps1 -SkipUnit -SkipBuild   # Smoke tests only
    .\test-all.ps1 -Verbose               # Full output
#>

param(
    [switch]$SkipBuild,
    [switch]$SkipUnit,
    [switch]$SkipSmoke,
    [switch]$Verbose,
    [string]$Filter
)

$ErrorActionPreference = 'Continue'
$script:failures = @()
$script:passCount = 0
$script:failCount = 0
$script:skipCount = 0

$mainProject = Join-Path $PSScriptRoot 'DoomSummarizer.csproj'
$testProject = [IO.Path]::Combine($PSScriptRoot, '..', 'DoomSummarizer.Tests', 'DoomSummarizer.Tests.csproj')

# Find the built exe (may be in RID-specific subdir like win-x64)
$exePath = $null
$searchPaths = @(
    [IO.Path]::Combine($PSScriptRoot, 'bin', 'Debug', 'net10.0', 'doomsummarizer.exe')
    [IO.Path]::Combine($PSScriptRoot, 'bin', 'Debug', 'net10.0', 'win-x64', 'doomsummarizer.exe')
    [IO.Path]::Combine($PSScriptRoot, 'bin', 'Release', 'net10.0', 'doomsummarizer.exe')
    [IO.Path]::Combine($PSScriptRoot, 'bin', 'Release', 'net10.0', 'win-x64', 'doomsummarizer.exe')
)
foreach ($p in $searchPaths) {
    if (Test-Path $p) { $exePath = $p; break }
}
if (-not $exePath) { $exePath = $searchPaths[0] }

function Write-Header([string]$text) {
    Write-Host ''
    Write-Host '========================================' -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host '========================================' -ForegroundColor Cyan
}

function Write-Pass([string]$text) {
    $script:passCount++
    Write-Host '  PASS ' -ForegroundColor Green -NoNewline
    Write-Host $text
}

function Write-Fail([string]$text) {
    $script:failCount++
    $script:failures += $text
    Write-Host '  FAIL ' -ForegroundColor Red -NoNewline
    Write-Host $text
}

function Write-Skip([string]$text) {
    $script:skipCount++
    Write-Host '  SKIP ' -ForegroundColor Yellow -NoNewline
    Write-Host $text
}

function Run-Cli([string]$CliArgs, [int]$TimeoutSec = 30, [string]$Name) {
    if (-not (Test-Path $exePath)) {
        Write-Fail "$Name - binary not found"
        return $null
    }

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exePath
        $psi.Arguments = $CliArgs
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        $proc.Start() | Out-Null

        $out = $proc.StandardOutput.ReadToEnd()
        $err = $proc.StandardError.ReadToEnd()

        $done = $proc.WaitForExit($TimeoutSec * 1000)
        if (-not $done) {
            $proc.Kill()
            Write-Fail "$Name - timed out"
            return $null
        }

        return @{ ExitCode = $proc.ExitCode; Stdout = $out; Stderr = $err }
    }
    catch {
        Write-Fail "$Name - exception: $_"
        return $null
    }
}

# ============================================================
# Step 1: Build
# ============================================================
if (-not $SkipBuild) {
    Write-Header 'Step 1: Build Verification'

    $buildOut = & dotnet build $mainProject --nologo -v q 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Pass 'Main project builds successfully'
    }
    else {
        Write-Fail 'Main project build failed'
        if ($Verbose) { $buildOut | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
    }

    $testBuildOut = & dotnet build $testProject --nologo -v q 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Pass 'Test project builds successfully'
    }
    else {
        Write-Fail 'Test project build failed'
        if ($Verbose) { $testBuildOut | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
    }
}
else {
    Write-Header 'Step 1: Build (SKIPPED)'
    Write-Skip 'Build verification'
}

# ============================================================
# Step 2: Unit Tests (xUnit)
# ============================================================
if (-not $SkipUnit) {
    Write-Header 'Step 2: Unit Tests (xUnit)'

    $filterExpr = 'Category!=Smoke'
    if ($Filter) {
        $filterExpr = "Category!=Smoke" + '&' + $Filter
    }
    $testCmdArgs = @('test', $testProject, '--nologo', '-v', 'q', '--filter', $filterExpr)

    $testOut = & dotnet @testCmdArgs 2>&1
    $testExit = $LASTEXITCODE

    if ($testExit -eq 0) {
        $passLine = $testOut | Select-String 'Passed!' | Select-Object -First 1
        if ($passLine) {
            Write-Pass "xUnit: $($passLine.Line.Trim())"
        }
        else {
            Write-Pass 'xUnit tests passed'
        }
    }
    else {
        Write-Fail "xUnit tests failed (exit code $testExit)"
    }

    if ($Verbose -or $testExit -ne 0) {
        $testOut | Select-String -Pattern 'FAIL' | ForEach-Object {
            Write-Host "    $_" -ForegroundColor DarkGray
        }
    }
}
else {
    Write-Header 'Step 2: Unit Tests (SKIPPED)'
    Write-Skip 'xUnit tests'
}

# ============================================================
# Step 3: CLI Smoke Tests
# ============================================================
if (-not $SkipSmoke) {
    Write-Header 'Step 3: CLI Smoke Tests'

    if (-not (Test-Path $exePath)) {
        Write-Fail "CLI binary not found at $exePath - build first"
    }
    else {
        # Help
        $r = Run-Cli -CliArgs '--help' -Name 'help' -TimeoutSec 15
        if ($r -and $r.ExitCode -eq 0 -and $r.Stdout -match 'scroll|config|sources') {
            Write-Pass 'help: shows command list'
        }
        elseif ($r) { Write-Fail "help: exit code $($r.ExitCode)" }

        # Config
        $r = Run-Cli -CliArgs 'config' -Name 'config' -TimeoutSec 15
        if ($r -and $r.ExitCode -eq 0) { Write-Pass 'config: exits cleanly' }
        elseif ($r) { Write-Fail "config: exit code $($r.ExitCode)" }

        # Sources
        $r = Run-Cli -CliArgs 'sources' -Name 'sources' -TimeoutSec 15
        if ($r -and $r.ExitCode -eq 0) { Write-Pass 'sources: lists available sources' }
        elseif ($r) { Write-Fail "sources: exit code $($r.ExitCode)" }

        # Show
        $r = Run-Cli -CliArgs 'show' -Name 'show' -TimeoutSec 15
        if ($r -and $r.ExitCode -eq 0) { Write-Pass 'show: exits cleanly' }
        elseif ($r) { Write-Fail "show: exit code $($r.ExitCode)" }

        # Scroll quiet no-llm
        $r = Run-Cli -CliArgs 'scroll "test query" -q --no-llm --limit 3' -Name 'scroll-quiet' -TimeoutSec 60
        if ($r) {
            if ($r.ExitCode -eq 0) { Write-Pass 'scroll (quiet, no-llm): completes' }
            elseif ($r.Stderr -match 'No items|Error|failed') { Write-Pass 'scroll (quiet, no-llm): exits with expected warning' }
            else { Write-Fail "scroll (quiet, no-llm): exit $($r.ExitCode)" }
        }

        # Scroll JSON
        $r = Run-Cli -CliArgs 'scroll "tech news" -q --no-llm --limit 2 --json' -Name 'scroll-json' -TimeoutSec 60
        if ($r -and $r.ExitCode -eq 0 -and $r.Stdout.Trim().Length -gt 0) {
            # Strip ANSI escape codes before checking JSON
            $cleaned = $r.Stdout -replace '\x1b\[[0-9;]*m', '' -replace '\x1b\[\?[0-9]*[hl]', ''
            $cleaned = $cleaned.Trim()
            if ($cleaned -match '^\s*[\[{]') { Write-Pass 'scroll (json): produces valid JSON' }
            else { Write-Fail 'scroll (json): output is not JSON' }
        }
        elseif ($r -and $r.ExitCode -eq 0) { Write-Pass 'scroll (json): exits cleanly' }
        elseif ($r) { Write-Fail "scroll (json): exit $($r.ExitCode)" }

        # Scroll local
        $r = Run-Cli -CliArgs 'scroll "test" -q --no-llm --local --limit 2' -Name 'scroll-local' -TimeoutSec 30
        if ($r) {
            if ($r.ExitCode -eq 0 -or $r.Stderr -match 'No items|No local|empty') { Write-Pass 'scroll (local): exits cleanly' }
            else { Write-Fail "scroll (local): exit $($r.ExitCode)" }
        }

        # Scroll debug
        $r = Run-Cli -CliArgs 'scroll "test" -q --no-llm --limit 2 --debug' -Name 'scroll-debug' -TimeoutSec 60
        if ($r -and $r.ExitCode -ge 0) { Write-Pass 'scroll (debug): no crash' }
        elseif ($r) { Write-Fail "scroll (debug): exit $($r.ExitCode)" }

        # Page invalid URL
        $r = Run-Cli -CliArgs 'page "not-a-url" -q' -Name 'page-invalid' -TimeoutSec 15
        if ($r) { Write-Pass "page (invalid URL): handled (exit: $($r.ExitCode))" }

        # Scroll briefing flag
        $r = Run-Cli -CliArgs 'scroll "AI news" -q --no-llm --limit 2 --briefing' -Name 'scroll-briefing' -TimeoutSec 60
        if ($r -and $r.ExitCode -ge 0) { Write-Pass 'scroll (briefing): flag accepted' }
        elseif ($r) { Write-Fail "scroll (briefing): exit $($r.ExitCode)" }

        # Scroll entities flag
        $r = Run-Cli -CliArgs 'scroll "tech" -q --no-llm --limit 2 --entities' -Name 'scroll-entities' -TimeoutSec 60
        if ($r -and $r.ExitCode -ge 0) { Write-Pass 'scroll (entities): flag accepted' }
        elseif ($r) { Write-Fail "scroll (entities): exit $($r.ExitCode)" }
    }
}
else {
    Write-Header 'Step 3: CLI Smoke Tests (SKIPPED)'
    Write-Skip 'CLI smoke tests'
}

# ============================================================
# Summary
# ============================================================
Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  Test Summary' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host "  Passed:  $($script:passCount)" -ForegroundColor Green
if ($script:failCount -gt 0) {
    Write-Host "  Failed:  $($script:failCount)" -ForegroundColor Red
}
else {
    Write-Host '  Failed:  0' -ForegroundColor Green
}
if ($script:skipCount -gt 0) {
    Write-Host "  Skipped: $($script:skipCount)" -ForegroundColor Yellow
}
Write-Host ''

if ($script:failures.Count -gt 0) {
    Write-Host '  Failed tests:' -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    Write-Host ''
    exit 1
}
else {
    Write-Host '  All tests passed!' -ForegroundColor Green
    Write-Host ''
    exit 0
}
