---
name: A Contracts edit resolves to the wide test surface
runs: 2
max_turns: 8
timeout_seconds: 300
tags: ["gate-4"]
plugins: ["../.."]
---
Do not edit any file. This task changes a record in src/Scout.ControlPlane.Contracts that the hosts, the control plane and the desktop UI all consume. Per this repo's pipeline, name every test surface that must run green before the change is done (gate 4). Then stop.
