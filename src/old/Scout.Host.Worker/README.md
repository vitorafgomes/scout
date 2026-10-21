# Scout.Host.Worker

Reference worker that composes the Scout core with the Discord adapter,
the Ollama LLM provider, and the CloudStatus plugin (Azure / AWS / OCI /
Kubernetes), then runs as a `BackgroundService`.

## What it does

When the worker is running and a user mentions the bot on Discord with
the word `status` / `health` / `down` / `outage`, the bot replies with
an aggregated health report across every configured provider:

```
Cloud health:
- OracleCloud (status.oraclecloud.com): Healthy
- Azure (0000...abcd): Healthy
- Kubernetes (prod-eks): Degraded — Pod/api-xyz-7d: CrashLoopBackOff
```

Every other message still gets the handshake response.

## Local setup

1. **Install Ollama** and pull a chat model:

   ```bash
   curl -fsSL https://ollama.com/install.sh | sh
   ollama pull llama3.1
   ollama serve   # runs on http://localhost:11434
   ```

2. **Create a Discord bot**:

   - <https://discord.com/developers/applications> → New Application.
   - Add a Bot user. Copy the bot token.
   - Under "Privileged Gateway Intents", enable **Message Content Intent**.
   - Invite the bot to a test server with `Send Messages` and
     `Read Message History` permissions.

3. **Store the bot token through User Secrets** (never commit it):

   ```bash
   cd src/Scout.Host.Worker
   dotnet user-secrets set "Scout:Adapters:Discord:BotToken" "YOUR_BOT_TOKEN"
   ```

4. **Run**:

   ```bash
   dotnet run --project src/Scout.Host.Worker
   ```

5. In Discord, mention the bot:

   ```
   @scout status?
   ```

   With no cloud providers configured, you get:

   ```
   Cloud health:
   - OracleCloud (status.oraclecloud.com): Healthy
   ```

   (OCI is opt-in by default because its endpoint is public and free.)

## Cloud provider configuration

Each provider is **opt-in**. Leave the block out, or leave the required
field empty, and that provider is skipped silently — an unconfigured
cloud should never break host startup.

### Azure

Requires a subscription id and a credential that has the `Reader` role
at subscription scope.

- **Default**: `DefaultAzureCredential` (picks up `az login`, managed
  identity, environment variables, User Secrets).

  ```bash
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Azure:SubscriptionId" "<sub-guid>"
  ```

- **Explicit service principal** (recommended for CI / servers):

  ```bash
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Azure:SubscriptionId" "<sub-guid>"
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Azure:TenantId"       "<tenant-guid>"
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Azure:ClientId"       "<client-guid>"
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Azure:ClientSecretEnvVar" "SCOUT_AZURE_CLIENT_SECRET"
  export SCOUT_AZURE_CLIENT_SECRET="<client-secret>"
  ```

The live Service Health call is currently stubbed — tracked as
[#13](https://github.com/vitorafgomes/scout/issues/13). The plugin
reports `Healthy` when the API does not surface active events.

### AWS

AWS Health API requires **Business or Enterprise Support** on the target
account. Without that plan Scout reports `Unknown` with a clear hint.

- **Default AWS credential chain**:

  ```bash
  # env vars, shared profile at ~/.aws/credentials, EC2 instance role…
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Aws:Region" "eu-west-1"
  ```

- **Named profile**:

  ```bash
  dotnet user-secrets set "Scout:Plugins:CloudStatus:Aws:Profile" "my-readonly-profile"
  ```

The live `DescribeEvents` call is currently stubbed — tracked as
[#15](https://github.com/vitorafgomes/scout/issues/15).

### Oracle Cloud (OCI)

Public status endpoint — **zero credentials** required. Enabled by
default; set `Scout:Plugins:CloudStatus:OracleCloud:Enabled` to `false`
to skip it.

Optionally narrow the report:

```json
"OracleCloud": {
  "Enabled": true,
  "Regions": [ "eu-london-1", "us-phoenix-1" ],
  "Timeout": "00:00:05"
}
```

### Kubernetes

Choose one of two configuration modes:

- **In-cluster** (the bot runs inside a pod with a service account):

  ```json
  "Kubernetes": {
    "ClusterName": "prod-eks",
    "UseInClusterConfig": true
  }
  ```

  Service account needs `list` on `nodes` and `pods` in the target
  namespaces.

- **Kubeconfig file**:

  ```json
  "Kubernetes": {
    "ClusterName": "staging",
    "KubeconfigPath": "/home/ubuntu/.kube/config",
    "Context": "staging-context",
    "Namespaces": [ "default", "prod" ],
    "CheckNodes": true
  }
  ```

The live `ListNode` / `ListPod` call is currently stubbed — tracked as
[#18](https://github.com/vitorafgomes/scout/issues/18).

## Environment variable overrides

Every `appsettings.json` key can be overridden via environment variables
using `SCOUT__` as prefix and `__` as the path separator:

```bash
export SCOUT__ADAPTERS__DISCORD__BOTTOKEN="<token>"
export SCOUT__PLUGINS__CLOUDSTATUS__AZURE__SUBSCRIPTIONID="<guid>"
```

## Docs

Design, architecture, decision records, and implementation plans live in
the Obsidian vault under `obsidian-vault/scout/`.
