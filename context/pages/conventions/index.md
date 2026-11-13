---
title: "Index: conventions"
version: "1.0"
date: 2026-11-13
changes: "Generated from the page headers."
page_type: index
source: ["context/pages/conventions/"]
---

# Conventions

- [[pages/conventions/decisions-that-look-like-defects|Decisions that look like defects]] - Behaviour in Scout that reads as a bug and is intended: what it is, why it is right, where it lives and the test that locks it. A reviewer or an audit checks this page before reporting; a finding that matches an entry is not a finding.
- [[pages/conventions/desktop-prototype|Desktop prototype]] - The clickable prototype of Scout Desktop is kept in context/prototypes/desktop, one artboard per screen; every change to a screen, and every new screen, updates or adds its artboard in the same branch, before the code.
- [[pages/conventions/testing|Testing]] - How Scout is tested: xUnit with Shouldly and NSubstitute, one test project per source project, warnings as errors, and the CI steps that run them.
- [[pages/conventions/tooling|Tooling]] - Who runs on which model and writes what, what lives in .claude/ and scripts/, what each hook does, and how the review workflow and the diagnostics script fit the gates.
