var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Symphonist_Host>("host");

builder.Build().Run();
