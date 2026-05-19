// Use weave-fleet-dev.localhost so cookies are isolated from any
// installed instance running on plain localhost.
const string DevHost = "weave-fleet-dev.localhost";

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.WeaveFleet_Api>("api")
    .WithHttpEndpoint(port: 5001, isProxied: false);

builder.AddViteApp("client", "../../client")
    .WithHttpEndpoint(port: 3002, env: "PORT", isProxied: false)
    .WithUrls(c =>
    {
        foreach (var url in c.Urls)
        {
            if (url.Endpoint is not null)
            {
                var uri = new UriBuilder(url.Url) { Host = DevHost };
                url.Url = uri.ToString().TrimEnd('/');
            }
        }
    })
    .WithExternalHttpEndpoints();

builder.Build().Run();
