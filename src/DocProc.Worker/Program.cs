using DocProc.Infrastructure.Blobs;
using DocProc.Infrastructure.Persistence;
using DocProc.ServiceDefaults;
using DocProc.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The API owns schema migration; the worker only connects.
builder.Services.TryAddDocProcPersistence(builder.Configuration, migrateByDefault: false);

// Same blob store as the API: Azure/Azurite when the AppHost injects ConnectionStrings:blobs, else ./data/blobs
// under the content root. Both hosts must resolve to the same store or the worker cannot read what the API ingested.
builder.Services.AddDocProcBlobs(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<PipelineWorker>();

var host = builder.Build();
host.Run();
