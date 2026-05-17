using Microsoft.Extensions.DependencyInjection;
using NuCode.Plugins;

namespace NuCode;

public sealed class PluginTests
{
    // --- Task 37: Hook abstractions ---

    [Fact]
    public void HookCollectionRegistersAndRetrievesHandlers()
    {
        var hook = BuiltInHooks.SystemPromptTransform;
        var collection = new NuCodeHookCollection();
        var called = false;

        collection.On(hook, (input, output) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var handlers = collection.GetHandlers(hook);

        handlers.ShouldHaveSingleItem();
        // Verify handler hasn't been called yet (just registered)
        called.ShouldBeFalse();
    }

    [Fact]
    public void HookCollectionReturnsEmptyForUnregisteredHook()
    {
        var collection = new NuCodeHookCollection();
        var handlers = collection.GetHandlers(BuiltInHooks.SystemPromptTransform);

        handlers.ShouldBeEmpty();
    }

    [Fact]
    public void HookCollectionSupportsMultipleHandlersPerHook()
    {
        var hook = BuiltInHooks.BeforeToolCall;
        var collection = new NuCodeHookCollection();
        var callOrder = new List<int>();

        collection
            .On(hook, (_, _) => { callOrder.Add(1); return Task.CompletedTask; })
            .On(hook, (_, _) => { callOrder.Add(2); return Task.CompletedTask; })
            .On(hook, (_, _) => { callOrder.Add(3); return Task.CompletedTask; });

        var handlers = collection.GetHandlers(hook);
        handlers.Count.ShouldBe(3);
    }

    [Fact]
    public void HookCollectionIsolatesDifferentHooks()
    {
        var collection = new NuCodeHookCollection();

        collection.On(BuiltInHooks.SystemPromptTransform, (_, _) => Task.CompletedTask);
        collection.On(BuiltInHooks.BeforeToolCall, (_, _) => Task.CompletedTask);
        collection.On(BuiltInHooks.AfterToolCall, (_, _) => Task.CompletedTask);

        collection.GetHandlers(BuiltInHooks.SystemPromptTransform).ShouldHaveSingleItem();
        collection.GetHandlers(BuiltInHooks.BeforeToolCall).ShouldHaveSingleItem();
        collection.GetHandlers(BuiltInHooks.AfterToolCall).ShouldHaveSingleItem();
        collection.GetHandlers(BuiltInHooks.SessionCreated).ShouldBeEmpty();
    }

    [Fact]
    public void HookCollectionTracksHookNames()
    {
        var collection = new NuCodeHookCollection();
        collection.On(BuiltInHooks.SystemPromptTransform, (_, _) => Task.CompletedTask);
        collection.On(BuiltInHooks.ToolRegistered, (_, _) => Task.CompletedTask);

        var names = collection.HookNames;
        names.ShouldContain("system.prompt.transform");
        names.ShouldContain("tool.registered");
        names.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HookHandlerCanMutateOutput()
    {
        var hook = BuiltInHooks.SystemPromptTransform;
        var collection = new NuCodeHookCollection();

        collection.On(hook, (input, output) =>
        {
            output.Segments.Add($"Plugin prompt for model {input.Model}");
            return Task.CompletedTask;
        });

        var handlers = collection.GetHandlers(hook);
        var input = new SystemPromptInput { Model = "gpt-4" };
        var output = new SystemPromptOutput();

        foreach (var handler in handlers)
        {
            await handler(input, output);
        }

        output.Segments.ShouldHaveSingleItem();
        output.Segments[0].ShouldBe("Plugin prompt for model gpt-4");
    }

    [Fact]
    public async Task MultipleHandlersMutateOutputSequentially()
    {
        var hook = BuiltInHooks.SystemPromptTransform;
        var collection = new NuCodeHookCollection();

        collection
            .On(hook, (_, output) => { output.Segments.Add("first"); return Task.CompletedTask; })
            .On(hook, (_, output) => { output.Segments.Add("second"); return Task.CompletedTask; });

        var handlers = collection.GetHandlers(hook);
        var output = new SystemPromptOutput();

        foreach (var handler in handlers)
        {
            await handler(new SystemPromptInput(), output);
        }

        output.Segments.ShouldBe(["first", "second"]);
    }

    [Fact]
    public void BuiltInHooksHaveDistinctNames()
    {
        var names = new HashSet<string>
        {
            BuiltInHooks.SystemPromptTransform.Name,
            BuiltInHooks.ToolRegistered.Name,
            BuiltInHooks.SessionCreated.Name,
            BuiltInHooks.BeforeToolCall.Name,
            BuiltInHooks.AfterToolCall.Name,
            BuiltInHooks.ChatParams.Name,
        };

        names.Count.ShouldBe(6);
    }

    // --- Task 38: Plugin registry ---

    private sealed class TestPlugin : INuCodePlugin
    {
        public string Name { get; }
        public Func<IServiceProvider, NuCodeHookCollection>? InitAction { get; init; }
        public bool Initialized { get; private set; }

        internal TestPlugin(string name)
        {
            Name = name;
        }

        public NuCodeHookCollection Initialize(IServiceProvider services)
        {
            Initialized = true;
            return InitAction?.Invoke(services) ?? new NuCodeHookCollection();
        }
    }

    private sealed class ThrowingPlugin : INuCodePlugin
    {
        public string Name => "throwing";

        public NuCodeHookCollection Initialize(IServiceProvider services)
        {
            throw new InvalidOperationException("Init failed!");
        }
    }

    private static PluginRegistry CreateRegistry()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new PluginRegistry(services, null);
    }

    [Fact]
    public void RegisterAddsPlugin()
    {
        var registry = CreateRegistry();
        var plugin = new TestPlugin("test-plugin");

        registry.Register(plugin);

        var all = registry.GetAll();
        all.ShouldHaveSingleItem();
        all[0].Name.ShouldBe("test-plugin");
        plugin.Initialized.ShouldBeTrue();
    }

    [Fact]
    public void RegisterSkipsDuplicateByName()
    {
        var registry = CreateRegistry();
        var plugin1 = new TestPlugin("dup");
        var plugin2 = new TestPlugin("dup");

        registry.Register(plugin1);
        registry.Register(plugin2);

        registry.GetAll().ShouldHaveSingleItem();
        plugin2.Initialized.ShouldBeFalse();
    }

    [Fact]
    public void RegisterHandlesInitializationFailureGracefully()
    {
        var registry = CreateRegistry();

        registry.Register(new ThrowingPlugin());

        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void RegisterMultiplePlugins()
    {
        var registry = CreateRegistry();

        registry.Register(new TestPlugin("alpha"));
        registry.Register(new TestPlugin("beta"));
        registry.Register(new TestPlugin("gamma"));

        var all = registry.GetAll();
        all.Count.ShouldBe(3);
        all[0].Name.ShouldBe("alpha");
        all[1].Name.ShouldBe("beta");
        all[2].Name.ShouldBe("gamma");
    }

    [Fact]
    public void GetAllReturnsSnapshot()
    {
        var registry = CreateRegistry();
        registry.Register(new TestPlugin("snap"));

        var snapshot = registry.GetAll();
        registry.Register(new TestPlugin("another"));

        // Snapshot should not include the later registration
        snapshot.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TriggerAsyncRunsHandlersAcrossPlugins()
    {
        var registry = CreateRegistry();
        var callOrder = new List<string>();

        registry.Register(new TestPlugin("p1")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.SystemPromptTransform, (_, output) =>
                {
                    callOrder.Add("p1");
                    output.Segments.Add("from-p1");
                    return Task.CompletedTask;
                })
        });

        registry.Register(new TestPlugin("p2")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.SystemPromptTransform, (_, output) =>
                {
                    callOrder.Add("p2");
                    output.Segments.Add("from-p2");
                    return Task.CompletedTask;
                })
        });

        var output = await registry.TriggerAsync(
            BuiltInHooks.SystemPromptTransform,
            new SystemPromptInput { SessionId = "s1", Model = "gpt-4" },
            new SystemPromptOutput());

        callOrder.ShouldBe(["p1", "p2"]);
        output.Segments.ShouldBe(["from-p1", "from-p2"]);
    }

    [Fact]
    public async Task TriggerAsyncReturnsOutputWhenNoHandlers()
    {
        var registry = CreateRegistry();

        var output = await registry.TriggerAsync(
            BuiltInHooks.SystemPromptTransform,
            new SystemPromptInput(),
            new SystemPromptOutput { Segments = ["original"] });

        output.Segments.ShouldBe(["original"]);
    }

    [Fact]
    public async Task TriggerAsyncContinuesAfterHandlerFailure()
    {
        var registry = CreateRegistry();

        registry.Register(new TestPlugin("failing")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.BeforeToolCall, (_, _) =>
                    throw new InvalidOperationException("handler crash"))
        });

        registry.Register(new TestPlugin("surviving")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.BeforeToolCall, (_, output) =>
                {
                    output.Arguments["injected"] = true;
                    return Task.CompletedTask;
                })
        });

        var output = await registry.TriggerAsync(
            BuiltInHooks.BeforeToolCall,
            new BeforeToolCallInput { ToolName = "bash", SessionId = "s1", CallId = "c1" },
            new BeforeToolCallOutput());

        ((bool)output.Arguments["injected"]!).ShouldBeTrue();
    }

    [Fact]
    public async Task TriggerAsyncPassesInputToAllHandlers()
    {
        var registry = CreateRegistry();
        var capturedInputs = new List<string>();

        registry.Register(new TestPlugin("capture")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.ChatParams, (input, _) =>
                {
                    capturedInputs.Add($"{input.Agent}:{input.Model}");
                    return Task.CompletedTask;
                })
        });

        await registry.TriggerAsync(
            BuiltInHooks.ChatParams,
            new ChatParamsInput { SessionId = "s1", Agent = "build", Model = "gpt-4o" },
            new ChatParamsOutput());

        capturedInputs.ShouldBe(["build:gpt-4o"]);
    }

    [Fact]
    public async Task TriggerAsyncOutputMutationsAccumulate()
    {
        var registry = CreateRegistry();

        registry.Register(new TestPlugin("adder1")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.AfterToolCall, (_, output) =>
                {
                    output.Metadata["key1"] = "val1";
                    return Task.CompletedTask;
                })
        });

        registry.Register(new TestPlugin("adder2")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.AfterToolCall, (_, output) =>
                {
                    output.Metadata["key2"] = "val2";
                    return Task.CompletedTask;
                })
        });

        var output = await registry.TriggerAsync(
            BuiltInHooks.AfterToolCall,
            new AfterToolCallInput { ToolName = "read", SessionId = "s1", CallId = "c1" },
            new AfterToolCallOutput());

        output.Metadata["key1"].ShouldBe("val1");
        output.Metadata["key2"].ShouldBe("val2");
    }

    [Fact]
    public async Task TriggerAsyncHandlesMultipleHandlersInSinglePlugin()
    {
        var registry = CreateRegistry();

        registry.Register(new TestPlugin("multi")
        {
            InitAction = _ => new NuCodeHookCollection()
                .On(BuiltInHooks.SessionCreated, (_, output) =>
                {
                    output.Metadata["a"] = "1";
                    return Task.CompletedTask;
                })
                .On(BuiltInHooks.SessionCreated, (_, output) =>
                {
                    output.Metadata["b"] = "2";
                    return Task.CompletedTask;
                })
        });

        var output = await registry.TriggerAsync(
            BuiltInHooks.SessionCreated,
            new SessionCreatedInput { SessionId = "s1" },
            new SessionCreatedOutput());

        output.Metadata["a"].ShouldBe("1");
        output.Metadata["b"].ShouldBe("2");
    }

    [Fact]
    public void PluginReceivesServiceProviderOnInit()
    {
        var services = new ServiceCollection();
        services.AddSingleton("test-service-value");
        var sp = services.BuildServiceProvider();
        var registry = new PluginRegistry(sp, null);
        string? resolvedValue = null;

        registry.Register(new TestPlugin("di-test")
        {
            InitAction = svc =>
            {
                resolvedValue = svc.GetService<string>();
                return new NuCodeHookCollection();
            }
        });

        resolvedValue.ShouldBe("test-service-value");
    }

    // --- DI integration ---

    [Fact]
    public void PluginRegistryIsResolvedFromDi()
    {
        var services = new ServiceCollection();
        services.AddNuCode();
        var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<IPluginRegistry>();

        registry.ShouldNotBeNull();
    }

    [Fact]
    public void PluginRegistryIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddNuCode();
        var sp = services.BuildServiceProvider();

        var r1 = sp.GetRequiredService<IPluginRegistry>();
        var r2 = sp.GetRequiredService<IPluginRegistry>();

        r2.ShouldBeSameAs(r1);
    }
}
