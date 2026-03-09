param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('provision', 'seed', 'start-daemon', 'teardown')]
    [string]$Command,

    [string]$RepoRoot,
    [string]$WorktreePath,
    [string]$Branch,
    [string]$SourceProject,
    [string]$ProjectPath,
    [int]$PortStart = 18080,
    [int]$PortEnd = 21999,
    [switch]$SeedLibrary
)

$ErrorActionPreference = 'Stop'

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$PathValue)

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $PathValue))
}

function Seed-LibraryCache {
    param(
        [Parameter(Mandatory = $true)][string]$SourceProjectPath,
        [Parameter(Mandatory = $true)][string]$WorktreeRoot
    )

    $sourceLibrary = Join-Path $SourceProjectPath 'Library'
    $targetLibrary = Join-Path $WorktreeRoot 'Library'

    if (-not (Test-Path $sourceLibrary -PathType Container)) {
        throw "source Library does not exist: $sourceLibrary"
    }

    if (Test-Path $targetLibrary) {
        throw "target Library already exists: $targetLibrary"
    }

    Copy-Item -Path $sourceLibrary -Destination $targetLibrary -Recurse
    Write-Output "seeded Library cache: $sourceLibrary -> $targetLibrary"
}

function Find-OpenPort {
    param(
        [Parameter(Mandatory = $true)][int]$StartPort,
        [Parameter(Mandatory = $true)][int]$EndPort
    )

    for ($port = $StartPort; $port -le $EndPort; $port++) {
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            $listener.Stop()
            return $port
        }
        catch {
            continue
        }
    }

    throw "failed to find open port in range $StartPort-$EndPort"
}

switch ($Command) {
    'provision' {
        if (-not $RepoRoot) { throw 'missing -RepoRoot' }
        if (-not $WorktreePath) { throw 'missing -WorktreePath' }
        if (-not $Branch) { throw 'missing -Branch' }

        $repoRootAbs = Resolve-AbsolutePath $RepoRoot
        $worktreePathAbs = Resolve-AbsolutePath $WorktreePath

        & git -C $repoRootAbs fetch origin main | Out-Null
        & git -C $repoRootAbs worktree add $worktreePathAbs -b $Branch origin/main

        if ($SeedLibrary.IsPresent) {
            if (-not $SourceProject) {
                throw '-SeedLibrary requires -SourceProject'
            }

            $sourceProjectAbs = Resolve-AbsolutePath $SourceProject
            Seed-LibraryCache -SourceProjectPath $sourceProjectAbs -WorktreeRoot $worktreePathAbs
        }

        Write-Output "provisioned worktree: $worktreePathAbs"
        Write-Output "branch: $Branch"
    }
    'seed' {
        if (-not $SourceProject) { throw 'missing -SourceProject' }
        if (-not $WorktreePath) { throw 'missing -WorktreePath' }

        $sourceProjectAbs = Resolve-AbsolutePath $SourceProject
        $worktreePathAbs = Resolve-AbsolutePath $WorktreePath
        Seed-LibraryCache -SourceProjectPath $sourceProjectAbs -WorktreeRoot $worktreePathAbs
    }
    'start-daemon' {
        if (-not $WorktreePath) { throw 'missing -WorktreePath' }
        if (-not $ProjectPath) { throw 'missing -ProjectPath' }

        $worktreePathAbs = Resolve-AbsolutePath $WorktreePath
        if ([System.IO.Path]::IsPathRooted($ProjectPath)) {
            $projectPathAbs = Resolve-AbsolutePath $ProjectPath
        }
        else {
            $projectPathAbs = Resolve-AbsolutePath (Join-Path $worktreePathAbs $ProjectPath)
        }
        $selectedPort = Find-OpenPort -StartPort $PortStart -EndPort $PortEnd

        $daemonCommand = "/daemon start --project `"$projectPathAbs`" --port $selectedPort --headless"
        $startupLog = Join-Path ([System.IO.Path]::GetTempPath()) ("unifocl-daemon-start-{0}.log" -f [Guid]::NewGuid().ToString('N'))

        Push-Location $worktreePathAbs
        try {
            "$daemonCommand`n/quit`n" | dotnet run --project src/unifocl/unifocl.csproj --disable-build-servers -v minimal *> $startupLog
        }
        finally {
            Pop-Location
        }

        $ready = $false
        for ($i = 0; $i -lt 40; $i++) {
            try {
                Invoke-WebRequest -Uri "http://127.0.0.1:$selectedPort/ping" -UseBasicParsing | Out-Null
                $ready = $true
                break
            }
            catch {
                Start-Sleep -Milliseconds 250
            }
        }

        if (-not $ready) {
            throw "daemon did not become ready on port $selectedPort. startup log: $startupLog"
        }

        Write-Output "daemon-ready-port: $selectedPort"
        Write-Output "daemon-start-command: $daemonCommand"
    }
    'teardown' {
        if (-not $RepoRoot) { throw 'missing -RepoRoot' }
        if (-not $WorktreePath) { throw 'missing -WorktreePath' }

        $repoRootAbs = Resolve-AbsolutePath $RepoRoot
        $worktreePathAbs = Resolve-AbsolutePath $WorktreePath

        & git -C $repoRootAbs worktree remove --force $worktreePathAbs
        & git -C $repoRootAbs worktree prune

        Write-Output "removed worktree: $worktreePathAbs"
    }
}
