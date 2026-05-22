using GraphNotificationsAzureFunction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new GraphSubscriptionSettings(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IGraphSubscriptionManager, GraphSubscriptionManager>();
builder.Services.AddSingleton<ILifecycleNotificationService, LifecycleNotificationService>();

builder.Build().Run();
