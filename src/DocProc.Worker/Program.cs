using DocProc.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<PipelineWorker>();

var host = builder.Build();
host.Run();
