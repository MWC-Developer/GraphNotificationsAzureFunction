# Graph Notifications Azure Function

This solution demonstrates how to receive and manage **Microsoft Graph change notifications** (webhooks) using an **Azure Functions v4** app built on **.NET 10** with the isolated worker model.

---

## What This Solution Demonstrates

| Capability | Details |
|---|---|
| **Webhook endpoint** | Receives Graph change notifications via HTTP POST at `api/graph/notifications` |
| **Lifecycle endpoint** | Handles Graph lifecycle events (reauthorization, subscription removed, missed notifications) at `api/graph/lifecycle` |
| **Subscription management** | Creates, lists, reauthorizes, and deletes Microsoft Graph subscriptions via the Graph API |
| **Subscription auto-renewal** | Automatically reauthorizes expiring subscriptions and recreates removed ones |
| **Notification storage** | Persists all received notifications (change and lifecycle) to **Azure Table Storage** |
| **Administration UI** | Browser-based management page at `api/graph/manage` for viewing subscriptions and recent notifications |
| **App-only authentication** | Uses `ClientSecretCredential` (Entra ID app registration with client secret) to authenticate against the Graph API |
| **Multiple resource tracking** | Supports configuring subscriptions for Mail, Calendar, OneDrive, Microsoft Teams resources, or any custom resource path |
| **Scale-out via queues** | HTTP triggers enqueue notifications immediately and return 202; queue-triggered workers do all processing asynchronously |
| **Rate limiting** | Proactive sliding-window limiter and reactive `Retry-After` honouring keep outbound Graph calls within the published 500-per-20-second threshold |

---

## Architecture Overview

The solution uses a **two-stage pipeline** to decouple fast HTTP acknowledgement from slower downstream processing, allowing it to scale to thousands of concurrent subscriptions without being throttled by the Graph API or timing out Graph's webhook delivery window.

```
Entra ID App Registration
		│  (client credentials flow)
		▼
GraphSubscriptionManager ──── GraphRateLimiter ──────► Microsoft Graph API
		│                     (≤ 475 calls / 20 s)      (create / list /
		│                                                 delete / reauthorize)
		│
Azure Function  ── HTTP triggers (fast path) ──────────────────────────────┐
  ├── api/graph/notifications  ◄── Graph sends change notifications        │
  │     • validates client state                                            │
  │     • enqueues to graph-change-notifications                            │
  │     • returns 202 immediately  ─────────────────────────────────────── │
  ├── api/graph/lifecycle       ◄── Graph sends lifecycle events           │
  │     • enqueues to graph-lifecycle-notifications                         │
  │     • returns 202 immediately  ─────────────────────────────────────── │
  └── api/graph/manage          ◄── Browser administration UI              │
																			│
Azure Storage Queues ◄──────────────────────────────────────────────────── ┘
  ├── graph-change-notifications
  └── graph-lifecycle-notifications
		│
		▼
Azure Function  ── Queue triggers (async workers, scale-out)
  ├── ProcessChangeNotification
  │     • deserializes notification
  │     • writes to Table Storage
  └── ProcessLifecycleNotification
		• deserializes notification
		• writes to Table Storage
		• calls LifecycleNotificationService (reauthorize / recreate / sync)
			  │
			  ▼
		GraphSubscriptionManager (rate-limited)
			  │
			  ▼
		Microsoft Graph API
		│
		▼
  Azure Table Storage
  (GraphNotifications table)
```

---

## Scaling Design

### Why queues?

Microsoft Graph expects a webhook endpoint to return **HTTP 2xx within 10 seconds**. If the function does storage writes and Graph API calls inline during that window, any latency spike (storage contention, Graph throttling, cold start) causes Graph to consider the delivery failed and retry — potentially triggering a cascade.

By enqueuing immediately and returning 202, the HTTP function completes in milliseconds regardless of downstream load. The queue workers then process messages at whatever pace the system can sustain, independently of Graph's delivery clock.

### Queue configuration (`host.json`)

| Setting | Value | Effect |
|---|---|---|
| `batchSize` | `8` | Each worker instance pulls up to 8 messages at once |
| `newBatchThreshold` | `4` | Fetches the next batch when fewer than 4 messages remain |
| `visibilityTimeout` | `30 s` | Message reappears for retry if a worker crashes mid-flight |
| `maxDequeueCount` | `5` | After 5 failed attempts the message is moved to the poison queue |
| `dynamicConcurrencyEnabled` | `true` | Host automatically tunes concurrency based on measured throughput |
| `snapshotPersistenceEnabled` | `true` | Concurrency snapshots survive restarts, avoiding cold-start over-scaling |

Azure Functions automatically scales out the number of worker instances based on queue depth, so the solution handles bursts of thousands of notifications without manual intervention.

### Queue names

| Queue | Written by | Consumed by |
|---|---|---|
| `graph-change-notifications` | `HandleNotifications` HTTP trigger | `ProcessChangeNotification` queue trigger |
| `graph-lifecycle-notifications` | `HandleLifecycleNotifications` HTTP trigger | `ProcessLifecycleNotification` queue trigger |

Each queue message is a JSON-serialized `NotificationQueueMessage` envelope containing the raw `GraphNotification` and a category string (`"change"` or `"lifecycle"`).

---

## Rate Limiting

### Published limit

The Microsoft Graph subscriptions endpoint enforces a limit of **500 calls per 20 seconds** per application. With many lifecycle events arriving simultaneously (reauthorization requests, subscription-removed events) and multiple queue worker instances processing them concurrently, it is straightforward to exceed this threshold.

### Two-layer defence (`GraphRateLimiter`)

The `GraphRateLimiter` singleton is shared across all worker instances in the process and protects every outbound Graph call made by `GraphSubscriptionManager`.

#### Layer 1 — Proactive (sliding-window token bucket)

A `SlidingWindowRateLimiter` is configured with the permit budget divided into **four equal segments** over the window. Before every Graph request, the caller must `AcquireAsync` one permit. If the budget for the current window is exhausted, the caller is queued and waits until a new segment opens — Graph never sees more requests than the configured ceiling.

Default values target **475 permits per 20-second window** (5 % headroom below the published limit), which leaves room for administrative calls from the management UI without risking a 429.

#### Layer 2 — Reactive (Retry-After honouring)

When Graph returns **HTTP 429 (Too Many Requests)** or **503 (Service Unavailable)**, `NotifyThrottled` is called. It:

1. Parses the `Retry-After` response header (delta-seconds or HTTP-date).
2. Records a **process-wide embargo timestamp** using a lock-free compare-and-swap on a `long` (UTC ticks), so two concurrent 429 responses always advance the embargo to the later of the two times.
3. All callers in `AcquireAsync` check this embargo before acquiring a permit and wait out the remaining delay.

The call is then **retried automatically** up to `MaxRetries` times (default 3), after honouring the embargo. This means a transient throttle is fully transparent to the caller.

### Rate-limit configuration

| Setting | Alt keys | Default | Description |
|---|---|---|---|
| `Graph:RateLimitPermits` | `GraphSubscription:RateLimitPermits` | `475` | Maximum Graph calls allowed per window |
| `Graph:RateLimitWindowSeconds` | `GraphSubscription:RateLimitWindowSeconds` | `20` | Sliding window length in seconds |
| `Graph:MaxRetries` | `GraphSubscription:MaxRetries` | `3` | Maximum retry attempts per call on a 429 or 503 response |

These can be adjusted via app settings without redeployment — useful if Microsoft revises the published throttle limits.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- An **Azure Storage account** (or [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) for local development) — used for both the Table Storage notification log and the two Azure Storage Queues
- A **Microsoft Entra ID app registration** with the appropriate Graph API permissions (see below)
- A publicly reachable HTTPS URL for the function (use [dev tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/overview) or [ngrok](https://ngrok.com/) for local testing)

---

## Entra ID App Registration

1. In the [Azure Portal](https://portal.azure.com), go to **Microsoft Entra ID → App registrations → New registration**.
2. Give it a name (e.g., `GraphNotificationsFunction`) and register it.
3. Under **Certificates & secrets**, create a **New client secret** and copy the value immediately.
4. Under **API permissions**, add the **Application** (not delegated) permissions required for the resources you want to subscribe to. Common examples:

   | Resource | Required permission |
   |---|---|
   | Mail (`/users/{id}/messages`) | `Mail.Read` |
   | Calendar (`/users/{id}/events`) | `Calendars.Read` |
   | OneDrive (`/users/{id}/drive/root`) | `Files.Read.All` |
   | Teams chat messages (`/chats/getAllMessages`) | `Chat.Read.All` |
   | Teams channel messages (`/teams/getAllMessages`) | `ChannelMessage.Read.All` |
   | Teams presence (`/communications/presences/{id}`) | `Presence.Read.All` |

5. Click **Grant admin consent** for your tenant.
6. Note the **Application (client) ID** and **Directory (tenant) ID** from the Overview page.

---

## Configuration

Configure the function using `local.settings.json` for local development or **Application Settings** in Azure.

### `local.settings.json` (local development)

```json
{
  "IsEncrypted": false,
  "Values": {
	"AzureWebJobsStorage": "UseDevelopmentStorage=true",
	"FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",

	"GraphTenantId": "<your-tenant-id>",
	"GraphClientId": "<your-app-client-id>",
	"GraphClientSecret": "<your-client-secret>",

	"GraphNotificationUrl": "https://<your-tunnel-host>/api/graph/notifications",
	"GraphLifecycleNotificationUrl": "https://<your-tunnel-host>/api/graph/lifecycle",

	"GraphSubscriptionResource": "users/{user-id}/messages",
	"GraphSubscriptionChangeType": "created,updated,deleted",
	"GraphSubscriptionLifetimeMinutes": "60",

	"GraphClientState": "<a-random-secret-string>",
	"GraphNotificationsTableName": "GraphNotifications",

	"Graph:RateLimitPermits": "475",
	"Graph:RateLimitWindowSeconds": "20",
	"Graph:MaxRetries": "3"
  }
}
```

### Configuration Reference

#### Core settings

| Setting | Alt keys | Description |
|---|---|---|
| `GraphTenantId` | `Graph:TenantId`, `GraphSubscription:TenantId` | Entra ID tenant ID |
| `GraphClientId` | `Graph:ClientId`, `GraphSubscription:ClientId` | App registration client ID |
| `GraphClientSecret` | `Graph:ClientSecret`, `GraphSubscription:ClientSecret` | App registration client secret |
| `GraphNotificationUrl` | `Graph:NotificationUrl`, `GraphSubscription:NotificationUrl` | Public HTTPS URL for change notifications (auto-detected from `WEBSITE_HOSTNAME` in Azure) |
| `GraphLifecycleNotificationUrl` | `Graph:LifecycleNotificationUrl`, `GraphSubscription:LifecycleNotificationUrl` | Public HTTPS URL for lifecycle notifications (defaults to `GraphNotificationUrl`) |
| `GraphSubscriptionResource` | `Graph:Resource`, `GraphSubscription:Resource` | Graph resource path to subscribe to (e.g. `users/{id}/messages`) |
| `GraphSubscriptionChangeType` | `Graph:ChangeType`, `GraphSubscription:ChangeType` | Comma-separated change types: `created`, `updated`, `deleted` |
| `GraphSubscriptionLifetimeMinutes` | `Graph:SubscriptionLifetimeMinutes` | Subscription TTL in minutes (default: `60`, max varies by resource) |
| `GraphClientState` | `Graph:ClientState`, `GraphSubscription:ClientState` | Secret string echoed back by Graph; used to validate incoming notifications |
| `GraphNotificationsTableName` | — | Azure Table Storage table name (default: `GraphNotifications`) |

#### Rate limiting settings

| Setting | Alt keys | Default | Description |
|---|---|---|---|
| `Graph:RateLimitPermits` | `GraphSubscription:RateLimitPermits` | `475` | Maximum Graph API calls allowed per rate-limit window |
| `Graph:RateLimitWindowSeconds` | `GraphSubscription:RateLimitWindowSeconds` | `20` | Sliding window length in seconds (matches Graph's 20 s throttle window) |
| `Graph:MaxRetries` | `GraphSubscription:MaxRetries` | `3` | Maximum retry attempts per call when Graph returns 429 or 503 |

---

## Creating a Graph Subscription

### Option 1 — Administration UI (recommended)

Once the function is running and reachable via HTTPS:

1. Open `https://<your-function-host>/api/graph/manage` in a browser.
2. Select one or more resources from the list (grouped by category: Microsoft 365, Microsoft Teams, etc.).
3. Enter a **User id** if any selected resource path contains `{id}`.
4. Click **Create Subscription**.
5. The page will show the new subscription ID, the resource it watches, and its expiry time.

### Option 2 — Microsoft Graph API (manual)

Send an authenticated POST request to `https://graph.microsoft.com/v1.0/subscriptions`:

```http
POST https://graph.microsoft.com/v1.0/subscriptions
Authorization: Bearer <access-token>
Content-Type: application/json

{
  "changeType": "created,updated,deleted",
  "notificationUrl": "https://<your-function-host>/api/graph/notifications",
  "lifecycleNotificationUrl": "https://<your-function-host>/api/graph/lifecycle",
  "resource": "users/{user-id}/messages",
  "expirationDateTime": "2025-12-31T00:00:00Z",
  "clientState": "<your-GraphClientState-value>"
}
```

Graph will perform a **validation handshake** — it sends a GET request with a `validationToken` query parameter to the `notificationUrl`. The function handles this automatically and echoes the token back.

### Option 3 — Graph Explorer

1. Navigate to [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer).
2. Sign in and consent to the required permissions.
3. Change the verb to **POST** and the URL to `https://graph.microsoft.com/v1.0/subscriptions`.
4. Paste the JSON body from Option 2 into the **Request body** tab and click **Run query**.

---

## Running Locally

```powershell
# Start Azurite (local storage emulator) in a separate terminal
# Azurite provides both the Table Storage and the Queue Storage used by the function
azurite --silent

# Start the function host
cd "GraphNotificationsAzureFunction"
func start
```

Use a dev tunnel or ngrok to expose `http://localhost:7071` over HTTPS, then set `GraphNotificationUrl` and `GraphLifecycleNotificationUrl` to the tunnel URL.

> **Note:** Azurite automatically creates the `graph-change-notifications` and `graph-lifecycle-notifications` queues on first use. No pre-creation is needed.

---

## Deploying to Azure

The solution includes a publish profile for Azure Functions **One Deploy**. You can deploy via Visual Studio (**Publish** menu) or the Azure CLI:

```powershell
az functionapp deployment source config-zip `
  --resource-group <rg> `
  --name <function-app-name> `
  --src <path-to-zip>
```

After deployment, the `WEBSITE_HOSTNAME` environment variable is set automatically, so `GraphNotificationUrl` and `GraphLifecycleNotificationUrl` are inferred if not explicitly configured.

> **Storage account:** The same Azure Storage account referenced by `AzureWebJobsStorage` is used for Table Storage (notification log) and Queue Storage (processing pipeline). No additional storage resources are required.

---

## Lifecycle Event Handling

Graph sends lifecycle events to the `api/graph/lifecycle` endpoint. The HTTP trigger enqueues these immediately and returns 202. The `ProcessLifecycleNotification` queue worker then calls `LifecycleNotificationService`, which dispatches based on the event type:

| Event | Action taken |
|---|---|
| `reauthorizationRequired` | Calls `subscriptions/{id}/reauthorize` on the Graph API (rate-limited) |
| `subscriptionRemoved` | Automatically creates a new subscription (rate-limited) |
| `missed` | Triggers a full sync (logged; extend `PerformFullSyncAsync` for your use case) |

All Graph API calls made during lifecycle handling pass through `GraphRateLimiter`, so a burst of reauthorization events from many subscriptions will be smoothed across the rate-limit window rather than causing a 429 cascade.

---

## Project Structure

| File | Purpose |
|---|---|
| `Program.cs` | Function host bootstrap and dependency injection (registers `TableClient`, `GraphRateLimiter`, `GraphSubscriptionManager`, `LifecycleNotificationService`) |
| `NotificationsWebhook.cs` | HTTP-triggered functions — validates requests, enqueues notifications, returns 202 immediately |
| `NotificationsQueueProcessor.cs` | Queue-triggered workers — deserializes messages, writes to Table Storage, calls lifecycle service |
| `GraphSubscriptionManager.cs` | Graph API client for subscription CRUD; all calls pass through `GraphRateLimiter` |
| `GraphRateLimiter.cs` | Process-wide singleton enforcing the 475-per-20-second proactive limit and reactive `Retry-After` embargo |
| `GraphSubscriptionSettings.cs` | Configuration binding, validation, and resource option catalogue (Mail, Calendar, OneDrive, Teams) |
| `LifecycleNotificationService.cs` | Handles Graph lifecycle events: reauthorize, recreate, full sync |
| `SubscriptionAdministrationWeb.cs` | Generates the browser-based admin UI |
| `GraphNotificationModels.cs` | JSON models for Graph notification payloads, queue message envelope, multi-output binding types, and `NotificationEntity` |
| `host.json` | Functions host configuration: dynamic concurrency, queue batch size, visibility timeout, dequeue limit |

---

## License

See [LICENSE.txt](LICENSE.txt).

