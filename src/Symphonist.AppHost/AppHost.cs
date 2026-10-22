var builder = DistributedApplication.CreateBuilder(args);

// The host binds 127.0.0.1:<SYMPHONIST_PORT> itself (ignoring ASPNETCORE_URLS), so Aspire must not proxy its endpoint.
builder.AddProject<Projects.Symphonist_Host>("host")
    .WithEndpoint("http", e => e.IsProxied = false);

builder.Build().Run();
