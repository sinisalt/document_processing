using System.Security.Claims;
using System.Threading.RateLimiting;
using DocProc.Infrastructure.Persistence.Tenancy;

namespace DocProc.Api.Documents;

/// <summary>
/// The limits on the table tool and region OCR (configuration section <c>TableTool</c>, docs/11 §12.3, §12.4): both run
/// in the API process every tenant shares, so one request is bounded in time, and a tenant (or a platform account) runs
/// only a few at a time.
/// </summary>
public sealed class TableToolOptions
{
    public const string Section = "TableTool";

    /// <summary>
    /// How long one <c>regions:table</c> read (the proposal, a preview, a write) may take in all, over every page and
    /// pattern; past it the request answers 422 <see cref="TableCapture.TooSlow"/>. Reading the 14 pages of the demo price
    /// list takes well under a second.
    /// </summary>
    public TimeSpan ReadBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Table reads and region OCR running at once per tenant (or per platform account).</summary>
    public int ConcurrentReadsPerTenant { get; set; } = 2;

    /// <summary>Further reads that wait for a free slot; more answer 429.</summary>
    public int QueuedReadsPerTenant { get; set; } = 4;

    /// <summary>
    /// Registers the options and the <see cref="TableCapture.RateLimitPolicy"/> policy: a concurrency limiter per tenant
    /// (from the tenant context the request resolved; per user in platform scope), 429 when its queue is full.
    /// </summary>
    public static IServiceCollection AddTableToolLimits(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TableToolOptions>(configuration.GetSection(Section));
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(TableCapture.RateLimitPolicy, context =>
            {
                var options = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<TableToolOptions>>().Value;
                var tenant = context.RequestServices.GetService<ITenantContext>();
                var key = tenant?.TenantId is { } tenantId
                    ? $"tenant:{tenantId:N}"
                    : $"user:{context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "anonymous"}";
                return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.ConcurrentReadsPerTenant),
                    QueueLimit = Math.Max(0, options.QueuedReadsPerTenant),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });
        return services;
    }
}
