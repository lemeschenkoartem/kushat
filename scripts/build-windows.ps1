param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
dotnet test tests/Kushat.Core.Tests/Kushat.Core.Tests.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Проверки не прошли." }
dotnet test tests/Kushat.Windows.Tests/Kushat.Windows.Tests.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Проверки Windows не прошли." }
dotnet publish src/Kushat.App/Kushat.App.csproj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o "artifacts/reference-$Runtime" --nologo
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки." }
Copy-Item README.md "artifacts/reference-$Runtime/README.md"
Copy-Item examples "artifacts/reference-$Runtime/examples" -Recurse -Force
Copy-Item docs "artifacts/reference-$Runtime/docs" -Recurse -Force
[xml]$project = Get-Content src/Kushat.App/Kushat.App.csproj -Raw
$version = [string]$project.Project.PropertyGroup.Version
$versionedZip = "artifacts/Kushat-$version-$Runtime.zip"
Compress-Archive -Path "artifacts/reference-$Runtime/*" -DestinationPath $versionedZip -Force
Copy-Item $versionedZip "artifacts/Kushat-reference-$Runtime.zip" -Force
Write-Host "Готово: $versionedZip"
