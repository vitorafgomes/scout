---
title: "Adapter.Teams: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Teams service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Adapter.Teams/", "tests/Scout.Adapter.Teams.Tests/"]
reliability: high
---

## Public contract

- **Entry point:** `AddTeamsAdapter(IServiceCollection, IConfiguration)` at `src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:27`; registers nothing without an app id (`:37`).
- **Key types:** `TeamsChatAdapter : IChatAdapter` (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:36`) with `ProcessActivityAsync(ITurnContext, CancellationToken)` (`:100`), `TeamsMessageTranslator` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:21`), `TeamsResponseSink : IResponseSink` (`src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:53`), `ITeamsActivitySender` (`src/Scout.Adapter.Teams/Streaming/ITeamsActivitySender.cs:16`), `TeamsTransportException` (`src/Scout.Adapter.Teams/Streaming/TeamsTransportException.cs:12`), `TeamsOptions` (`src/Scout.Adapter.Teams/TeamsOptions.cs:8`).
- **Configuration keys:** `Scout:Adapters:Teams` with `AppId`, `AppPassword`, `TenantId` (`src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:25`).
- **External boundaries:** Bot Framework connector; the sink writes through `SendActivityAsync` (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:155`). `ProcessActivityAsync` has no production caller in the repository.
