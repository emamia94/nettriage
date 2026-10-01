# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-10-01

First release. The scope is deliberately narrow: deterministic, offline triage.

### Added

- `nettriage scan <path>` accepting a `.sln`, `.slnx`, a single project, or a directory tree.
- 33 detectors across hard blockers, warnings and informational findings, each with a stable
  code, a rationale, and an effort baseline.
- Source scanning for C# and VB.NET, with comments and string literals stripped before
  matching so documentation is never reported as a usage.
- `using`-import resolution: `Page` is matched when `System.Web.UI` is imported, and not
  otherwise.
- Target framework lifecycle checks against published end-of-support dates, including the
  "approaching end of support" warning.
- `Directory.Build.props` / `Directory.Build.targets` inheritance, so solutions that declare
  their target framework once are read correctly.
- Dependency-graph sequencing: shared libraries first, consumers after, with a reason and an
  action per step.
- Disclosed heuristic effort model with a low/high range.
- Four output formats: `console`, `json`, `markdown`, `html`.
- `--fail-on red|yellow` for use as a CI gate.
- 62 unit and end-to-end tests.

### Notes

- No network access, no telemetry, no licence check. There is no paid tier.
- Analysis is lexical. See the Limitations section of the README for what that excludes.
