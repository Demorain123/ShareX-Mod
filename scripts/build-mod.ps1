[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [ValidateSet("x64", "ARM64")][string]$Platform = "x64",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$runtimeId = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$worktree = Join-Path ([System.IO.Path]::GetTempPath()) ("ShareX-Mod-build-" + [guid]::NewGuid().ToString("N"))

try {
    Write-Host "Creating clean temporary worktree: $worktree" -ForegroundColor Cyan
    & git -C $repoRoot worktree add --detach $worktree HEAD
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed" }

    & pwsh -NoProfile -File (Join-Path $worktree "scripts\run-v04-hooks.ps1")
    if ($LASTEXITCODE -ne 0) { throw "v0.4 overlay hook application failed" }

    $postHook = Join-Path $worktree "scripts\apply-v041-post-hooks.ps1"
    if (Test-Path $postHook) {
        & pwsh -NoProfile -File $postHook
        if ($LASTEXITCODE -ne 0) { throw "v0.4.1 post-hook application failed" }
    }

    Push-Location $worktree
    try {
        dotnet restore --runtime $runtimeId "ShareX.sln"
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

        if ($Platform -eq "ARM64") {
            dotnet restore --runtime "win-x64" "ShareX.Setup\ShareX.Setup.csproj"
            if ($LASTEXITCODE -ne 0) { throw "x64 setup restore failed" }
        }

        dotnet build --no-restore --configuration $Configuration -p:Platform=$Platform --self-contained true /m:1 "ShareX.sln"
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

        if ($Platform -eq "ARM64") {
            dotnet build --configuration $Configuration -p:Platform=x64 --self-contained true "ShareX.Setup\ShareX.Setup.csproj"
            if ($LASTEXITCODE -ne 0) { throw "x64 setup build failed" }
        }

        & "ShareX.Setup\bin\$Configuration\win-x64\ShareX.Setup.exe" -silent -job $Configuration -platform $Platform
        if ($LASTEXITCODE -ne 0) { throw "ShareX.Setup packaging failed" }
    }
    finally {
        Pop-Location
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot "mod-output"
    }

    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    Copy-Item -Path (Join-Path $worktree "Output\*") -Destination $OutputDirectory -Recurse -Force
    Write-Host "ShareX-Mod build complete: $OutputDirectory" -ForegroundColor Green
}
finally {
    if (Test-Path $worktree) {
        & git -C $repoRoot worktree remove --force $worktree 2>$null
    }
}
