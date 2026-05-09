var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.WeaveFleet_Api>("api")
    .WithHttpEndpoint(port: 5001, isProxied: false);

builder.AddViteApp("client", "../../client")
    .WithHttpEndpoint(port: 3002, env: "PORT", isProxied: false)
    .WithExternalHttpEndpoints();

builder.Build().Run();
