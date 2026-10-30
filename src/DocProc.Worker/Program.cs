using DocProc.Infrastructure.Persistence;
using DocProc.Worker;

var builder = Host.CreateApplicationBuilder(args);

// The API owns schema migration; the worker only connects.
builder.Services.TryAddDocProcPersistence(builder.Configuration, migrateByDefault: false);
builder.Services.AddHostedService<PipelineWorker>();

var host = builder.Build();
host.Run();
