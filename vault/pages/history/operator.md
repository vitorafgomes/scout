---
title: "Operator - history"
version: "1.0"
date: 2026-11-05
changes: "Initial history note: the FaceMouth acceptance run of phase 8c-B"
page_type: reference
status: artefact
description: "What changed in the channel operator worker and when, and the dated acceptance runs the service page points to."
source:
  - chat
  - src/Scout.Operator/FaceMouth.cs
reliability: high
---

# Operator - history

> Dated changes and acceptance runs of [[pages/services/operator]]; the service page states how it works now.

## 2026-11-05 - FaceMouth acceptance (phase 8c-B)

- **Asked for:** the plan's acceptance for the automatic mouth detection: a real `FaceMouth` run on the four built-in presenters and two generated-style portraits, every detected box within 0.10 of the picture on each edge, recorded in the vault rather than in a unit test.
- **Result:** every picture had one face found; every built-in box within 0.048 of its hand-measured box.

Run on 2026-11-05 with the real `FaceMouth`, the real `ProcessRunner`, the bundled ffmpeg of Scout Desktop's linux-x64 media tools and ONNX Runtime 1.31.1, from a throwaway test that is not in the tree. Models: `blazeface-short-range.onnx` SHA-256 `02a04d5d37c3558dc4d5274f7f8f0f0f01ac94e46c5ffb2cee82395d47e23181`, `face-landmarker.onnx` SHA-256 `f38c3321ceffbc9e95103480ad38cc3f52e7e1bde2bcee7cd9355d0b9138ac0c`, the digests the desktop's notice records. Read from the sessions' metadata, all float32: the detector's `input` [1,128,128,3], `regressors` [1,896,16] and `classificators` [1,896,1]; the mesh's `input_12` [-1,256,256,3], `Identity` [-1,1,1,1434], `Identity_1` [-1,1,1,1] and `Identity_2` [-1,1], a dynamic batch that `FaceMouth` fills with 1; the names and shapes match what the code assumes. `face_detection_available` was true. The built-ins are 1024x1024 RGBA whose transparent background decodes to black; the two others were drawn for the run with Pillow in another style: a round face with dark skin, short black hair and round glasses on a light blue 1280x960 background, and a long narrow face with pale skin and red hair on a warm 768x1024 background. Boxes are left, top, right and bottom as fractions of the picture; the distance is to the `presenters.json` box, (0.389, 0.534) to (0.613, 0.645) for all four.

| Picture | Found | Box found | Largest edge distance |
|---|---|---|---|
| ada.png | yes | 0.413, 0.545, 0.579, 0.611 | 0.034 (right, bottom) |
| kofi.png | yes | 0.434, 0.563, 0.570, 0.612 | 0.045 (left) |
| mei.png | yes | 0.436, 0.562, 0.565, 0.614 | 0.048 (right) |
| rafa.png | yes | 0.437, 0.561, 0.565, 0.612 | 0.048 (left, right) |
| round face, glasses | yes | 0.430, 0.531, 0.569, 0.585 | drawn mouth 0.448, 0.539, 0.552, 0.563 |
| long face, red hair | yes | 0.417, 0.458, 0.586, 0.539 | drawn mouth 0.438, 0.481, 0.563, 0.501 |

Every picture had one face found and every built-in box is within 0.048 of its hand-measured box on each edge, inside the plan's 0.10. The hand-measured boxes are larger than the found ones and identical for the four presenters; the found boxes sit on the drawn lips, padded 15 percent. No threshold was changed for the run.
