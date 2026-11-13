---
title: "Desktop operator window: Reports"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop operator window service page."
page_type: service
status: active
description: "Read when a change touches: Reports."
source: - "context/pages/services/desktop-ui.md" - ui/packages/main/src - ui/packages/renderer/src/app/production - ui/packages/renderer/src/app/assembly - ui/packages/renderer/src/app/reports - ui/packages/renderer/src/app/experiments - ui/packages/renderer/src/app/comments - ui/build
reliability: high
---

## Reports

The Reports page (route `operator/reports`, `ui/packages/renderer/src/app/app.routes.ts:102`; `ReportsComponent`, `ui/packages/renderer/src/app/reports/reports.component.ts:262`) lists the projects whose publication has a video id (`:367`), newest first, and reads them on open, on Refresh and every 10 s (`:21`). It draws `Reports.dc.html`: a loading card until the first read answers, "No video is on YouTube yet. Publish one from the Assembly page." with Go to Assembly when there is none, and a failed read as an alert over the last list. The picker chooses the newest video until the owner picks another, which the refresh keeps; each option reads "<topic> · published <yyyy-MM-dd HH:mm>" in London, "scheduled" with the publish time for a scheduled video and "uploaded" for any other. For the chosen one it shows:

- **First 24 hours and First 7 days** - one card per window, "Taken <yyyy-MM-dd HH:mm> London time" and the numbers as the APIs returned them as tiles, nothing derived (`:303`): views, minutes watched, average view duration in seconds, average view percentage, subscribers gained and lost, likes, comments, shares, and impressions and their click-through rate only when the snapshot has them. "No <window> numbers yet." without a snapshot.
- **Audience retention** - an inline SVG on a 420 by 170 view box, the plot from x 40 to 410 and y 10 to 140 with three gridlines, the points as returned as a polyline (`:323`), x the elapsed ratio and y the audience watch ratio scaled to the larger of 1 and the highest ratio, so a ratio above 1 is not clipped and the top gridline is labelled with that top; a dashed line marks where each scene ends, from the scenes' durations in their order, without the start and the end (`:333`); under it 0:00, half and the whole of those durations, and the legend.
- **What Scout proposes** - each proposal with "New <kind>", its text, a status chip (Waiting for you, Accepted, Rejected or Tested, else the status as stored) and its reason; a thumbnail proposal shows its picture through `scout-media` with `?v=<updatedAt>` (`:141`), which serves `thumbnail-proposal-<id>.png` under the unchanged name rules; Accept and Reject only on a `proposed` one (`:146`) send `scout:operator.proposalDecision` (`:352`) with the chosen video's id; while one is out every Accept and Reject is off and "Saving your decision..." shows on that proposal, and a refusal shows its reason under the heading.
