# Changelog

User-facing changes to PerfLint for Unity. This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased] — pre-release

PerfLint is in pre-release while subscriptions are being finalized. What works today:

- **Local scan** across Performance, Assets, Migration, and Project Settings — one click, runs entirely on your machine.
- **Mobile-focused checks**: textures whose compression silently fell back to uncompressed, Multithreaded Rendering, Optimize Mesh Data, stereo clips that could ship as mono, and oversized fonts — each with a one-click fix where it's safe.
- **Project health score (0–100)** and a self-contained, shareable **HTML report**.
- **One-click & batch fixes** for safe, deterministic issues (Pro).
- **Merge duplicate assets** (Pro): keep one copy, redirect every reference across the project, then delete the rest — one click. Pick which copy to keep (defaults to the most-referenced one); guarded and previewed before it runs; commit to version control first.
- **AI Fix & Explain** — zero-config out of the box, or bring your own API key. Every AI change is shown as a diff for review and is compile-verified with automatic rollback.
- **Local-first, zero telemetry** — your code and art assets are never uploaded.

A **1.0.0** release will follow once paid subscriptions go live; from then on each release will be listed here.
