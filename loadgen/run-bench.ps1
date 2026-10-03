# Old vs new benchmark: each API version is exported from git, run against a throwaway PostgreSQL 18
# container, and driven by the NBomber robot fleet. Runs happen one at a time, never in parallel.
#
# Usage:  powershell -ExecutionPolicy Bypass -File loadgen\run-bench.ps1
#         powershell -ExecutionPolicy Bypass -File loadgen\run-bench.ps1 -Robots 10,50,100 -DurationSeconds 30 -Backlog 1000
param(
    [string]$Robots = '10,50,100',      # comma-separated, because -File passes every argument as one string
    [int]$DurationSeconds = 30,
    [int]$WarmupSeconds = 5,
    [int]$Backlog = 1000,
    [string]$Versions = 'old,new',
    [int]$Repeats = 1,
    [switch]$KeepDb
)

$robotCounts = $Robots.Split(',') | ForEach-Object { [int]$_.Trim() }
$versionList = $Versions.Split(',') | ForEach-Object { $_.Trim() }

$loadgen = $PSScriptRoot
$repo = Split-Path $loadgen -Parent
$work = Join-Path $loadgen '.work'
$logs = Join-Path $work 'logs'
$results = Join-Path $loadgen ('results\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))   # one folder per invocation, so runs never mix
$refs = @{ old = 'sit315'; new = 'phase-3' }   # old = the SIT331 code, new = Phases 1-3
$container = 'robotbench-pg'
$dbPort = 55432
$conn = "Host=localhost;Port=$dbPort;Database=robotbench;Username=postgres;Password=bench"
$hashKey = 'loadgen-bench-hash-key'
$apiUrl = 'http://localhost:5080'

function Assert-Ok([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

function Wait-Api([string]$url) {
    for ($i = 0; $i -lt 120; $i++) {
        try {
            Invoke-WebRequest -Uri "$url/api/adapter/devices/1001/work-items/claim-next" -Method Post -Body '{}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 2 | Out-Null
            return
        }
        catch {
            if ($_.Exception.Response) { return }   # a 401 still means the API is up
        }
        Start-Sleep -Milliseconds 500
    }
    throw "API at $url did not start"
}

New-Item -ItemType Directory -Force $work, $logs, $results | Out-Null

# The machine and the exact code under test, recorded next to the numbers they produced.
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$os = Get-CimInstance Win32_OperatingSystem
@(
    "started:     $(Get-Date -Format o)"
    "machine:     $($cpu.Name.Trim()), $($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads, $ramGb GB RAM, $($os.Caption) $($os.Version)"
    "database:    postgres:18-alpine in Docker, max_connections=300, same machine"
    "generator:   NBomber, same machine, closed loop (each robot waits for every reply)"
    "parameters:  robots=$Robots duration=${DurationSeconds}s warmup=${WarmupSeconds}s backlog=$Backlog jobs/robot repeats=$Repeats versions=$Versions"
    "old commit:  $(git -C $repo rev-parse --short $refs['old']) ($($refs['old']))"
    "new commit:  $(git -C $repo rev-parse --short $refs['new']) ($($refs['new']))"
    "loadgen:     $(git -C $repo rev-parse --short HEAD) ($(git -C $repo branch --show-current)), uncommitted changes: $([bool](git -C $repo status --porcelain))"
) | Set-Content -Encoding utf8 (Join-Path $results 'run-info.txt')

# 1. Build the load generator.
dotnet build "$loadgen\RobotLoadGen\RobotLoadGen.csproj" -c Release -nologo -v quiet | Out-Host
Assert-Ok 'load generator build'
$gen = "$loadgen\RobotLoadGen\bin\Release\net8.0\RobotLoadGen.dll"

# 2. Export each API version straight from git and publish it, so the old run uses exactly the old code.
foreach ($v in $versionList) {
    $zip = Join-Path $work "$v.zip"
    $src = Join-Path $work "$v-src"
    $out = Join-Path $work "$v-api"
    Remove-Item -Recurse -Force $zip, $src, $out -ErrorAction SilentlyContinue
    git -C $repo archive --format=zip -o $zip $refs[$v]
    Assert-Ok "git archive $($refs[$v])"
    Expand-Archive -Path $zip -DestinationPath $src
    dotnet publish "$src\RobotControllerApi.csproj" -c Release -o $out -nologo -v quiet | Out-File -Encoding utf8 (Join-Path $logs "publish-$v.log")
    Assert-Ok "publish $v (see $logs\publish-$v.log)"
}

# 3. A fresh PostgreSQL 18 container (same image as the tests), loaded with the repo's own schema.
docker rm -f $container *> $null
docker run -d --name $container -e POSTGRES_PASSWORD=bench -e POSTGRES_DB=robotbench -p "${dbPort}:5432" postgres:18-alpine -c max_connections=300 | Out-Null
Assert-Ok 'docker run'
dotnet $gen init --db $conn --repo $repo
Assert-Ok 'db init'

# 4. Settings every API run gets. Production means no user-secrets, so your Final_project DB is never used.
$apiEnv = @{
    ASPNETCORE_ENVIRONMENT               = 'Production'
    ASPNETCORE_URLS                      = $apiUrl
    ConnectionStrings__DefaultConnection = $conn
    DeviceCredential__HashKey            = $hashKey
    Persistence__Provider                = 'ADO'
    RequestLogging__Enabled              = 'false'     # it prints every request body to the console, which would swamp the timings
    Logging__LogLevel__Default           = 'Warning'
}
$saved = @{}
foreach ($k in $apiEnv.Keys) {
    $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
    [Environment]::SetEnvironmentVariable($k, $apiEnv[$k], 'Process')
}

try {
    for ($r = 1; $r -le $Repeats; $r++) {
        foreach ($n in $robotCounts) {
            foreach ($v in $versionList) {
                $tag = "$v-$n-robots-r$r"
                Write-Host "=== $tag ==="

                dotnet $gen seed --db $conn --hash-key $hashKey --robots $n --backlog $Backlog
                Assert-Ok "seed $tag"

                $apiArgs = @{
                    FilePath               = "$work\$v-api\RobotControllerApi.exe"
                    WorkingDirectory       = "$work\$v-api"
                    NoNewWindow            = $true
                    PassThru               = $true
                    RedirectStandardOutput = "$logs\api-$tag.out.log"
                    RedirectStandardError  = "$logs\api-$tag.err.log"
                }
                $api = Start-Process @apiArgs
                try {
                    Wait-Api $apiUrl
                    $genArgs = @{
                        FilePath               = 'dotnet'
                        ArgumentList           = @("`"$gen`"", 'run', '--api', $apiUrl, '--robots', $n, '--duration', $DurationSeconds, '--warmup', $WarmupSeconds, '--label', $v, '--out', "`"$results\bench.csv`"")   # quoted by hand: Start-Process does not quote paths with spaces
                        WorkingDirectory       = "$loadgen\RobotLoadGen"
                        NoNewWindow            = $true
                        PassThru               = $true
                        Wait                   = $true
                        RedirectStandardOutput = "$logs\gen-$tag.log"
                        RedirectStandardError  = "$logs\gen-$tag.err.log"
                    }
                    $genRun = Start-Process @genArgs
                    if ($genRun.ExitCode -ne 0) { throw "load generator failed for $tag, see $logs\gen-$tag.log" }
                }
                finally {
                    Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
                    $api.WaitForExit()
                }

                dotnet $gen check --db $conn --label $v --robots $n --out "$results\db_check.csv"
                Assert-Ok "check $tag"
            }
        }
    }

    dotnet $gen summary --results $results
}
finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
    if (-not $KeepDb) { docker rm -f $container *> $null }
}
