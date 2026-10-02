# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Vendor tooling only. Nothing in the shipped package changes.

### Added

- **Licence fulfilment** (`tools/NetTriage.Fulfilment`). Turns a paid Polar order into a delivered
  licence file with no human in the loop: it reads paid orders since its cursor, mints the licence
  locally with the private key, emails it to the customer, and records it. Renewals are handled by
  the same path, because Polar turns a renewal into another paid order and the expiry comes from
  the subscription's new period end. It is idempotent, it stops at the first failure instead of
  skipping ahead, and it exits non-zero so a scheduled run cannot fail silently.
- `replay` and a `file` mail mode, so the whole pipeline can be rehearsed over recorded orders
  without contacting a customer or a mail server.

## [0.2.1] - 2026-10-02

Packaging fix. No behaviour change; the detectors, the analysis and the licence format are
untouched.

### Fixed

- The README pointed `git clone` at a placeholder owner, and the package declared no repository
  URL, so nuget.org showed no link to the source. Both now name the published repository, and
  the README carries NuGet, licence and CI badges.

## [0.2.0] - 2026-10-02

Adds the licensed edition. The free edition is unchanged and is not gated: the same detectors,
sequencing, effort model and four text formats run with no licence, no account and no network.

### Added

- **Licensing** (`NetTriage.Core/Licensing`). Licences are signed files, verified locally against
  a public key compiled into the binary, so they work air-gapped. ECDsa P-256 with SHA-256,
  because Ed25519 does not exist in the .NET base class library. No activation server, no
  telemetry. `license status`, `license show`, `license install`.
- **Baseline and drift gate** (`Baseline`). `baseline save` records how many times each detector
  fired in each project; `scan --baseline … --fail-on-new red|yellow|any` fails only on new
  findings. The baseline stores counts and root-relative paths, so ordinary edits — including
  ones that move every line number — do not invalidate it.
- **Estate roll-up** (`Estate`). `estate <dir>` treats each topmost directory holding a solution
  or project file as an application, gives it a stable identity derived from its git remote,
  ranks the applications worst-first by a disclosed risk score, and rolls the numbers up.
  `--history <file>` appends a snapshot and reports the trend. The history is local; there is no
  server.
- **Custom rules** (`Rules`). `-r <file>` adds customer-defined detectors using the same matching
  engine as the built-ins, overrides the severity of built-in detectors, and allowlists findings
  with a reason and an optional expiry date. Suppressed findings are listed in the report and an
  expired entry stops applying and says so.
- **Organisation exports** (`Reporting`). `--format xlsx` writes a filterable OOXML workbook with
  no third-party dependency; `--format summary` writes a one-page executive brief.
- **`GlobMatcher`**, a small glob engine supporting `*`, `**` and `?`, case-insensitive, treating
  both separators as separators.
- `tools/NetTriage.LicenseIssuer`, the publisher-side tool that mints licences. Kept in the
  repository so the trust model is auditable; the private key is not.
- 96 further tests (159 in total), covering signing, tampering, expiry, baseline diffing, rule
  loading and application, estate identity and trend, and the workbook's XML.

### Changed

- `--format` gains `xlsx` and `summary`, both licensed.
- Exit code `3` is now returned when a licensed feature is requested without a valid licence.
- `--max-findings` default corrected to 6, which is what the CLI had always used.
- `EstateOptions.Exclude` was accepted and documented but never applied; it now filters both the
  application boundary and the projects inside each application.
- Applying an allowlist now re-derives each project's bucket and the report summary, so
  suppressing a project's only blocker moves it out of red instead of leaving a headline that
  contradicts the findings list underneath it.
- `BaselineStore` requires the format marker to be present in the file. `Baseline.Format` has a
  default, so deserializing alone accepted any JSON object as an empty baseline — which would then
  have reported every project as new.
- A glob ending in `**` now matches everything under its prefix, including a file directly in it;
  previously `src/Printing/**` did not match `src/Printing/Label.cs`.
- An estate application whose directory is a generic container (`src`, `source`, `code`, …) is
  named after its parent, so a repository is no longer called "src".

### Notes

- No network access, no telemetry, no account, still. The licence check reads one local file.
- Free features are never gated: without a licence the tool falls back to the free behaviour
  rather than refusing to run.

---

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
