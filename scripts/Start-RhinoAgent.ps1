param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [string] $RhinoPath = "C:\Program Files\Rhino 8\System\Rhino.exe",

    [switch] $WithMcp,

    [switch] $SelfTest,

    [switch] $ProviderSelfTest,

    [switch] $PromptSelfTest,

    [switch] $ExitAfterSelfTest,

    [int] $SelfTestTimeoutSeconds = 45,

    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "RhinoAgent.sln"
$plugin = Join-Path $repoRoot "src\RhinoAgent\bin\$Configuration\net8.0\RhinoAgent.rhp"

function Format-ProcessArgument {
    param([Parameter(Mandatory)] [string] $Path)

    if ($Path -match "\s") {
        return """$Path"""
    }

    return $Path
}

if (-not $NoBuild) {
    dotnet build $solution --configuration $Configuration | Write-Host
}

if (-not (Test-Path $RhinoPath)) {
    throw "Rhino executable was not found: $RhinoPath"
}

if (-not (Test-Path $plugin)) {
    throw "RhinoAgent plugin was not found. Build first: $plugin"
}

$runScriptParts = New-Object System.Collections.Generic.List[string]
$rhinoArgs = New-Object System.Collections.Generic.List[string]
$rhinoArgs.Add("/netcore")
$rhinoArgs.Add("/nosplash")
$rhinoArgs.Add("-_LoadPlugin")
$rhinoArgs.Add((Format-ProcessArgument $plugin))
$selfTestOutput = Join-Path ([System.IO.Path]::GetTempPath()) "RhinoAgent\self-test.json"
$providerSelfTestOutput = Join-Path ([System.IO.Path]::GetTempPath()) "RhinoAgent\provider-self-test.json"
$promptSelfTestOutput = Join-Path ([System.IO.Path]::GetTempPath()) "RhinoAgent\prompt-self-test.json"

if ($WithMcp) {
    $mcpPackage = Join-Path $env:APPDATA "McNeel\Rhinoceros\packages\8.0\rhinomcp"
    $mcpPlugin = Get-ChildItem $mcpPackage -Recurse -Filter "rhinomcp.rhp" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($mcpPlugin) {
        $rhinoArgs.Add("-_LoadPlugin")
        $rhinoArgs.Add((Format-ProcessArgument $mcpPlugin.FullName))
        $runScriptParts.Add("_MCPStart _Enter")
    }
    else {
        Write-Warning "RhinoMCP package was not found under $mcpPackage; launching RhinoAgent only."
    }
}

if ($SelfTest) {
    if (Test-Path $selfTestOutput) {
        Remove-Item -LiteralPath $selfTestOutput -Force
    }

    $runScriptParts.Add("_AgentSelfTest _Enter")
}

if ($ProviderSelfTest) {
    if (Test-Path $providerSelfTestOutput) {
        Remove-Item -LiteralPath $providerSelfTestOutput -Force
    }

    $runScriptParts.Add("_AgentProviderSelfTest _Enter")
}

if ($PromptSelfTest) {
    if (Test-Path $promptSelfTestOutput) {
        Remove-Item -LiteralPath $promptSelfTestOutput -Force
    }

    $runScriptParts.Add("_AgentPromptSelfTest _Enter")
}

if ($runScriptParts.Count -gt 0) {
    $macro = "! " + ($runScriptParts -join " ")
    $rhinoArgs.Add("/runscript=""$macro""")
}

$process = Start-Process -FilePath $RhinoPath -ArgumentList $rhinoArgs -PassThru
Write-Host "Started Rhino $($process.Id)"
Write-Host "Plugin: $plugin"

function Wait-RhinoAgentTestOutput {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Label
    )

    $deadline = (Get-Date).AddSeconds($SelfTestTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            Write-Host "$Label`: $Path"
            Get-Content -Raw $Path | Write-Host
            return
        }

        if ($process.HasExited) {
            throw "Rhino exited before writing $Label output."
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for $Label to write $Path"
}

if ($SelfTest) {
    Wait-RhinoAgentTestOutput -Path $selfTestOutput -Label "Self-test"
}

if ($ProviderSelfTest) {
    Wait-RhinoAgentTestOutput -Path $providerSelfTestOutput -Label "Provider self-test"
}

if ($PromptSelfTest) {
    Wait-RhinoAgentTestOutput -Path $promptSelfTestOutput -Label "Prompt self-test"
}

if (($SelfTest -or $ProviderSelfTest -or $PromptSelfTest) -and $ExitAfterSelfTest -and -not $process.HasExited) {
    $null = $process.CloseMainWindow()
    Start-Sleep -Seconds 3
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
