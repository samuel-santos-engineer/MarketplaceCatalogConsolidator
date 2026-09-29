using System.Globalization;
using System.Threading.RateLimiting;
using MarketplaceCatalogConsolidator.Api.Contracts;
using MarketplaceCatalogConsolidator.Application.Ports;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace MarketplaceCatalogConsolidator.Api.Operations;

public static class ApiHardening
{
    public const string UploadPolicy = "uploads";
    public const string ReadPolicy = "public-reads";
    public const int UploadPermits = 5;
    public const int ReadPermits = 120;
    public const int MaximumRequestBytes = 532_768;
    public const string DevelopmentPlaceholder = "development-only-not-a-secret";

    public static void AddApiHardening(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaximumRequestBytes;
        });
        builder.Services.AddExceptionHandler<SafeExceptionHandler>();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(UploadPolicy, context => Partition(context, UploadPermits));
            options.AddPolicy(ReadPolicy, context => Partition(context, ReadPermits));
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await WriteErrorAsync(context.HttpContext, 429, "rate_limit_exceeded", "Too many requests. Please retry later.", cancellationToken);
            };
        });
    }

    private static RateLimitPartition<string> Partition(HttpContext context, int permits)
    {
        var address = context.Connection.RemoteIpAddress;
        var key = address is null ? "unknown-peer" : address.MapToIPv6().ToString();
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    public static void ValidateRuntimeConfiguration(IConfiguration configuration, IHostEnvironment environment, IStoragePaths paths)
    {
        var usePlaceholder = configuration.GetValue<bool>("Development:UsePlaceholderApiKey");
        if (usePlaceholder && !environment.IsDevelopment())
            throw new InvalidOperationException("The development API-key convention is allowed only in Development.");
        if (usePlaceholder && string.IsNullOrWhiteSpace(configuration["Security:ApiKey"]))
            configuration["Security:ApiKey"] = DevelopmentPlaceholder;
        var apiKey = configuration["Security:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || (!environment.IsDevelopment() && apiKey == DevelopmentPlaceholder))
            throw new InvalidOperationException("Configure a non-empty upload API key through the runtime environment.");
        if (!File.Exists(paths.StarterDatabasePath)
            || string.Equals(paths.WorkingDatabasePath, paths.StarterDatabasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Configure a valid immutable starter database and a separate working storage root.");
        if (configuration.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED"))
            throw new InvalidOperationException("Automatic forwarded-header trust must be disabled; rate limits use the connection peer.");
    }

    public static void UseApiHardening(this WebApplication app)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            SuppressDiagnosticsCallback = _ => true,
            ExceptionHandler = context => WriteErrorAsync(context, 500, "internal_error", "The request could not be completed.", context.RequestAborted)
        });
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                if (context.Request.IsHttps) context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
                return Task.CompletedTask;
            });
            await next(context);
        });
        app.UseRouting();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/api/v1/uploads")
            {
                await next(context);
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaximumRequestBytes;
            if (context.Request.ContentLength > MaximumRequestBytes)
            {
                await WriteErrorAsync(context, 413, "payload_too_large", "The upload request is too large.", context.RequestAborted);
                return;
            }
            var original = context.Request.Body;
            await using var bounded = new BoundedRequestStream(original, MaximumRequestBytes);
            context.Request.Body = bounded;
            try { await next(context); }
            finally { context.Request.Body = original; }
        });
    }

    internal static Task WriteErrorAsync(HttpContext context, int status, string code, string message, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ApiErrorResponse(Guid.NewGuid().ToString("D"), code, message), cancellationToken);
    }
}

internal sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid().ToString("D");
        var tooLarge = exception is BadHttpRequestException { StatusCode: 413 };
        logger.LogError("Request failed with {ExceptionType}, trace {TraceId}", exception.GetType().Name, traceId);
        context.Response.StatusCode = tooLarge ? 413 : 500;
        await context.Response.WriteAsJsonAsync(new ApiErrorResponse(traceId, tooLarge ? "payload_too_large" : "internal_error",
            tooLarge ? "The upload request is too large." : "The request could not be completed."), cancellationToken);
        return true;
    }
}
