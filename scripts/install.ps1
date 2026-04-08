# unifocl installer for Windows (x64)
# Usage (run in PowerShell as a regular user):
#   iwr -useb https://raw.githubusercontent.com/Kiankinakomochi/unifocl/main/scripts/install.ps1 | iex
#   # or with a specific version:
#   & { $env:VERSION = "2.3.0"; iwr -useb https://... | iex }
#   # strict attestation verification (requires gh CLI):
#   & { $env:UNIFOCL_REQUIRE_ATTESTATION = "1"; iwr -useb https://... | iex }

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
$ChecksumUrl = "https://github.com/$Repo/releases/download/v$Version/unifocl-$Version-checksums.txt"
$TmpDir      = Join-Path $env:TEMP "unifocl-install-$([System.IO.Path]::GetRandomFileName())"
$ZipPath     = Join-Path $TmpDir "unifocl.zip"
$ChecksumsPath = Join-Path $TmpDir "checksums.txt"

# ── Download ─────────────────────────────────────────────────────────────────
Write-Host "Downloading unifocl $Version (win-x64)..."
New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null
try {
    Invoke-WebRequest $DownloadUrl -OutFile $ZipPath -UseBasicParsing
    Invoke-WebRequest $ChecksumUrl -OutFile $ChecksumsPath -UseBasicParsing
} catch {
    Write-Error "Download failed: $_`nURL: $DownloadUrl"
    exit 1
}

# ── Verify checksum (required) ──────────────────────────────────────────────
$TargetName = "unifocl-$Version-win-x64.zip"
$ExpectedHash = $null
foreach ($line in Get-Content $ChecksumsPath) {
    if ($line -match '^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>.+)$') {
        if ($Matches['name'].Trim() -eq $TargetName) {
            $ExpectedHash = $Matches['hash'].ToLowerInvariant()
            break
        }
    }
}
if (-not $ExpectedHash) {
    Write-Error "Failed to find checksum entry for $TargetName"
    exit 1
}

$ActualHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualHash -ne $ExpectedHash) {
    Write-Error "Checksum verification failed. Expected $ExpectedHash, got $ActualHash"
    exit 1
}
Write-Host "Checksum verified."

# ── Verify attestation (optional, strict via env) ──────────────────────────
$StrictAttestation = ($env:UNIFOCL_REQUIRE_ATTESTATION -eq "1")
$Gh = Get-Command gh -ErrorAction SilentlyContinue
if ($Gh) {
    & gh attestation verify $ZipPath --repo $Repo | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Attestation verified."
    } elseif ($StrictAttestation) {
        Write-Error "Attestation verification failed (strict mode)."
        exit 1
    } else {
        Write-Warning "Attestation verification failed; continuing (non-strict mode)."
    }
} elseif ($StrictAttestation) {
    Write-Error "gh CLI is required when UNIFOCL_REQUIRE_ATTESTATION=1."
    exit 1
} else {
    Write-Warning "gh CLI not found; skipping attestation verification."
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
