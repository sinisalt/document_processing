using System.Security.Claims;
using System.Threading.RateLimiting;
using DocProc.Infrastructure.Persistence.Tenancy;

namespace DocProc.Api.Endpoints;

/// <summary>
/// The limit on <c>POST /output-mappings:preview</c> (configuration section <c>Outputs:Preview</c>): a preview renders
/// in the renderer pool the API's tenants share (each render up to its time limit), so a tenant (or a platform
/// account) runs only a few at a time, as the table tool does (docs/11 §12.4), and gets 429 beyond its queue.
/// </summary>
public sealed class OutputPreviewOptions
{
    public const string Section = "Outputs:Preview";

    /// <summary>The rate limiting policy of the preview.</summary>
    public const string RateLimitPolicy = "output-preview";

    /// <summary>Previews running at once per tenant (or per platform account).</summary>
    public int ConcurrentPreviewsPerTenant { get; set; } = 2;

    /// <summary>Further previews that wait for a free slot; more answer 429.</summary>
    public int QueuedPreviewsPerTenant { get; set; } = 2;

    /// <summary>Registers the options and <see cref="RateLimitPolicy"/>: a concurrency limiter per tenant (per user in platform scope), 429 when its queue is full.</summary>
    public static IServiceCollection AddOutputPreviewLimits(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutputPreviewOptions>(configuration.GetSection(Section));
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicy, context =>
            {
                var options = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutputPreviewOptions>>().Value;
                var tenant = context.RequestServices.GetService<ITenantContext>();
                var key = tenant?.TenantId is { } tenantId
                    ? $"tenant:{tenantId:N}"
                    : $"user:{context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "anonymous"}";
                return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.ConcurrentPreviewsPerTenant),
                    QueueLimit = Math.Max(0, options.QueuedPreviewsPerTenant),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });
        return services;
    }
}
