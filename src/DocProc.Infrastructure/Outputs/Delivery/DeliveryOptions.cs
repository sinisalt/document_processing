namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>
/// The <c>Delivery</c> configuration section (docs/11 §13.3, §13.6): retries, the delivery sweeper, the file drop's
/// root and the development-only allowance for the demos' <c>mock-erp</c>.
/// </summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>
    /// The waits between attempts (docs/11 §13.6: 1 min, 5 min, 15 min, 1 h, 3 h, 6 h, then every 12 h): the wait after
    /// attempt n is entry n, the last entry repeats. Each wait gets up to 10 % jitter. A <c>Retry-After</c> of the
    /// receiver replaces the wait (at least 1 s, at most <see cref="MaxRetryAfter"/>). Empty: the default schedule.
    /// </summary>
    public List<TimeSpan> RetrySchedule { get; set; } = [];

    /// <summary>Attempts before a retryable failure is dead-lettered (docs/11 §13.6: 15).</summary>
    public int MaxAttempts { get; set; } = 15;

    /// <summary>How long after its creation a delivery is still retried (docs/11 §13.6: 72 h).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(72);

    /// <summary>The longest <c>Retry-After</c> honoured.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// How often the delivery sweeper looks for deliveries whose message was lost (a crash between the fan-out's
    /// commit and its nudge, a retry waiting in a process that ended). The first sweep runs one interval after start.
    /// Zero turns it off.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long past its due time a delivery is left to its own message before the sweeper queues it, so the normal
    /// path is not doubled. A fan-out's delivery is due at once, a retry at its backoff.
    /// </summary>
    public TimeSpan SweepGrace { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The most deliveries one sweep queues.</summary>
    public int SweepBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a delivery being sent is held by the attempt that claimed it, on top of the transport's timeout; a
    /// process that dies mid-send leaves the row to the sweeper once this has passed.
    /// </summary>
    public TimeSpan SendLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The root folder of the file drop (<c>blob</c> transport); a relative path is under the host's content root.
    /// Each tenant writes below <c>{root}/{tenantId}</c>.
    /// </summary>
    public string DropRoot { get; set; } = "data/outbound";

    /// <summary>
    /// Development only (docs/11 §13.3): <c>host</c> or <c>host:port</c> entries an http destination may reach over
    /// plain http at a private address, past <c>TenantEndpointGuard</c> (the demos' <c>mock-erp</c>:
    /// <c>mock-erp:8080</c> in compose, <c>localhost:8086</c> under the AppHost). Ignored outside the Development
    /// environment, where the guard applies unchanged.
    /// </summary>
    public List<string> DevelopmentHosts { get; set; } = [];

    /// <summary>The default schedule of docs/11 §13.6.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetrySchedule =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
        TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
    ];

    /// <summary>The schedule in force: <see cref="RetrySchedule"/>, else <see cref="DefaultRetrySchedule"/>.</summary>
    public IReadOnlyList<TimeSpan> EffectiveRetrySchedule => RetrySchedule is { Count: > 0 } schedule ? schedule : DefaultRetrySchedule;
}

/// <summary>
/// Which hosts a delivery may reach past <c>TenantEndpointGuard</c>: none, except the
/// <see cref="DeliveryOptions.DevelopmentHosts"/> of a Development host. Registered once per host; tests register
/// their own (a fake receiver on loopback).
/// </summary>
public sealed class DeliveryEndpointPolicy
{
    private readonly HashSet<string> _hosts;

    public DeliveryEndpointPolicy(IEnumerable<string> exemptHosts)
    {
        ArgumentNullException.ThrowIfNull(exemptHosts);
        _hosts = new HashSet<string>(exemptHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0), StringComparer.Ordinal);
    }

    /// <summary>No exemptions: the guard applies to every call.</summary>
    public static DeliveryEndpointPolicy Strict { get; } = new([]);

    /// <summary>Whether <paramref name="host"/> (on <paramref name="port"/>) may be reached over http at any address.</summary>
    public bool IsExempt(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length > 1 && name[0] == '[' && name[^1] == ']')
        {
            name = name[1..^1];
        }

        return _hosts.Contains(name) || _hosts.Contains($"{name}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    public bool IsExempt(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return IsExempt(uri.Host, uri.Port);
    }
}
