# Third-party notices

Scout is released under the MIT licence (see `LICENSE`). This file lists the material in this
repository that comes from other projects, where each one is published, and the licence it is used
under. An entry is added in the same change that brings the material in.

## Wiki skills

- **What:** the skills under `.claude/skills/wiki-crystallize`, `wiki-ingest`, `wiki-integrate`,
  `wiki-lint`, `wiki-query` and the reference files under `.claude/skills/wiki-shared`. They are
  used while developing Scout to keep the project context under `context/`; they are not part of
  the Scout application.
- **From:** `llm-wiki-skills`, published at <https://github.com/vanillaflava/llm-wiki-skills>.
- **Changes made here:** the skills are kept as published, apart from small edits in each
  `SKILL.md`. The base of `context/wiki-schema.md` (the default page types and frontmatter fields)
  also comes from that project's schema file.
- **Its own credit:** that project names Andrej Karpathy's llm-wiki note as the origin of the
  pattern, <https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f>, and follows the
  Agent Skills specification, <https://agentskills.io/specification>.
- **Licence:** MIT.

```
MIT License

Copyright (c) 2026 Axel Tölke

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
