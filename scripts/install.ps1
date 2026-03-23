# unifocl installer for Windows (x64)
# Usage (run in PowerShell as a regular user):
#   iwr -useb https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.ps1 | iex
#   # or with a specific version:
#   & { $env:VERSION = "2.3.0"; iwr -useb https://... | iex }

param(
    [string]$Version = $env:VERSION
)

$ErrorActionPreference = "Stop"

$Repo       = "Kiankinakomochi/unifocl"
$InstallDir = Join-Path $env:LOCALAPPDATA "unifocl\bin"

# ── Architecture check ───────────────────────────────────────────────────────
$Arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($Arch -ne [System.Runtime.InteropServices.Architecture]::X64) {
    Write-Error "Only x64 Windows is supported. Your architecture: $Arch"
    exit 1
}

# ── Resolve version ──────────────────────────────────────────────────────────
if (-not $Version) {
    Write-Host "Fetching latest release version..."
    $Release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest"
    $Version = $Release.tag_name.TrimStart('v')
    if (-not $Version) {
        Write-Error "Failed to resolve latest version. Set `$env:VERSION to install a specific version."
        exit 1
    }
}

$DownloadUrl = "https://github.com/$Repo/releases/download/v$Version/unifocl-$Version-win-x64.zip"
$TmpDir      = Join-Path $env:TEMP "unifocl-install-$([System.IO.Path]::GetRandomFileName())"
$ZipPath     = Join-Path $TmpDir "unifocl.zip"

# ── Download ─────────────────────────────────────────────────────────────────
Write-Host "Downloading unifocl $Version (win-x64)..."
New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null
try {
    Invoke-WebRequest $DownloadUrl -OutFile $ZipPath -UseBasicParsing
} catch {
    Write-Error "Download failed: $_`nURL: $DownloadUrl"
    exit 1
}

# ── Extract ──────────────────────────────────────────────────────────────────
Write-Host "Extracting..."
Expand-Archive $ZipPath -DestinationPath $TmpDir -Force

$BinaryPath = Join-Path $TmpDir "unifocl.exe"
if (-not (Test-Path $BinaryPath)) {
    Write-Error "Extraction failed: unifocl.exe not found in archive."
    exit 1
}

# ── Install ──────────────────────────────────────────────────────────────────
Write-Host "Installing to $InstallDir..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $BinaryPath (Join-Path $InstallDir "unifocl.exe") -Force

# ── Add to PATH ───────────────────────────────────────────────────────────────
$UserPath = [Environment]::GetEnvironmentVariable("PATH", "User") ?? ""
if ($UserPath -notlike "*$InstallDir*") {
    [Environment]::SetEnvironmentVariable("PATH", "$UserPath;$InstallDir", "User")
    Write-Host ""
    Write-Host "Added $InstallDir to your user PATH."
    Write-Host "Restart your terminal (or open a new window) for PATH to take effect."
}

# ── Cleanup ───────────────────────────────────────────────────────────────────
Remove-Item $TmpDir -Recurse -Force -ErrorAction SilentlyContinue

# ── Done ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "unifocl $Version installed successfully."
Write-Host "Run 'unifocl --version' to verify."
