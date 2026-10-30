// Local orchestration for a developer machine (docs/09). One `dotnet run` starts every dependency as a container,
// the API and the worker, and the Aspire dashboard with traces, logs and metrics. Nothing here is deployed to Azure;
// Bicep owns production. Resource names are the connection-string names the hosts read.

var builder = DistributedApplication.CreateBuilder(args);

// `aspire publish` can emit a docker-compose file for people who prefer compose over the AppHost.
builder.AddDockerComposeEnvironment("compose");

// SQL Server 2025 (amd64 only; Rosetta on Apple Silicon). The database resource name is the connection string key.
var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sql = builder.AddSqlServer("sql", sqlPassword, port: 1433)
    .WithImageRegistry("mcr.microsoft.com")
    .WithImage("mssql/server")
    .WithImageTag("2025-latest")
    .WithDataVolume("docproc-sql-data")
    .WithLifetime(ContainerLifetime.Persistent);
var database = sql.AddDatabase("DocProc");

// Azure Blob Storage → Azurite. Containers mirror docs/01: immutable raw documents, derived artefacts, exports.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(azurite => azurite
        .WithDataVolume("docproc-azurite-data")
        .WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("blobs");
storage.AddBlobContainer("raw-documents", "raw");
storage.AddBlobContainer("derived", "derived");
storage.AddBlobContainer("exports", "exports");

// Azure Service Bus → official emulator (brings its own SQL Server container). One queue per pipeline stage (docs/03).
var messaging = builder.AddAzureServiceBus("messaging")
    .RunAsEmulator(emulator => emulator.WithLifetime(ContainerLifetime.Persistent));
foreach (var stage in new[] { "ingest", "normalize", "split", "classify", "match", "extract", "validate", "publish", "synthesize-profile" })
{
    messaging.AddServiceBusQueue(stage);
}

messaging.AddServiceBusTopic("document-events").AddServiceBusSubscription("webhooks");

// Keycloak as the local OIDC provider with a seeded realm: platform staff and two tenants (docs/09 "Identity").
var keycloak = builder.AddKeycloak("keycloak", port: 8080)
    .WithDataVolume("docproc-keycloak-data")
    .WithRealmImport("./realms")
    .WithLifetime(ContainerLifetime.Persistent);

// Key Vault → Lowkey Vault for the BYOK secret flow (docs/08 "Key handling"). Same SecretClient calls as Azure.
var vault = builder.AddContainer("vault", "nagyesta/lowkey-vault", "7.3.127")
    .WithHttpsEndpoint(port: 8443, targetPort: 8443, name: "https")
    .WithEnvironment("LOWKEY_ARGS", "--LOWKEY_VAULT_NAMES=docproc --LOWKEY_VAULT_RELAXED_PORTS=true")
    .WithLifetime(ContainerLifetime.Persistent);

// GreenMail: SMTP + IMAP for end-to-end runs where a test sends an email with attachments (docs/09 "Email").
var mail = builder.AddContainer("mail", "greenmail/standalone", "2.1.15")
    .WithEndpoint(port: 3025, targetPort: 3025, name: "smtp", scheme: "tcp")
    .WithEndpoint(port: 3143, targetPort: 3143, name: "imap", scheme: "tcp")
    .WithHttpEndpoint(port: 8085, targetPort: 8080, name: "api")
    .WithEnvironment("GREENMAIL_OPTS", "-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled -Dgreenmail.users=invoices@tenant-a.local,invoices@tenant-b.local")
    .WithLifetime(ContainerLifetime.Persistent);

var api = builder.AddProject<Projects.DocProc_Api>("api")
    .WithReference(database).WaitFor(database)
    .WithReference(blobs).WaitFor(storage)
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(keycloak).WaitFor(keycloak)
    .WithEnvironment("Persistence__MigrateOnStartup", "true")
    .WithEnvironment("Authentication__Oidc__Authority", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/docproc"))
    .WithEnvironment("Authentication__Oidc__RequireHttpsMetadata", "false")
    .WithEnvironment("KeyVault__Endpoint", vault.GetEndpoint("https"))
    .WithHttpHealthCheck("/health/ready")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.DocProc_Worker>("worker")
    .WithReference(database)
    .WithReference(blobs)
    .WithReference(messaging)
    .WithEnvironment("KeyVault__Endpoint", vault.GetEndpoint("https"))
    .WithEnvironment("Mail__ImapHost", mail.GetEndpoint("imap").Property(EndpointProperty.Host))
    .WithEnvironment("Mail__ImapPort", mail.GetEndpoint("imap").Property(EndpointProperty.Port))
    .WaitFor(api); // the API applies migrations first

builder.Build().Run();
