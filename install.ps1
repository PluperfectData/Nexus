# Installs Nexus on this Windows computer: the programs it needs, its build, and a Nexus shortcut on the desktop.
# Then it starts Nexus. Running it again rebuilds Nexus from the code in this folder, and skips the programs already
# installed. With Scry beside this folder, it can install Scry too.
#
# Right-click this file and choose Run with PowerShell, or run:
#   powershell -ExecutionPolicy Bypass -File "<this folder>\install.ps1"

# -Alongside is how Scry's installer runs this one: it neither asks about Scry nor starts Nexus.
param([switch]$Alongside)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$data = Join-Path $root 'data'
$app = Join-Path $data 'app'
$repos = Split-Path (Split-Path $root)
$jwt = Join-Path $repos 'Pluperfect.Jwt'
$mail = Join-Path $repos 'Pluperfect.Mail'
$scry = Join-Path (Split-Path $root) 'Scry'
$apiPort = 6163

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "== $message" -ForegroundColor Green
}

function Invoke-Native([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "This failed, with exit code ${LASTEXITCODE}: $file $($arguments -join ' ')"
    }
}

# A program installed by winget is on the saved path, but not yet on this window's.
function Update-SessionPath {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Test-Native([scriptblock]$check) {
    try {
        & $check *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Install-Program([string]$id, [string]$name) {
    Write-Step "Installing $name. Accept the Windows prompt if one appears."
    Invoke-Native 'winget' @('install', '--id', $id, '-e', '--source', 'winget', '--accept-package-agreements', '--accept-source-agreements')
    Update-SessionPath
}

function Test-Node {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return $false }
    return [int]((node --version).TrimStart('v').Split('.')[0]) -ge 24
}

# The SDK that Server\global.json asks for: .NET 10, at least the feature band it names.
function Test-DotNet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    Push-Location (Join-Path $root 'Server')
    try { return Test-Native { dotnet --version } } finally { Pop-Location }
}

# The newest MySQL 8 or later under Program Files, as its bin folder.
function Find-MySql {
    $found = Get-ChildItem 'C:\Program Files\MySQL' -Directory -Filter 'MySQL Server *' -ErrorAction SilentlyContinue |
        Where-Object { (Test-Path (Join-Path $_.FullName 'bin\mysqld.exe')) -and [version]($_.Name -replace '^MySQL Server ', '') -ge [version]'8.0' } |
        Sort-Object { [version]($_.Name -replace '^MySQL Server ', '') } -Descending |
        Select-Object -First 1
    if ($found) { return Join-Path $found.FullName 'bin' }
    return $null
}

function Test-Listening([int]$port) {
    return [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

# Clones a checkout the build needs when it is missing, installing Git first if it is missing too.
function Get-Checkout([string]$name, [string]$path, [string]$url) {
    if (Test-Path $path) { return }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Install-Program 'Git.Git' 'Git' }
    Write-Step "Fetching $name into $path"
    Invoke-Native 'git' @('clone', $url, $path)
}

try {
    Write-Step 'Checking the programs Nexus needs'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'winget is missing. Install App Installer from the Microsoft Store, then run this again.'
    }
    if (Test-Listening $apiPort) {
        throw "Something is using port ${apiPort}: Nexus's window, its development copy, or Nexus in Docker. Stop it, then run this again."
    }

    $withScry = $false
    if (-not $Alongside -and (Test-Path (Join-Path $scry 'install.ps1'))) {
        $withScry = (Read-Host 'Scry is beside Nexus. Install Scry too? (y/N)') -match '^[Yy]'
    }

    if (Test-DotNet) { Write-Host '.NET 10 is installed.' }
    else {
        Install-Program 'Microsoft.DotNet.SDK.10' '.NET 10 SDK'
        if (-not (Test-DotNet)) { throw '.NET 10 was installed, but this window cannot run it yet. Close it, then run this again.' }
    }

    if (Test-Node) { Write-Host 'Node 24 or newer is installed.' }
    else { Install-Program 'OpenJS.NodeJS.LTS' 'Node.js' }

    $mysqlBin = Find-MySql
    if ($mysqlBin) { Write-Host "MySQL is installed, in $mysqlBin." }
    else {
        # Nexus runs a MySQL server of its own in the data folder, so the server this installs is never set up or used.
        Install-Program 'Oracle.MySQL' 'MySQL'
        $mysqlBin = Find-MySql
        if (-not $mysqlBin) { throw 'MySQL was installed but not found under C:\Program Files\MySQL.' }
    }

    Get-Checkout 'Pluperfect.Jwt' $jwt 'https://github.com/mnwachukwu/Pluperfect.Jwt.git'
    Get-Checkout 'Pluperfect.Mail' $mail 'https://github.com/mnwachukwu/Pluperfect.Mail.git'

    Write-Step 'Building the service'
    $published = Join-Path $app 'api.new'
    if (Test-Path $published) { Remove-Item $published -Recurse -Force }
    Invoke-Native 'dotnet' @('publish', (Join-Path $root 'Server\src\Nexus.Service\Nexus.Service.csproj'), '-c', 'Release', '-o', $published)
    $current = Join-Path $app 'api'
    if (Test-Path $current) { Remove-Item $current -Recurse -Force }
    Move-Item $published $current

    Write-Step 'Building the pages'
    Invoke-Native 'npm' @('ci', '--prefix', (Join-Path $jwt 'client'))
    Invoke-Native 'npm' @('run', 'build', '--prefix', (Join-Path $jwt 'client'))
    Invoke-Native 'npm' @('ci', '--prefix', (Join-Path $root 'Client'))
    $env:VITE_API_URL = "https://localhost:$apiPort"
    Invoke-Native 'npm' @('run', 'build', '--prefix', (Join-Path $root 'Client'), '--', '--outDir', (Join-Path $app 'site'), '--emptyOutDir')
    Remove-Item Env:VITE_API_URL

    Write-Step "Trusting the certificate for Nexus's addresses, and for https://127.0.0.1:6164, where providers such as Schwab send people back"
    if (Test-Native { dotnet dev-certs https --check --trust }) { Write-Host 'The certificate is trusted.' }
    else {
        Write-Host 'Windows asks once whether to trust it. Choose Yes.'
        Invoke-Native 'dotnet' @('dev-certs', 'https', '--trust')
    }
    # PEM files in data, which start.ts serves the pages and the service with.
    $nextCertificate = Join-Path $data 'certificate.new.pem'
    $nextKey = Join-Path $data 'certificate.new.key'
    Remove-Item $nextCertificate, $nextKey -ErrorAction SilentlyContinue
    Invoke-Native 'dotnet' @('dev-certs', 'https', '--export-path', $nextCertificate, '--format', 'Pem', '--no-password')
    Move-Item $nextCertificate (Join-Path $data 'certificate.pem') -Force
    Move-Item $nextKey (Join-Path $data 'certificate.key') -Force

    Write-Step 'Recording where the programs are'
    $tools = [ordered]@{
        dotnet = (Get-Command dotnet).Source
        mysqlBin = $mysqlBin
    }
    $toolsFile = Join-Path $data 'install.json'
    [IO.File]::WriteAllText("$toolsFile.tmp", ($tools | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    Move-Item "$toolsFile.tmp" $toolsFile -Force

    Write-Step 'Putting a Nexus shortcut on the desktop'
    $node = (Get-Command node).Source
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Nexus.lnk'))
    $shortcut.TargetPath = $node
    $shortcut.Arguments = '"' + (Join-Path $root 'start.ts') + '"'
    $shortcut.WorkingDirectory = $root
    $shortcut.Description = 'Starts Nexus'
    $shortcut.Save()

    if ($Alongside) { return }

    if ($withScry) {
        Write-Step 'Installing Scry'
        Invoke-Native 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scry 'install.ps1'), '-Alongside')
        Start-Process $node -ArgumentList ('"' + (Join-Path $scry 'start.ts') + '"') -WorkingDirectory $scry
    }

    Write-Step 'Starting Nexus'
    & $node (Join-Path $root 'start.ts')
} catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Read-Host 'The install stopped. Press Enter to close this window'
    exit 1
}
