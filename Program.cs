using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using SKFProductAssistant.Agents;
using SKFProductAssistant.Orchestrator;
using SKFProductAssistant.Plugins;
using SKFProductAssistant.Services;
using StackExchange.Redis;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;
        var useRedis = bool.TryParse(config["USE_REDIS"], out var r) && r;

        if (useRedis)
        {
            var connectionString = config["REDIS_CONNECTION_STRING"]
                ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING is required when USE_REDIS=true");

            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
            services.AddSingleton<ICacheService, RedisCacheService>();
            services.AddSingleton<IFeedbackStore, RedisFeedbackStore>();
        }
        else
        {
            services.AddSingleton<ICacheService, InMemoryCacheService>();
            services.AddSingleton<IFeedbackStore, InMemoryFeedbackStore>();
        }

        services.AddSingleton<IConversationStateService, ConversationStateService>();
        services.AddSingleton<DatasheetPlugin>();
        services.AddSingleton<CachePlugin>();
        services.AddSingleton<FeedbackPlugin>();
        services.AddSingleton<QAAgent>();
        services.AddSingleton<FeedbackAgent>();
        services.AddSingleton<ConversationOrchestrator>();

        var endpoint   = config["AZURE_OPENAI_ENDPOINT"]   ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required");
        var apiKey     = config["AZURE_OPENAI_API_KEY"]     ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is required");
        var deployment = config["AZURE_OPENAI_DEPLOYMENT"]  ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT is required");

        // Transient so each request gets a fresh Kernel — avoids concurrency issues
        // with plugin state. Plugins themselves are singletons (stateless after construction).
        services.AddTransient<Kernel>(sp =>
        {
            var builder = Kernel.CreateBuilder();
            builder.AddAzureOpenAIChatCompletion(deployment, endpoint, apiKey);
            builder.Plugins.AddFromObject(sp.GetRequiredService<DatasheetPlugin>(), "DatasheetPlugin");
            builder.Plugins.AddFromObject(sp.GetRequiredService<CachePlugin>(),     "CachePlugin");
            builder.Plugins.AddFromObject(sp.GetRequiredService<FeedbackPlugin>(),  "FeedbackPlugin");
            return builder.Build();
        });
    })
    .Build();

host.Run();
