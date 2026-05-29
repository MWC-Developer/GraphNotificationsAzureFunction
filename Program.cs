/*
 * By David Barrett, Microsoft Ltd. Use at your own risk.  No warranties are given.
 * 
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * */

using Azure.Data.Tables;
using GraphNotificationsAzureFunction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// ConfigureFunctionsApplicationInsights() installs a post-configure rule that raises the
// Application Insights log level back to Warning, overriding any AddFilter calls made here.
// Remove that rule so our namespace filter (Information) is not silently discarded.
builder.Logging.Services.Configure<Microsoft.Extensions.Logging.LoggerFilterOptions>(options =>
{
    var aiRule = options.Rules.FirstOrDefault(r =>
        r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (aiRule is not null)
    {
        options.Rules.Remove(aiRule);
    }
});

builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("GraphNotificationsAzureFunction", LogLevel.Information);

builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured.");
    var tableName = Environment.GetEnvironmentVariable("GraphNotificationsTableName");
    if (string.IsNullOrWhiteSpace(tableName))
    {
        tableName = "GraphNotifications";
    }
    var client = new TableClient(connectionString, tableName);
    client.CreateIfNotExists();
    return client;
});
builder.Services.AddSingleton(sp => new GraphSubscriptionSettings(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<GraphRateLimiter>();
builder.Services.AddSingleton<IGraphSubscriptionManager, GraphSubscriptionManager>();
builder.Services.AddSingleton<ILifecycleNotificationService, LifecycleNotificationService>();

builder.Build().Run();
