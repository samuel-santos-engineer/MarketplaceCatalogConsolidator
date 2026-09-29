FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore MarketplaceCatalogConsolidator.sln
RUN dotnet publish src/MarketplaceCatalogConsolidator.Api/MarketplaceCatalogConsolidator.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir -p /home/data/uploads /home/data/reports \
    && chown -R 10001:10001 /home/data
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 10001:10001
ENTRYPOINT ["dotnet", "MarketplaceCatalogConsolidator.Api.dll"]