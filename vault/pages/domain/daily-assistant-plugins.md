---
title: "Daily-Assistant Plugins - Wave 1 Reference"
version: "1.0"
date: 2026-07-03
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: knowledge
status: active
description: "What actually shipped in Wave 1 of the daily-assistant plugin roadmap (plugin-roadmap-daily-assistant (private note)): the tools the chat LLM can call, their data sources, config, and how to use..."
source:
  - "obsidian-vault/scout/04-api/daily-assistant-plugins.md"
reliability: medium
---

# Daily-Assistant Plugins - Wave 1 Reference

What actually shipped in Wave 1 of the daily-assistant plugin roadmap
(plugin-roadmap-daily-assistant (private note)): the tools the chat LLM can call, their
data sources, config, and how to use them. Implemented on branch
`feat/daily-plugins-wave-1` (PR #7), built one-agent-per-plugin over a shared
foundation.

## Overview

Seven Semantic Kernel plugins + one shared foundation. Each plugin is a
`Scout.Plugin.<Name>` project whose `[KernelFunction]` methods the LLM invokes
by name through function-calling. All are **read-only or Scout-local
descriptive-write** (they never mutate anything external), and each **self-gates
on `Scout:Plugins:<Name>:Enabled`** (default `true`). They are wired into
`AddScoutHost` and aggregated onto the shared Kernel by `AddScoutLlm`.

Verified: solution builds 41 projects, 0 warnings; ~340 plugin tests + 26
foundation tests green.

## Shared foundation — `Scout.Plugin.Common`

Not a plugin; the building blocks the others reuse.

- **`IScoutLocalStore`** (`JsonFileScoutLocalStore`) — one atomic JSON file per
  collection under `{LocalAppData}/Scout/plugin-data/`. The **only write surface
  a plugin may touch** (Scout-owned local state). Atomic temp-write-then-rename,
  per-collection lock.
- **`RelaxedDate`** — tolerant date/time parsing over an injected `TimeProvider`:
  ISO and day/month formats plus en/pt relative words (`today`/`hoje`,
  `tomorrow`/`amanhã`, `yesterday`/`ontem`) and weekday names. Exposes
  `TryParse`, `Today`, `Now`, and `AcceptedFormatsHint`.
- **`AddScoutPluginCommon`** — idempotent DI registration of the store and
  `TimeProvider`.

## The plugins

### Weather — `Scout.Plugin.Weather`
Source: Open-Meteo (free, no key). Read-only.

| Function | Answers |
|---|---|
| `get_current_weather(city)` | conditions now: temp, feels-like, sky, wind, rain |
| `get_forecast(city, day="today")` | min/max, rain %, sunrise/sunset; `day` accepts today/tomorrow/weekday |
| `get_air_quality(city)` | European AQI + plain rating |

Config `Scout:Plugins:Weather`: `Enabled`, `DefaultCity`, `TimeZone` (empty =
machine local). Ask: *"como está o tempo em Londres?"*, *"vai chover amanhã?"*

### Dates — `Scout.Plugin.Dates`
Local computation only (no network). Read-only.

| Function | Answers |
|---|---|
| `current_time(timezone="")` | current time of day + date + zone (added this session) |
| `today(timezone="")` | date, weekday, ISO week |
| `days_until(date)` | countdown to a date |
| `day_of_week(date)` | weekday a date falls on |
| `date_difference(startDate, endDate)` | days between two dates |
| `add_to_date(date, days)` | date arithmetic |

Config `Scout:Plugins:Dates`: `Enabled`, `DefaultTimeZone` (empty = machine
local). Ask: *"que horas são?"*, *"quantos dias faltam pro Natal?"*

### Currency — `Scout.Plugin.Currency`
Source: Frankfurter (ECB rates, free, no key). Read-only.

`get_exchange_rate(from, to)`, `convert_currency(amount, from, to)`,
`get_rate_trend(from, to)` (30-day). Config `Scout:Plugins:Currency:Enabled`.
Ask: *"quanto tá a libra em reais?"*, *"converte 250 GBP pra BRL"*.

### Holidays — `Scout.Plugin.Holidays`
Source: Nager.Date (free, no key). Read-only.

`get_next_holidays(country="GB")`, `is_holiday(date, country="GB")`,
`list_holidays(country, year)`, `get_long_weekends(country="GB")`. Config
`Scout:Plugins:Holidays`: `Enabled`, `DefaultCountry` (GB), `TimeZone` (empty =
machine local). Ask: *"próximo feriado no Reino Unido?"*

### Wikipedia — `Scout.Plugin.Wikipedia`
Source: Wikimedia REST (free, no key). Read-only.

`search_wikipedia(query, language="en")`, `get_article_summary(title,
language="en")` (~1500-char cap), `get_on_this_day()`. Config
`Scout:Plugins:Wikipedia:Enabled`. Ask: *"o que é computação quântica?"*

### Lists — `Scout.Plugin.Lists`
Scout-local store (collection `lists`). Descriptive-write.

`add_to_list(listName, item)`, `show_list(listName="")`,
`remove_from_list(listName, item)` (fuzzy), `clear_list(listName)`. Config
`Scout:Plugins:Lists:Enabled`. Ask: *"adiciona leite na lista de compras"*.

### Notes — `Scout.Plugin.Notes`
Scout-local store (collection `notes`). Descriptive-write.

`add_note(text, tags="")`, `search_notes(query)`,
`list_recent_notes(count="5")`, `delete_note(id)`. Config
`Scout:Plugins:Notes:Enabled`. Ask: *"anota: senha do wifi é 1234"*.

## Time zone behaviour (session addition)

Default time zone is the **machine's local zone**, read directly in the backend
worker (`TimeZoneInfo.Local`) — the worker runs on the user's own device, so no
Electron round-trip is needed for the default. Resolution order:

1. Per-call `timezone` argument (e.g. *"que horas são em Tóquio?"*).
2. `Scout:Plugins:<Name>:(Default)TimeZone` config, set by the desktop app.
3. Machine local zone (fallback).

**Settings UI:** a *Time zone* card in the Electron Settings screen shows the
detected machine zone and lets the user override it. The override is stored in
the vault (authoritative, read by `build-env.ts`) and mirrored in `localStorage`
for display; on worker launch it is injected as
`SCOUT__PLUGINS__{DATES,WEATHER,HOLIDAYS}__...TIMEZONE`. No override = the
machine-local default.

## Shutdown fix (session addition)

Closing the UI now reliably stops the backend. `before-quit` previously exited
only inside `stop()`'s `finally`; a hung `stop()` (worker already crashed/exited,
whose `exit` event never re-fires) left a windowless Electron main alive, holding
the worker's stdin open so its parent-death watchdog never fired — the backend
outlived the closed UI. Fixed with a bounded force-exit guard: the app always
exits, which closes stdin and lets the watchdog self-terminate the worker.

## Config reference

Add under `Scout:Plugins:<Name>` in the worker config (or via the desktop app
env injection). All keyless; all `Enabled` default `true`.

```
Scout:Plugins:Weather   -> Enabled, DefaultCity, TimeZone
Scout:Plugins:Dates     -> Enabled, DefaultTimeZone
Scout:Plugins:Currency  -> Enabled
Scout:Plugins:Holidays  -> Enabled, DefaultCountry, TimeZone
Scout:Plugins:Wikipedia -> Enabled
Scout:Plugins:Lists     -> Enabled
Scout:Plugins:Notes     -> Enabled
Scout:Plugins:LocalStore -> RootDirectory (store path override; tests only)
```

## Known limitations

- **Small-model tool-calling.** The default LLM is a local `qwen2.5:3b`. It does
  not always emit a tool call — it may answer from its own knowledge and refuse
  ("I can't check the weather") even when the tool exists. Rephrasing ("use the
  weather tool for London") helps; a larger model (`qwen3.5:9b`) is more reliable
  but slower on CPU. See [[pages/runbooks/local-models]].
- Feature/tool availability requires a rebuilt AppImage — plugins are compiled
  into the worker binary, so an old AppImage has none of them.
- Waves 2 and 3 (14 more plugins) are not built. See
  plugin-roadmap-daily-assistant (private note).

## Cross-refs

plugin-roadmap-daily-assistant (private note) · [[pages/runbooks/local-models]] ·
[[pages/decisions/0009-descriptive-vs-executive-writes]] · improvement-backlog (private note)
