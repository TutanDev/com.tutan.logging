# Builds the generator, runs its tests, and copies it to Runtime/, where Unity applies it to
# Tutan.Logging and every assembly that references it. The build is deterministic, so a DLL that
# did not change is copied over itself unchanged.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

dotnet test (Join-Path $here 'Tutan.Logging.Generators.Tests') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'The generator tests failed, so the generator was not copied.' }

$built = Join-Path $here 'Tutan.Logging.Generators/bin/Release/netstandard2.0/Tutan.Logging.Generators.dll'
Copy-Item $built (Join-Path $here '../Runtime/Tutan.Logging.Generators.dll') -Force
Write-Host 'Copied Tutan.Logging.Generators.dll to Runtime/.'
