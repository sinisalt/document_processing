using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;

namespace DocProc.AppHost.Tests;

/// <summary>
/// <c>docker-compose.yml</c> is the second way to run the stack (README "With Docker Compose"). The AppHost stays the
/// single source of truth: these checks build its application model (nothing starts) and hold the compose file and the
/// Service Bus emulator's <c>docker/servicebus/Config.json</c> to it, and the published ports to the Keycloak realm's
/// redirect URIs and the web apps' <c>.env.production</c>, so neither file drifts when the AppHost changes.
/// </summary>
public sealed class ComposeParityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Service_bus_emulator_config_declares_exactly_the_apphost_entities()
    {
        var model = await BuildModelAsync();
        var queues = model.Resources.OfType<AzureServiceBusQueueResource>().ToList();
        var topics = model.Resources.OfType<AzureServiceBusTopicResource>().ToList();
        var subscriptions = model.Resources.OfType<AzureServiceBusSubscriptionResource>().ToList();
        queues.ShouldNotBeEmpty();

        using var config = JsonDocument.Parse(Read("docker", "servicebus", "Config.json"));
        var namespaces = config.RootElement.GetProperty("UserConfig").GetProperty("Namespaces").EnumerateArray().ToList();
        namespaces.Count.ShouldBe(1);
        var ns = namespaces[0];

        Names(ns, "Queues").ShouldBe(queues.Select(q => q.QueueName), ignoreOrder: true);
        Names(ns, "Topics").ShouldBe(topics.Select(t => t.TopicName), ignoreOrder: true);
        foreach (var topic in ns.GetProperty("Topics").EnumerateArray())
        {
            var name = topic.GetProperty("Name").GetString();
            Names(topic, "Subscriptions").ShouldBe(subscriptions.Where(s => s.Parent.TopicName == name).Select(s => s.SubscriptionName), ignoreOrder: true, $"subscriptions of {name}");
        }

        // The AppHost sets no entity properties (lock duration, delivery count, ...), so the config leaves the emulator's defaults too.
        queues.ShouldAllBe(q => q.LockDuration == null && q.MaxDeliveryCount == null && q.RequiresSession == null && q.DefaultMessageTimeToLive == null);
        ns.GetProperty("Queues").EnumerateArray().Where(q => q.TryGetProperty("Properties", out var _)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Compose_runs_the_apphost_images()
    {
        var model = await BuildModelAsync();
        var compose = Compose();

        string AppHostImage(string resource)
        {
            var image = model.Resources.Single(r => r.Name == resource).Annotations.OfType<ContainerImageAnnotation>().Last();
            return $"{(image.Registry is { Length: > 0 } registry ? registry + "/" : string.Empty)}{image.Image}:{image.Tag}";
        }

        Image(compose, "sqlserver").ShouldBe(AppHostImage("sql"));
        Image(compose, "azurite").ShouldBe(AppHostImage("storage"));
        Image(compose, "servicebus").ShouldBe(AppHostImage("messaging"));
        Image(compose, "keycloak").ShouldBe(AppHostImage("keycloak"));
        Image(compose, "vault").ShouldEndWith(AppHostImage("vault"));
        Image(compose, "mail").ShouldEndWith(AppHostImage("mail"));
    }

    [Theory]
    [InlineData("vault", "LOWKEY_ARGS")]
    [InlineData("mail", "GREENMAIL_OPTS")]
    public async Task Compose_configures_the_plain_containers_like_the_apphost(string service, string variable)
    {
        var appHost = await EnvironmentOfAsync(service);

        Environment(Compose(), service)[variable].ShouldBe(appHost[variable] as string);
    }

    /// <summary>
    /// The hosts read the keys the AppHost sets (docs/09 "Settings reference"); compose must set every one of them, so a
    /// setting added to the AppHost for the API or the worker is not silently missing from the compose stack.
    /// </summary>
    [Theory]
    [InlineData("api")]
    [InlineData("worker")]
    public async Task Compose_sets_every_setting_the_apphost_gives_a_host(string host)
    {
        var appHost = await EnvironmentOfAsync(host);
        var expected = appHost.Keys
            .Where(key => key.Contains("__", StringComparison.Ordinal) && !IsAspirePlumbing(key))
            .ToList();
        expected.ShouldContain("ConnectionStrings__DocProc");
        expected.ShouldContain("Persistence__Provider");

        var compose = Environment(Compose(), host);

        expected.Where(key => !compose.ContainsKey(key)).ShouldBeEmpty($"docker-compose.yml does not set these AppHost settings for {host}");
        compose["Persistence__Provider"].ShouldBe(appHost["Persistence__Provider"] as string);
        compose["Authentication__Dev__Enabled"].ShouldBe(appHost["Authentication__Dev__Enabled"] as string);
        compose["Secrets__Path"].ShouldBe(Environment(Compose(), host == "api" ? "worker" : "api")["Secrets__Path"], "both hosts share one secret store");
    }

    [Fact]
    public void Keycloak_imports_the_apphost_realm_and_publishes_the_issuer_the_apps_and_the_api_use()
    {
        var compose = Compose();
        var keycloak = Service(compose, "keycloak");

        Volumes(keycloak).ShouldContain(v => v.StartsWith("./src/DocProc.AppHost/realms:/opt/keycloak/data/import", StringComparison.Ordinal));
        Directory.GetFiles(RepositoryPath("src", "DocProc.AppHost", "realms"), "*.json").ShouldNotBeEmpty();
        PublishedPort(keycloak, 8080).ShouldBe(8080);

        var issuer = $"{Environment(compose, "keycloak")["KC_HOSTNAME"]}/realms/{Realm().GetProperty("realm").GetString()}";
        issuer.ShouldBe("http://localhost:8080/realms/docproc");
        Environment(compose, "api")["Authentication__Oidc__Authority"].ShouldBe(issuer);
        Environment(compose, "api")["Authentication__Oidc__MetadataAddress"].ShouldBe("http://keycloak:8080/realms/docproc/.well-known/openid-configuration");
        foreach (var app in new[] { "customer-portal", "admin-console" })
        {
            DotEnv("web", "apps", app, ".env.production")["VITE_OIDC_AUTHORITY"].ShouldBe(issuer, app);
        }
    }

    [Theory]
    [InlineData("portal", "customer-portal")]
    [InlineData("admin", "admin-console")]
    public void Each_app_is_published_on_the_port_its_realm_client_redirects_to(string service, string app)
    {
        var compose = Compose();
        var definition = Service(compose, service);
        ((YamlMappingNode)definition["build"])["args"].ShouldBeOfType<YamlMappingNode>()["APP"].ToString().ShouldBe(app);

        // The unprivileged nginx of web/Dockerfile listens on 8080 inside the container.
        var origin = $"http://localhost:{PublishedPort(definition, 8080)}";
        var clientId = DotEnv("web", "apps", app, ".env.production")["VITE_OIDC_CLIENT_ID"];
        var client = Realm().GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == clientId);

        client.GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString()).ShouldContain($"{origin}/*");
        client.GetProperty("webOrigins").EnumerateArray().Select(u => u.GetString()).ShouldContain(origin);
    }

    /// <summary>
    /// The apps' Content-Security-Policy (web/nginx.conf.template) allows calls to and frames of <c>OIDC_ORIGIN</c> only;
    /// an authority on another origin would make sign-in fail in the browser with a CSP violation.
    /// </summary>
    [Fact]
    public void The_web_images_allow_the_oidc_authority_of_both_apps_in_their_content_security_policy()
    {
        var match = Regex.Match(Read("web", "Dockerfile"), @"OIDC_ORIGIN=(\S+)");
        match.Success.ShouldBeTrue("web/Dockerfile sets OIDC_ORIGIN");
        var template = Read("web", "nginx.conf.template");
        template.ShouldContain("connect-src 'self' ${OIDC_ORIGIN}");
        template.ShouldContain("frame-src 'self' ${OIDC_ORIGIN}");

        foreach (var app in new[] { "customer-portal", "admin-console" })
        {
            var authority = new Uri(DotEnv("web", "apps", app, ".env.production")["VITE_OIDC_AUTHORITY"]);
            match.Groups[1].Value.ShouldBe(authority.GetLeftPart(UriPartial.Authority), app);
        }
    }

    [Fact]
    public void Compose_needs_nothing_outside_the_project()
    {
        var compose = Compose();

        foreach (var section in new[] { "networks", "volumes" })
        {
            if (compose.Children.TryGetValue(new YamlScalarNode(section), out var node) && node is YamlMappingNode entries)
            {
                entries.Children.Values.OfType<YamlMappingNode>()
                    .ShouldAllBe(e => !e.Children.ContainsKey(new YamlScalarNode("external")), $"an external {section} entry needs something another project created");
            }
        }

        foreach (var (name, definition) in Services(compose))
        {
            if (!definition.Children.TryGetValue(new YamlScalarNode("ports"), out var ports))
            {
                continue;
            }

            ((YamlSequenceNode)ports).Children.Select(p => p.ToString())
                .ShouldAllBe(p => p.StartsWith("${DOCPROC_BIND_ADDRESS:-127.0.0.1}:", StringComparison.Ordinal), $"{name} publishes a port on every interface");
        }
    }

    /// <summary>
    /// A container that exits while the stack starts (SQL Server's first boot times out on a busy machine and exits 1)
    /// fails the whole <c>docker compose up</c> with "dependency failed to start" unless compose restarts it. So every
    /// long-running service has a restart policy; only the one-shot helpers, which others wait for to complete, have
    /// <c>restart: "no"</c>.
    /// </summary>
    [Fact]
    public void Every_long_running_service_is_restarted_when_it_exits()
    {
        var compose = Compose();
        var services = Services(compose).ToList();
        var oneShots = services
            .SelectMany(s => s.Definition.Children.TryGetValue(new YamlScalarNode("depends_on"), out var deps)
                ? ((YamlMappingNode)deps).Children
                : Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>())
            .Where(d => ((YamlMappingNode)d.Value)["condition"].ToString() == "service_completed_successfully")
            .Select(d => d.Key.ToString())
            .ToHashSet(StringComparer.Ordinal);
        oneShots.ShouldBe(["servicebus-ready", "llm-setup"], ignoreOrder: true);

        foreach (var (name, definition) in services)
        {
            definition.Children.TryGetValue(new YamlScalarNode("restart"), out var restart).ShouldBeTrue($"{name} has no restart policy");
            if (oneShots.Contains(name))
            {
                restart!.ToString().ShouldBe("no", $"{name} is a one-shot helper");
            }
            else
            {
                restart!.ToString().ShouldBeOneOf(["unless-stopped", "on-failure", "always"], $"{name} must come back when it exits");
            }
        }
    }

    [Theory]
    [InlineData("api")]
    [InlineData("worker")]
    public void Hosts_do_not_log_every_sql_statement(string host) =>
        Environment(Compose(), host)["Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command"].ShouldBe("Warning");

    private static bool IsAspirePlumbing(string key) =>
        key.StartsWith("OTEL_", StringComparison.Ordinal)
        || key.StartsWith("ASPNETCORE_", StringComparison.Ordinal)
        || key.StartsWith("DOTNET_", StringComparison.Ordinal)
        || key.StartsWith("services__", StringComparison.Ordinal)
        || key.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase);

    private static async Task<DistributedApplicationModel> BuildModelAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DocProc_AppHost>(Ct);
        var app = await builder.BuildAsync(Ct);
        return app.Services.GetRequiredService<DistributedApplicationModel>();
    }

    /// <summary>The environment the AppHost gives a resource, evaluated without starting anything (values may be expressions).</summary>
    private static async Task<Dictionary<string, object>> EnvironmentOfAsync(string name)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DocProc_AppHost>(Ct);
        await using var app = await builder.BuildAsync(Ct);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();

        var resource = (IResourceWithEnvironment)model.Resources.Single(r => r.Name == name);
        var context = new EnvironmentCallbackContext(executionContext, resource, cancellationToken: Ct);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return new Dictionary<string, object>(context.EnvironmentVariables, StringComparer.Ordinal);
    }

    private static IEnumerable<string?> Names(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var items) ? items.EnumerateArray().Select(i => i.GetProperty("Name").GetString()) : [];

    private static YamlMappingNode Compose()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(Read("docker-compose.yml"));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static IEnumerable<(string Name, YamlMappingNode Definition)> Services(YamlMappingNode compose) =>
        ((YamlMappingNode)compose["services"]).Children.Select(e => (e.Key.ToString(), (YamlMappingNode)e.Value));

    private static YamlMappingNode Service(YamlMappingNode compose, string name) => (YamlMappingNode)((YamlMappingNode)compose["services"])[name];

    private static string Image(YamlMappingNode compose, string service) => Service(compose, service)["image"].ToString();

    private static List<string> Volumes(YamlMappingNode service) => ((YamlSequenceNode)service["volumes"]).Children.Select(v => v.ToString()).ToList();

    /// <summary>The host port a service publishes for <paramref name="containerPort"/> (<c>[address:]host:container</c>).</summary>
    private static int PublishedPort(YamlMappingNode service, int containerPort)
    {
        var mapping = ((YamlSequenceNode)service["ports"]).Children.Select(p => p.ToString().Split(':'))
            .Single(parts => parts[^1] == containerPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return int.Parse(mapping[^2], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A service's environment with YAML merge keys (<c>&lt;&lt;: *anchor</c>) applied, the way compose reads it.</summary>
    private static Dictionary<string, string> Environment(YamlMappingNode compose, string service)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Merge((YamlMappingNode)Service(compose, service)["environment"], result);
        return result;

        static void Merge(YamlMappingNode mapping, Dictionary<string, string> into)
        {
            foreach (var (key, value) in mapping.Children)
            {
                if (key.ToString() == "<<")
                {
                    var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                    Merge((YamlMappingNode)value, merged);
                    foreach (var (k, v) in merged)
                    {
                        into.TryAdd(k, v);
                    }
                }
                else
                {
                    into[key.ToString()] = value.ToString();
                }
            }
        }
    }

    private static JsonElement Realm()
    {
        using var doc = JsonDocument.Parse(Read("src", "DocProc.AppHost", "realms", "docproc-realm.json"));
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> DotEnv(params string[] path) =>
        Read(path).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('=', StringComparison.Ordinal))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

    private static string Read(params string[] path) => File.ReadAllText(RepositoryPath(path)).ReplaceLineEndings("\n");

    private static string RepositoryPath(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocProc.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (DocProc.slnx)."), .. path]);
    }
}
