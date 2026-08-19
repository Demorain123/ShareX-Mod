[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$program = Join-Path $repoRoot "LongCapture.Standalone\Program.cs"
$project = Join-Path $repoRoot "LongCapture.Standalone\LongCapture.Standalone.csproj"

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "Browser Agent v0.1 target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1 compatibility anchor not found: '$Marker' in $Path"
    }

    Write-Host "[BrowserAgent-v0.1] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $updated = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1] applied: $Marker" -ForegroundColor Cyan
    }
}

# Keep the normal RC6 Program.cs pristine in git. This hook is deliberately run
# only after the full RC6 overlay chain has finished, so future RC hooks retain
# their original compatibility anchors and Browser Agent stays a removable shell.
Replace-Literal -Path $program `
    -Old @'
        LongCaptureLog.Initialize();
        RegisterGlobalExceptionLogging();
'@ `
    -New @'
        LongCaptureLog.Initialize();

        // Chrome launches a Native Messaging host with the calling extension
        // origin as argv[0]. Handle that path before desktop UI initialization.
        if (BrowserAgentNativeHost.IsChromeNativeMessagingLaunch(args))
        {
            return BrowserAgentNativeHost.Run(args);
        }

        RegisterGlobalExceptionLogging();
'@ `
    -Marker 'BrowserAgentNativeHost.IsChromeNativeMessagingLaunch(args)'

Replace-Literal -Path $program `
    -Old @'
        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunSelfTest();
            LongCaptureLog.Info($"--self-test completed exitCode={code}");
            return code;
        }
'@ `
    -New @'
        if (args.Any(x => string.Equals(x, "--browser-agent-poc-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = BrowserAgentPocSelfTest.Run();
            LongCaptureLog.Info($"--browser-agent-poc-self-test completed exitCode={code}");
            return code;
        }

        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunSelfTest();
            LongCaptureLog.Info($"--self-test completed exitCode={code}");
            return code;
        }
'@ `
    -Marker '--browser-agent-poc-self-test'

Replace-Literal -Path $program `
    -Old @'
        InitializeDesktopUiHosts();
        using var exclusionWatcher = CaptureExclusionWatcher.Start();
'@ `
    -New @'
        InitializeDesktopUiHosts();

        if (args.Any(x => string.Equals(x, "--browser-agent-poc", StringComparison.OrdinalIgnoreCase)))
        {
            using var pocForm = new BrowserAgentPocForm();
            LongCaptureLog.Info("entering Browser Agent v0.1 PoC WinForms message loop");
            Application.Run(pocForm);
            LongCaptureLog.Info("Browser Agent v0.1 PoC WinForms message loop exited");
            return 0;
        }

        using var exclusionWatcher = CaptureExclusionWatcher.Start();
'@ `
    -Marker 'using var pocForm = new BrowserAgentPocForm();'

Replace-Literal -Path $program `
    -Old @'
            string versionPath = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.json");
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.v04.json");
            if (!File.Exists(versionPath) || !File.Exists(settingsPath)) return 10;

            Type? modBuildInfo = Type.GetType("ShareX.ScreenCaptureLib.ShareXModBuildInfo, ShareX.ScreenCaptureLib", throwOnError: false);
'@ `
    -New @'
            string versionPath = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.json");
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.v04.json");
            if (!File.Exists(versionPath) || !File.Exists(settingsPath)) return 10;

            string browserAgentManifest = Path.Combine(AppContext.BaseDirectory, "BrowserAgent", "Extension", "manifest.json");
            string browserAgentWorker = Path.Combine(AppContext.BaseDirectory, "BrowserAgent", "Extension", "service-worker.js");
            string browserAgentInstaller = Path.Combine(AppContext.BaseDirectory, "BrowserAgent", "INSTALL-BROWSER-AGENT.cmd");
            if (!File.Exists(browserAgentManifest) || !File.Exists(browserAgentWorker) || !File.Exists(browserAgentInstaller)) return 30;

            int browserAgentSmoke = BrowserAgentPocSelfTest.Run();
            if (browserAgentSmoke != 0) return browserAgentSmoke;

            Type? modBuildInfo = Type.GetType("ShareX.ScreenCaptureLib.ShareXModBuildInfo, ShareX.ScreenCaptureLib", throwOnError: false);
'@ `
    -Marker 'string browserAgentManifest = Path.Combine(AppContext.BaseDirectory, "BrowserAgent"'

# Package only the thin PoC helper assets. SDK-style Compile globs pick up the
# BrowserAgent*.cs source automatically, so no project-level source mutation is needed.
Replace-Literal -Path $project `
    -Old @'
    <Content Include="Replay-Last-Capture.ps1" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
'@ `
    -New @'
    <Content Include="Replay-Last-Capture.ps1" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <Content Include="START-BROWSER-AGENT-POC.cmd" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <Content Include="BrowserAgent\**\*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
'@ `
    -Marker 'START-BROWSER-AGENT-POC.cmd'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1 post-RC6 overlay compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1 post-RC6 overlay applied." -ForegroundColor Green
}
