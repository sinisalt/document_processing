using DocProc.Infrastructure.Persistence;
using DocProc.Pipeline;
using DocProc.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The API owns schema migration; the worker only connects. Same provider switch as the API (docs/09 "Running without
// Docker"), so Persistence:Provider applies to both hosts or to neither.
builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false);

// The processing stack (docs/03 "Implementation"): PDF, classification, e-invoice parsing, the LLM gateway, the same
// blob store as the API (Azure/Azurite when the AppHost injects ConnectionStrings:blobs, else ./data/blobs under the
// content root) and the pipeline runner. The worker exists to run stages, so it runs them unless Pipeline:RunInProcess
// says otherwise; the Service Bus transport that replaces the in-process queue in Azure is a later block.
builder.AddDocProcProcessingStack(runInProcessDefault: true);

var host = builder.Build();
host.Run();
