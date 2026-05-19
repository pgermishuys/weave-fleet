// Standalone entry point for running FakeLlmServer as a process (for manual testing).
// In tests, use FakeLlmServerFixture.StartAsync() instead.
using FakeLlmServer;

var queue = new ScriptedResponseStore();

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton(queue);

var app = builder.Build();
app.MapOpenAiEndpoints();

await app.RunAsync();
