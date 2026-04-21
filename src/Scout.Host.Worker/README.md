# Scout.Host.Worker

Reference worker that composes the Scout core with a chat adapter and an LLM
provider, then runs as a `BackgroundService`.

Default composition:

- Chat adapter: **Discord** (`Scout.Adapter.Discord`)
- LLM provider: **Ollama** (`Scout.Llm.Ollama`)

## Local setup

1. **Install Ollama** and pull a chat model:

   ```bash
   curl -fsSL https://ollama.com/install.sh | sh
   ollama pull llama3.1
   ollama serve   # runs on http://localhost:11434
   ```

2. **Create a Discord bot**:

   - Go to <https://discord.com/developers/applications> → New Application.
   - Add a Bot user. Copy the bot token.
   - Under "Privileged Gateway Intents", enable **Message Content Intent**.
   - Invite the bot to a test server with the `Send Messages` and
     `Read Message History` permissions.

3. **Store the token with User Secrets** (never commit it):

   ```bash
   cd src/Scout.Host.Worker
   dotnet user-secrets set "Scout:Adapters:Discord:BotToken" "YOUR_BOT_TOKEN"
   ```

   Alternatively, export as environment variable:

   ```bash
   export SCOUT__ADAPTERS__DISCORD__BOTTOKEN="YOUR_BOT_TOKEN"
   ```

4. **Run**:

   ```bash
   dotnet run --project src/Scout.Host.Worker
   ```

5. In Discord, mention the bot in a channel it has access to:

   ```
   @scout is anyone home?
   ```

   The bot replies with the V1 handshake:
   `Scout is online. 0 plugin(s) loaded. Ask me something specific.`

## Configuration shape

See `appsettings.json`. All values are override-able via environment
variables using the double-underscore separator.

```json
{
  "Scout": {
    "ChatAdapter": "Discord",
    "LlmProvider": "Ollama",
    "EnabledPlugins": [],
    "Llm": {
      "Provider": "Ollama",
      "Ollama": {
        "Endpoint": "http://localhost:11434",
        "Model": "llama3.1"
      }
    },
    "Adapters": {
      "Discord": { "BotToken": "<from env / user secrets>" }
    }
  }
}
```

## Docs

Design, architecture, and plans live in the Obsidian vault under
`obsidian-vault/scout/`.
