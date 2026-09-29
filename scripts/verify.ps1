$ErrorActionPreference = 'Stop'
$solution = 'MarketplaceCatalogConsolidator.sln'

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $solution --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test $solution --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet format $solution --verify-no-changes --no-restore
exit $LASTEXITCODE