#!/usr/bin/env sh
set -eu

solution="MarketplaceCatalogConsolidator.sln"

dotnet restore "$solution"
dotnet build "$solution" --configuration Release --no-restore
dotnet test "$solution" --configuration Release --no-build
dotnet format "$solution" --verify-no-changes --no-restore