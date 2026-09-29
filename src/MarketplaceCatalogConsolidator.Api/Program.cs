using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Parsing;
using MarketplaceCatalogConsolidator.Application.Uploads;
using MarketplaceCatalogConsolidator.Application.Workflow;
using MarketplaceCatalogConsolidator.Api.Endpoints;
using MarketplaceCatalogConsolidator.Api.Operations;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddApiHardening();
builder.Services.AddOpenApi(options => options.AddOperationTransformer(UploadEndpoints.TransformOpenApiAsync));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IStoragePaths>(_ => new FileSystemStoragePaths(new CatalogStorageOptions(
    builder.Configuration["Catalog:StorageRoot"],
    builder.Configuration["Catalog:StarterDatabasePath"])));
builder.Services.AddSingleton<SqliteConnectionFactory>(services =>
    new SqliteConnectionFactory(services.GetRequiredService<IStoragePaths>().WorkingDatabasePath));
builder.Services.AddSingleton<IWorkingDatabaseBootstrapper, SqliteWorkingDatabaseBootstrapper>();
builder.Services.AddSingleton<IDatabaseMigrator, SqliteDatabaseMigrator>();
builder.Services.AddSingleton<IWorkflowLock, FileSystemWorkflowLock>();
builder.Services.AddSingleton<IUploadStore, SqliteUploadStore>();
builder.Services.AddSingleton<IPublicReadStore, SqlitePublicReadStore>();
builder.Services.AddSingleton<IReadinessCheck, StorageReadinessCheck>();
builder.Services.AddSingleton<IUploadItemStore, SqliteUploadItemStore>();
builder.Services.AddSingleton<IConsolidationItemStore, SqliteConsolidationItemStore>();
builder.Services.AddSingleton<IProductCatalog, SqliteProductCatalog>();
builder.Services.AddSingleton<ISellerOfferStore, SqliteSellerOfferStore>();
builder.Services.AddSingleton<IStagingFileStore, FileSystemStagingFileStore>();
builder.Services.AddSingleton<IReportFileStore, FileSystemReportFileStore>();
builder.Services.AddSingleton<SourceDocumentParser>();
builder.Services.AddSingleton<IUploadItemProcessor, SourceCatalogItemProcessor>();
builder.Services.AddSingleton<UploadAcceptanceService>();
builder.Services.AddSingleton<UploadReportFinalizationService>();
builder.Services.AddSingleton<ConsolidationWorkflow>();
builder.Services.AddSingleton<StartupRecoveryService>();
builder.Services.AddSingleton(services => new ConsolidationPollingWorker(
    services.GetRequiredService<ConsolidationWorkflow>(),
    TimeSpan.FromMilliseconds(500)));
builder.Services.AddHostedService<ConsolidationHostedService>();

var app = builder.Build();

app.UseApiHardening();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi("/openapi/v1.json").RequireRateLimiting(ApiHardening.ReadPolicy);
app.MapUploadEndpoints();
app.MapPublicReadEndpoints();

var paths = app.Services.GetRequiredService<IStoragePaths>();
ApiHardening.ValidateRuntimeConfiguration(builder.Configuration, app.Environment, paths);
await app.Services.GetRequiredService<IWorkingDatabaseBootstrapper>().EnsureWorkingDatabaseAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(paths.WorkingDatabasePath, app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<StartupRecoveryService>().RecoverAsync(app.Lifetime.ApplicationStopping);

await app.RunAsync();

public partial class Program
{
}
