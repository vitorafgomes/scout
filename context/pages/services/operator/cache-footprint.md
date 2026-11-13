---
title: "Operator: Cache footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Operator service page."
page_type: service
status: active
description: "Read when a change touches: Cache footprint."
source: ["src/Scout.Operator/", "tests/Scout.Operator.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md", "chat"]
reliability: high
---

## Cache footprint

None. The operator keeps no state of its own besides the in-process search, picture and Openverse counters, the hand-over channel, the video id of an upload not saved yet, the reply id of a reply not saved yet and which reply actions this process knows unposted, the draft requests of the last failed drafts run, the slots the scheduler ran, whether rhubarb answered, the ONNX sessions of the two face models, kept for the life of the process, and whether `FaceMouth` has logged its failure; every project lives in the control plane, and its media files in the directory the control plane names.
