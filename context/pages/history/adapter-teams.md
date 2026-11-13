---
title: "Adapter.Teams - history"
version: "1.0"
date: 2026-11-09
changes: "Initial history note: mention by id and its tag stripped, a Begin without a stream id, Stop detaching the handler first, an unreadable mention entity kept inside the adapter"
page_type: reference
status: artefact
description: "What changed in the Teams adapter and when."
source:
  - chat
  - src/Scout.Adapter.Teams/TeamsChatAdapter.cs
  - src/Scout.Adapter.Teams/TeamsAdapterLifecycle.cs
  - src/Scout.Adapter.Teams/TeamsMessageTranslator.cs
  - src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs
  - tests/Scout.Adapter.Teams.Tests/
reliability: high
---

# Adapter.Teams - history

> Dated changes to [[pages/services/adapter-teams]]; the service page states how it works now.

## 2026-11-11 - A final-only sink declares no fine-grained editing

- **Asked for:** an automated audit of main (issue #250, "Final-only sinks advertise fine-grained editing"): `TeamsResponseSink.Capabilities.FineGrainedEdit` was true even with streaming off, where Begin and Update write nothing, so the pump scheduled interim renders the sink dropped.
- **What changed:** the capability is the sink's `streamingSupported` argument. The Slack and Discord sinks always edit their message, so their constant true stays.
- **Tests:** `A_Final_Only_Sink_Does_Not_Advertise_Fine_Grained_Editing` in `TeamsResponseSinkTests.cs` (streaming on and off), red on the off case with the constant put back.

## 2026-11-10 - The handler published with the running state

- **Asked for:** the first sentence of Scout's own finding "Lifecycle state and handler publication are not atomic": a turn racing a start can observe a null handler and be silently dropped. The 2026-11-09 change fixed the stop half; the start still marked the lifecycle running before the adapter stored the handler.
- **What changed:** the handler moved from the adapter into `TeamsAdapterLifecycle`. `Start(handler, hostToken)` stores it with the lifetime token and the running state under the lifecycle's lock; `TryGetSession` reads the three in one step and replaces `TryGetLifetimeToken`; `StopAsync` clears the running state and the handler in one step before it waits for the cancellation. The adapter's turn path reads the session once, and its stop only logs when the lifecycle stopped.
- **Tests:** `The_Lifecycle_Reads_As_Running_Only_With_The_Handler_Of_That_Start_Before_A_Start_After_A_Stop_And_After_A_Restart` in `TeamsAdapterLifecycleTests.cs`, red with the start not storing its handler; `A_Message_While_Running_Reaches_The_Handler_Of_That_Start_And_One_After_Stop_Reaches_None_And_Never_Throws` in `TeamsChatAdapterTests.cs`, through the adapter, red with the adapter's stop leaving the lifecycle running, and green with the adapter's turn path put back to a two-step read. Proved by a test: the adapter reads the running state and the handler in one step from the lifecycle, a message after stop is dropped, and a restart's handler gets the next message. `A_Turn_Racing_Start_Is_Never_Dropped_For_A_Missing_Handler` in `TeamsChatAdapterTests.cs` delivers a turn from `SessionPublished`, an internal hook the lifecycle runs as the last step inside `Start`'s lock (null outside tests, reached through an internal `Lifecycle` accessor on the adapter), and asserts that the lifecycle read as running and that the handler got the message. It is red on the code before the fix, where the turn saw the lifecycle running before the adapter stored the handler and was dropped, and red with the new lifecycle storing the handler after the hook. The turn runs on the start's own thread, through the re-entrant lock, because a turn on another thread would block on that lock while the hook waited for it. Proved by a test: a turn racing a start is never dropped for a missing handler, and the handler is published no later than the running state. Proved only by reading the code: that the lock keeps a turn on another thread out of a start's publication. The other lifecycle tests pass the handler to `Start` and read through `TryGetSession`; `A_Start_While_A_Stop_Is_Unwinding_Keeps_Its_Handler_After_That_Stop_Returns` and `Two_Concurrent_Stops_And_A_Start_Never_Leave_A_Running_Adapter_Without_Its_Handler` green before and after.

## 2026-11-09 - Mention by id, a Begin without a stream id, Stop detaching the handler first

- **Asked for:** Scout's own audit of this repository (issues "A missing begin response id leaves an invalid stream active", "Any mention can address the bot when its display name is absent" and "Lifecycle state and handler publication are not atomic").
- **What changed:**
  - `TeamsResponseSink.BeginAsync` treats an informative activity answered with a null or empty resource id like a refused stream: one Warning, no interim update on the wire, and a plain final message without a `streaminfo` entity. Before, every update went out as a streaming activity with no stream id to echo.
  - `TeamsMessageTranslator` accepts a group or channel message when one of its mention entities carries the bot's account id, projected from the activity as `InboundTeamsMessage.MentionedIds`, or when the text holds the bot's display name in an `at` tag. The fallback that took any `at` tag as the bot's when the activity carried no recipient name is gone, so a mention of someone else is never for the bot.
  - `TeamsChatAdapter.StopAsync` clears the handler before it waits for the lifecycle's stop. Before, it cleared it after the wait, so a `StartAsync` that ran while the stop was unwinding had its handler cleared by that stop and the adapter was left running with none.
  - The translator strips the bot's mention by the tag text of the mention entity that carries the bot's id, projected as `InboundTeamsMessage.RecipientMentionTexts`, and then by the display name as before. Before, a mention accepted by id whose tag differed from `Recipient.Name` reached the model with the raw `at` tag.
  - `TeamsChatAdapter.ProcessActivityAsync` projects the activity inside its try. The projection now reads the mention entities through the SDK, which throws a `JsonSerializationException` on an entity it cannot convert; outside the try that failure escaped into the webhook host. It is now logged at Error with the activity id, and the turn ends without work.
- **Not changed:** the handler and the lifecycle are still two fields, not one gate. The order alone holds the rule: every stop clears the handler before its own lifecycle stop, and every start stores its handler after its own lifecycle start, so however two stops and a start interleave, an adapter left running has the handler of the start that armed it. A turn that arrives between a start arming the lifecycle and storing its handler returns without work, as a turn before the start does. A stop that races a start which has not returned can leave the stopped adapter holding that start's handler until the next start replaces it; a stopped adapter dispatches nothing. A Begin that fails with a 5xx or a reset still escapes to the caller, and a 403 `ContentStreamNotAllowed` still downgrades to one final message.
- **Tests:** `A_Begin_Answered_Without_A_Stream_Id_Sends_No_Update_And_Ends_With_A_Plain_Message` (null and empty) and the Rule I test `A_Begin_The_Service_Fails_With_A_5xx_Escapes_To_The_Caller` in `tests/Scout.Adapter.Teams.Tests/Streaming/TeamsSinkBoundaryTests.cs`; `A_Group_Message_Mentioning_Someone_Else_Is_Not_For_The_Bot_When_Its_Display_Name_Is_Absent` (null, empty and blank name), `A_Group_Message_Mentioning_The_Bot_By_Its_Id_Is_Accepted_When_Its_Display_Name_Is_Absent` `ProjectInbound_Maps_The_Ids_Of_The_Mention_Entities`, `A_Mention_Accepted_By_Id_Is_Stripped_By_Its_Own_Tag_When_The_Tag_Differs_From_The_Bots_Display_Name` and `A_Mention_Entity_Of_The_Bot_That_Carries_No_Tag_Text_Never_Fails_And_The_Name_Tag_Is_Still_Stripped` in `tests/Scout.Adapter.Teams.Tests/TeamsMessageTranslatorTests.cs`; `A_Start_While_A_Stop_Is_Unwinding_Keeps_Its_Handler_After_That_Stop_Returns` and `Two_Concurrent_Stops_And_A_Start_Never_Leave_A_Running_Adapter_Without_Its_Handler`, 10,000 raced rounds, `A_Group_Mention_Matched_By_Id_Reaches_The_Handler_Without_Its_Tag_When_The_Tag_Differs_From_The_Bots_Name` and `An_Activity_Whose_Mention_Entity_Cannot_Be_Read_Never_Fails_The_Webhook_And_Is_Logged_With_Its_Id` in `tests/Scout.Adapter.Teams.Tests/TeamsChatAdapterTests.cs`. `Update_5xx_Is_Dropped_Terminal_Still_Coherent` now answers its Begin with a stream id, because without one no update is sent.
- **Invariants:** [[pages/invariants/pluggable-boundaries]] holds; the mention ids and tag texts cross into the translator's record as strings and no Bot Framework type leaves the adapter project. [[pages/invariants/no-executive-writes]] holds; the only platform writes are still the reply activities.

## Version notes: `pages/services/adapter-teams.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- A sink for a group chat or channel, where streaming is off, declares no fine-grained editing, so the pump sends it no interim render and writes only the final (2026-11-11). Earlier: The handler lives in the lifecycle: a start publishes it with the running state and a turn reads both in one step, so a turn that sees the adapter running is always dispatched to the handler of that start; a stop detaches it in the same step; a turn racing a start is proved never dropped by a test through an internal hook (2026-11-10). A test of a failed reply over the limit through the real pump: its first part, one truncation marker, the footer (2026-11-10). A group message counts as addressed to the bot by a mention entity carrying its id or its display name, never by another user's mention, and the tag of the entity that matched by id is stripped whatever it says; an activity whose mention entity cannot be read is logged and never fails the webhook; a Begin answered without a stream id falls back to one final message; Stop detaches the handler before it waits, so a restart is never left without one (2026-11-09). Earlier: no origin reference and no origin poster, so no later posts (ADR 0033)
