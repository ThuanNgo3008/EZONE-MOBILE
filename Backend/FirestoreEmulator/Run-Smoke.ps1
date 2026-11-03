# Runs only the isolated test project; never production Program.Main.
$ErrorActionPreference = 'Stop'
$names = @('EZONE_TEST_PROJECT_ID', 'FIRESTORE_EMULATOR_HOST',
    'GOOGLE_APPLICATION_CREDENTIALS', 'FIREBASE_TOKEN', 'CI', 'FIREBASE_CLI_DISABLE_TELEMETRY')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$previousDirectory = Get-Location
try {
    $env:EZONE_TEST_PROJECT_ID = 'demo-ezone-regression'
    $env:FIRESTORE_EMULATOR_HOST = '127.0.0.1:8085'
    $env:GOOGLE_APPLICATION_CREDENTIALS = $null
    $env:FIREBASE_TOKEN = $null
    $env:CI = 'true'
    $env:FIREBASE_CLI_DISABLE_TELEMETRY = 'true'
    Set-Location -LiteralPath $PSScriptRoot
    $cli = Join-Path $PSScriptRoot 'node_modules/firebase-tools/lib/bin/firebase.js'
    if (-not (Test-Path -LiteralPath $cli)) { throw 'Run npm ci in Backend/FirestoreEmulator first.' }
    & node $cli emulators:exec --only firestore --project demo-ezone-regression --config firebase.json `
        'dotnet test ../WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj --no-build --no-restore --nologo -v:minimal'
    if ($LASTEXITCODE -ne 0) { throw "Emulator verification failed with exit code $LASTEXITCODE." }
}
finally {
    Set-Location -LiteralPath $previousDirectory.Path
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
