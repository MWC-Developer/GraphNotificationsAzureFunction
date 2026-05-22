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
| **Multiple resource tracking** | Supports configuring subscriptions for Mail, Calendar, OneDrive, or any custom resource |

---

## Architecture Overview

```
Entra ID App Registration
		│  (client credentials)
		▼
GraphSubscriptionManager  ──────► Microsoft Graph API
		│                          (create / list / delete subscriptions)
		│
Azure Function (HTTP triggers)
  ├── api/graph/notifications  ◄── Graph sends change notifications
  ├── api/graph/lifecycle      ◄── Graph sends lifecycle events
  └── api/graph/manage         ◄── Browser administration UI
		│
		▼
  Azure Table Storage
  (GraphNotifications table)
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- An **Azure Storage account** (or [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) for local development)
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
   | All users' mail (`/users`) | `Mail.Read` |

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
	"GraphNotificationsTableName": "GraphNotifications"
  }
}
```

### Configuration Reference

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

---

## Creating a Graph Subscription

### Option 1 — Administration UI (recommended)

Once the function is running and reachable via HTTPS:

1. Open `https://<your-function-host>/api/graph/manage` in a browser.
2. Select a resource from the dropdown (or use the pre-configured default).
3. Click **Create Subscription**.
4. The page will show the new subscription ID, the resource it watches, and its expiry time.

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
azurite --silent

# Start the function host
cd "GraphNotificationsAzureFunction"
func start
```

Use a dev tunnel or ngrok to expose `http://localhost:7071` over HTTPS, then set `GraphNotificationUrl` and `GraphLifecycleNotificationUrl` to the tunnel URL.

---

## Deploying to Azure

The solution includes a publish profile for Azure Functions **One Deploy**. You can deploy via Visual Studio (**Publish** menu) or the Azure CLI:

```powershell
az functionapp deployment source config-zip \
  --resource-group <rg> \
  --name <function-app-name> \
  --src <path-to-zip>
```

After deployment, the `WEBSITE_HOSTNAME` environment variable is set automatically, so `GraphNotificationUrl` and `GraphLifecycleNotificationUrl` are inferred if not explicitly configured.

---

## Lifecycle Event Handling

Graph sends lifecycle events to the `api/graph/lifecycle` endpoint when:

| Event | Action taken |
|---|---|
| `reauthorizationRequired` | Calls `subscriptions/{id}/reauthorize` on the Graph API |
| `subscriptionRemoved` | Automatically creates a new subscription |
| `missed` | Triggers a full sync (logged; extend `PerformFullSyncAsync` for your use case) |

---

## Project Structure

| File | Purpose |
|---|---|
| `Program.cs` | Function host bootstrap and dependency injection |
| `NotificationsWebhook.cs` | HTTP-triggered functions (notifications, lifecycle, management) |
| `GraphSubscriptionManager.cs` | Graph API client for subscription CRUD operations |
| `GraphSubscriptionSettings.cs` | Configuration binding and validation |
| `LifecycleNotificationService.cs` | Handles Graph lifecycle events |
| `SubscriptionAdministrationWeb.cs` | Generates the browser-based admin UI |
| `GraphNotificationModels.cs` | JSON models for Graph notification payloads |

---

## License

See [LICENSE.txt](LICENSE.txt).
