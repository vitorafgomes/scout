---
title: "Plugin catalogue"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: longform
status: active
description: "One row per Scout plugin: functions, data source, read-only or descriptive write, state kept, configuration keys, and how plugins are registered."
source: ["src/Scout.Plugin.*/", "src/Scout.Host.Console/HostComposition.cs", "src/Scout.Llm/LlmServiceCollectionExtensions.cs", "src/Scout.ControlPlane/Features/FeatureCatalog.cs"]
reliability: high
---

# Plugin catalogue

> Twenty-three Semantic Kernel plugins, 87 model-callable functions between them, none of which mutates anything outside Scout's own local store.

Config keys are under `Scout:Plugins:<Plugin>`; names only. Stateful plugins also use `Scout:Plugins:LocalStore:RootDirectory` ([[pages/services/plugin-common]]). Rules: [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]].

| Plugin | Functions | Data source | Writes | State | Config keys |
|---|---|---|---|---|---|
| [[pages/services/plugin-github\|GitHub]] | list_repositories, list_pull_requests, list_issues, get_repository | GitHub REST (GET) | read-only | no | `Token` |
| [[pages/services/plugin-codereview\|CodeReview]] | analyze_code_quality, detect_vulnerabilities, suggest_improvements, generate_tests, explain_code, review_pull_request, analyze_repository | configured LLM, GitHub REST (GET) | read-only | no | `DefaultRepoUrl`, `Temperature`, `MaxTokens`, `MaxFileBytes`, `MaxFiles`, `CorpusCharBudget`, `Scout:Plugins:GitHub:Token` |
| [[pages/services/plugin-converter\|Converter]] | convert_units, calculate | none (local) | read-only | no | `Enabled`, `MaxExpressionLength` |
| [[pages/services/plugin-currency\|Currency]] | get_exchange_rate, convert_currency, get_rate_trend | Frankfurter API | read-only | no | `Enabled` |
| [[pages/services/plugin-dates\|Dates]] | today, current_time, days_until, day_of_week, date_difference, add_to_date | none (local clock) | read-only | no | `Enabled`, `DefaultTimeZone` |
| [[pages/services/plugin-dictionary\|Dictionary]] | define_word, get_synonyms, check_spelling | dictionaryapi.dev, Datamuse | read-only | no | `Enabled` |
| [[pages/services/plugin-holidays\|Holidays]] | get_next_holidays, is_holiday, list_holidays, get_long_weekends | Nager.Date API | read-only | no | `Enabled`, `DefaultCountry`, `TimeZone` |
| [[pages/services/plugin-ledger\|Ledger]] | record_expense, record_income, show_month_summary, list_recent_entries | Scout local store | descriptive write, local | yes | `Enabled`, `DefaultCurrency`, `TimeZone` |
| [[pages/services/plugin-lists\|Lists]] | add_to_list, show_list, remove_from_list, clear_list | Scout local store | descriptive write, local | yes | `Enabled` |
| [[pages/services/plugin-markets\|Markets]] | get_stock_quote, get_crypto_price, get_trending_crypto | Stooq, CoinGecko | read-only | no | `Enabled`, `CoinGeckoApiKey` |
| [[pages/services/plugin-news\|News]] | get_top_headlines, search_news, get_hacker_news_top, save_to_reading_list, get_reading_list | RSS feeds from config, Hacker News, local store | read-only; `save_to_reading_list` local write | yes | `Enabled`, `Feeds` |
| [[pages/services/plugin-notes\|Notes]] | add_note, search_notes, list_recent_notes, delete_note | Scout local store | descriptive write, local | yes | `Enabled` |
| [[pages/services/plugin-nutrition\|Nutrition]] | find_food_product, get_nutrition_facts, get_nutrition_by_barcode | Open Food Facts | read-only | no | `Enabled` |
| [[pages/services/plugin-parcels\|Parcels]] | track_package, watch_package, list_watched_packages, unwatch_package | Ship24 (POST search), local store | read-only intent (one POST); watch list local write | yes | `ApiKey`, `TimeZone` |
| [[pages/services/plugin-recipes\|Recipes]] | find_recipes_by_ingredient, get_recipe, suggest_random_recipe | TheMealDB | read-only | no | `Enabled` |
| [[pages/services/plugin-reminders\|Reminders]] | add_reminder, list_reminders, complete_reminder, delete_reminder | Scout local store | descriptive write, local | yes | `Enabled`, `TimeZone` |
| [[pages/services/plugin-selfinfo\|SelfInfo]] | where_installed, system_info, resource_usage, active_configuration, worker_status | host OS and whitelisted config | read-only | no | `Enabled`, `AdminUserIds`, `DmOnly` |
| [[pages/services/plugin-sports\|Sports]] | get_last_result, get_next_fixture, get_league_table | TheSportsDB | read-only | no | `Enabled`, `FootballDataToken`, `TimeZone` |
| [[pages/services/plugin-weather\|Weather]] | get_current_weather, get_forecast, get_air_quality | Open-Meteo | read-only | no | `Enabled`, `DefaultCity`, `TimeZone` |
| [[pages/services/plugin-websearch\|WebSearch]] | search_web, get_instant_answer, fetch_page | SearXNG (config), DuckDuckGo, any URL for `fetch_page` | read-only | no | `Enabled`, `SearxUrl` |
| [[pages/services/plugin-whattowatch\|WhatToWatch]] | search_show, get_next_episode, recommend_shows, where_to_watch | TVMaze, TMDB (optional) | read-only | no | `Enabled`, `TmdbApiKey`, `DefaultCountry`, `TimeZone`, `MaxResults` |
| [[pages/services/plugin-wikipedia\|Wikipedia]] | search_wikipedia, get_article_summary, get_on_this_day | Wikimedia API | read-only | no | `Enabled` |
| [[pages/services/plugin-worldclock\|WorldClock]] | current_time_in_city, convert_time_between_cities, time_difference_between_cities | none (system time zones) | read-only | no | `Enabled`, `DefaultTimeZone` |

Two other projects also contribute kernel functions and are not plugins in this sense: Skills (`list_skills`, `load_skill`, `src/Scout.Skills/SkillsPlugin.cs:27,40`) and Knowledge (`search_project`, `find_symbol`, `list_projects`, `src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26,41,53`).

## Things a reader should know

- **Only external POST:** Parcels `track_package` sends a Ship24 search POST (`src/Scout.Plugin.Parcels/ParcelsClient.cs:28`); the provider documents it as creating no tracker.
- **Model-chosen URL:** WebSearch `fetch_page` GETs any http or https URL the model supplies; no private-address block was found (`src/Scout.Plugin.WebSearch/WebSearchPlugin.cs:178,191`).
- **Data egress:** CodeReview sends code and fetched file contents to the configured LLM provider (`src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:50`).
- **Opt-in:** SelfInfo is the only plugin that defaults to off (`src/Scout.Plugin.SelfInfo/SelfInfoServiceCollectionExtensions.cs:43`); Parcels and GitHub are off until a credential is set; CodeReview has no enable flag.

## Registration and discovery

Each plugin ships one `Add<Name>Plugin(IServiceCollection, IConfiguration)` extension. It binds `Scout:Plugins:<Name>`, returns early when the plugin is disabled or its credential is blank (self-gating), registers a typed `HttpClient` if it has one, and adds a `KernelPlugin` singleton built by `KernelPluginFactory.CreateFromObject` (for example `src/Scout.Plugin.Weather/WeatherServiceCollectionExtensions.cs:37,53`). WebSearch trims `search_web` out of its plugin when no SearXNG URL is set (`src/Scout.Plugin.WebSearch/WebSearchServiceCollectionExtensions.cs:67`).

The host calls every extension by name in `src/Scout.Host.Console/HostComposition.cs:80-121`. There is no assembly scanning and no plugin directory. `Scout.Llm` builds the one shared kernel and adds every `KernelPlugin` it finds in DI (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:96`), so a registered plugin is visible to the model with no further step. Plugin.Common supplies the shared store and clock, registered idempotently by whichever plugin needs them first (`src/Scout.Plugin.Common/ScoutPluginCommonServiceCollectionExtensions.cs:22`).

`FeatureCatalog` (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:7-13`) is a hard-coded list of three features for the desktop UI: `github`, `kubernetes`, `code-review`. The activator that would attach and detach plugins at run time defaults to `NullFeatureActivator` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:69`), whose `CanActivate` is always false (`src/Scout.ControlPlane/Features/IFeatureActivator.cs:31`). No Kubernetes plugin exists under `src/`.

## Against "pluggable by configuration"

- Configuration only switches an already-compiled plugin on or off. Adding or removing a plugin needs a project reference (`src/Scout.Host.Console/Scout.Host.Console.csproj:19-41`) and a line in `HostComposition.cs`; the host is therefore coupled to all 23 plugin assemblies.
- Toggling at run time is not wired: the feature activator is the null one and the catalogue covers three of 23 plugins. Credentials entered in the UI stay in memory only (`src/Scout.Host.Console/HostComposition.cs:127`).
- CodeReview depends on the GitHub plugin's configuration key by string, not by project reference.
- Related pages: [[pages/services/control-plane]], [[pages/services/llm]], [[pages/services/host-console]], [[pages/architecture/project-map]].
