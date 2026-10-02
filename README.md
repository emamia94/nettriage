# nettriage

**Deterministic, offline triage for .NET Framework → modern .NET migrations.**

[![NuGet](https://img.shields.io/nuget/v/NetTriage.svg?label=nuget)](https://www.nuget.org/packages/NetTriage)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/emamia94/nettriage/blob/main/LICENSE)
[![CI](https://github.com/emamia94/nettriage/actions/workflows/ci.yml/badge.svg)](https://github.com/emamia94/nettriage/actions/workflows/ci.yml)

Point it at a solution, a project, or a whole directory tree. It tells you which projects
will actually fight you, why, how much of the work is architectural rather than mechanical,
and what order to do them in.

```
dotnet tool install -g NetTriage
nettriage scan ./MyCompany.sln
```

---

## What this is not

**This is not a code converter.** It does not rewrite your C#. Plenty of tools already do
that, and they all stop at the same place: somewhere between 50% and 90% of the way through,
at the point where a human has to decide what the application is supposed to do now that
`AppDomain` no longer exists.

The hard part of a migration is not the rewriting. It is knowing *which* of your 40 projects
to start with, which ones are actually cheap, and which ones are three-month rewrites
disguised as a version bump. That decision is made before any conversion tool is useful, and
it is made on incomplete information in almost every organisation.

nettriage answers that question, deterministically, in under two seconds, with no network
access and no account.

---

## Why now

- **.NET 8 and .NET 9 reach end of support on 10 November 2026.** .NET Framework 4.6.2 follows
  on 13 January 2027. This is not a "someday" problem any more.
- Microsoft **deprecated the .NET Upgrade Assistant** (`dotnet/upgrade-assistant` →
  `dotnet/modernize-dotnet` → both retired), and its replacement requires a GitHub Copilot
  subscription per developer. The free, deterministic path out of .NET Framework was removed.
- The remaining commercial tools are aimed at the enterprise top end. A team of eight
  developers with thirty applications has no cheap way to answer "what are we in for?"

nettriage is that answer, and it is free.

---

## Install

```bash
dotnet tool install --global NetTriage
```

Requires the .NET SDK (8.0 or later). Nothing else — no Roslyn, no NuGet packages, no
network access at runtime.

---

## Usage

```bash
nettriage scan <path> [options]          # assess one solution, project or directory
nettriage baseline save <path> [options] # record the current state, to gate on drift
nettriage baseline show <file>           # summarise a recorded baseline
nettriage estate <dir> [options]         # roll up many applications into one portfolio
nettriage rules validate <file>          # check a custom rule file
nettriage rules template [file]          # print a starter rule file
nettriage license status                 # show licence state and features
nettriage license install <file>         # install a licence file
nettriage detectors                      # list every check with its rationale
nettriage version
nettriage help
```

`<path>` may be a `.sln`, a `.slnx`, a `.csproj`/`.vbproj`, or a directory. Given a
directory, it walks the tree looking for projects.

### Options

| Option | Meaning |
| --- | --- |
| `-f, --format <fmt>` | `console` (default), `json`, `markdown`, `html` — plus `xlsx` and `summary` with a licence |
| `-o, --output <file>` | write the report to a file |
| `-x, --exclude <glob>` | skip matching paths; repeatable |
| `--fail-on <level>` | `none` (default), `red`, `yellow` — exit `2` when met |
| `--max-findings <n>` | findings shown per project (default 6) |
| `--no-color` | disable ANSI colour |
| `-q, --quiet` | suppress the report on stdout |
| `-r, --rules <file>` | apply a custom rule file — **licensed** |
| `-b, --baseline <file>` | compare against a recorded baseline — **licensed** |
| `--fail-on-new <level>` | `none` (default), `red`, `yellow`, `any` — exit `2` on new findings only — **licensed** |

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | scan completed |
| `1` | bad arguments, or the path could not be read |
| `2` | `--fail-on` or `--fail-on-new` threshold was met |
| `3` | a licensed feature was requested without a valid licence |

---

## Free and licensed

Everything that answers *"what is wrong with this application?"* is free, permanently, with no
account and no network: all 33 detectors, sequencing, effort estimates, and console / JSON /
Markdown / HTML output.

The licensed edition answers the questions that come **after** that one. It is a single annual
price per organisation, and it unlocks four things:

| | What it does |
| --- | --- |
| **Baseline and drift gate** | `baseline save` records how many times each detector fired in each project. `scan --baseline … --fail-on-new red` then fails CI only on findings that are *new*. Without it, `--fail-on` is all-or-nothing, which on a large legacy estate means the gate is switched off on day one. |
| **Estate roll-up** | `estate <dir>` scans many applications, gives each a stable identity derived from its git remote, and ranks them worst-first by risk. `--history <file>` appends a snapshot and shows the trend, so "is it getting better?" has an answer. |
| **Custom rules** | `rules validate` and `-r <file>` add your own detectors using the same matching engine as the built-ins, override the severity of built-in ones, and allowlist findings with a reason and an expiry date. Suppressed findings are listed in the report, never dropped silently. |
| **Organisation exports** | `--format xlsx` writes a filterable workbook (summary, projects, findings, sequence); `--format summary` writes a one-page executive brief. |

The licence is a signed file verified locally against a public key compiled into the binary.
There is no activation server, no phone-home, and no account — it works air-gapped. Free
features are never gated: a lapsed licence returns the tool to the free behaviour, it does not
stop it working.

`nettriage license status` always exits `0`; use `nettriage license show <file>` when you want a
non-zero exit for an invalid licence in a script.

---

## Example

```
$ nettriage scan ./LegacyDemo.sln

nettriage 0.1.0  ·  deterministic .NET modernization triage

  Root       /work/LegacyDemo
  Scanned    2026-10-01 17:49 UTC
  Projects   7   ·   234 source lines in 12 files
  Mode       offline, deterministic (no network calls)

PORTFOLIO
  Runtime split        .NET Framework 5  ·  modern .NET 2
  Triage               RED 4  ·  YELLOW 2  ·  GREEN 1
  Findings             blockers 8  ·  warnings 10  ·  info 6
  Out of support       1 project(s) target a runtime that receives no security patches
  End-of-support targets net6.0, net8.0
  Effort (heuristic)   69 - 158 engineer-days   (≈ 3 months to 8 months for one engineer)

HARD BLOCKERS  (no supported equivalent - scope these as rewrites)
  NT1001  ASP.NET WebForms                               1 project(s)
  NT1002  WCF service host (server side)                 1 project(s)
  NT1003  .NET Remoting                                  1 project(s)
  NT1006  BinaryFormatter / binary serialization         1 project(s)
  NT2003  Target framework is out of support             1 project(s)

PROJECTS

  RED     LegacyDemo.Domain
          src/LegacyDemo.Domain/LegacyDemo.Domain.csproj
          net48  ·  Library  ·  67 LOC  ·  12-26 engineer-days
          legacy project format
          · net48: .NET Framework 4.8 - Windows component, security fixes only.
          B NT1003 .NET Remoting  ×2  src/LegacyDemo.Domain/LegacyDemo.Domain.csproj:1
          B NT1006 BinaryFormatter / binary serialization  ×3  src/LegacyDemo.Domain/Money.cs:16
          W NT1010 System.Drawing  ×2  src/LegacyDemo.Domain/LegacyDemo.Domain.csproj:1
          i NT2002 packages.config  ×1  src/LegacyDemo.Domain/packages.config:1

  RED     LegacyDemo.Web
          src/LegacyDemo.Web/LegacyDemo.Web.csproj
          net48  ·  ASMX, Web, WebForms  ·  48 LOC  ·  27-61 engineer-days
          B NT1001 ASP.NET WebForms  ×8  Default.aspx:1
          B NT1008 ASMX / SOAP web services (System.Web.Services)  ×1  src/LegacyDemo.Web/LegacyDemo.Web.csproj:1
          W NT1016 System.Web HttpContext  ×7  src/LegacyDemo.Web/LegacyDemo.Web.csproj:1
          W NT1017 System.Web.Caching / HttpRuntime  ×1  src/LegacyDemo.Web/Global.asax.cs:10
          W NT1018 Global.asax / HttpApplication  ×1  src/LegacyDemo.Web/Global.asax.cs:6
          W NT1020 System.Configuration ConfigurationManager  ×3  src/LegacyDemo.Web/Default.aspx.cs:14

SUGGESTED SEQUENCE  (shared libraries first, consumers after)
   1. LegacyDemo.Domain
      Referenced by 4 projects - shared library, migrate it first so consumers can follow.
      Has 2 hard blocker(s), so scope it separately rather than bundling it with the easy work.
      → Scope as a rewrite of the affected layers, not a migration. Run it as its own project
        with its own plan. Consider multi-targeting (current framework + net10.0) so old and
        new consumers can coexist during the transition.
   2. LegacyDemo.Data
      Referenced by 1 project - migrate it before that consumer.
      → Retarget plus targeted refactors. Keep behaviour parity as the success criterion.
   3. LegacyDemo.Tests
      No project references it - safe to move without breaking consumers.
      → Mechanical retarget and package upgrade. Good pilot candidate - use it to calibrate
        the estimate.

  Effort figures are a disclosed heuristic, not a quote. Calibrate on a pilot before
  committing to dates. Analysis is lexical: it detects referenced APIs, not data flow.
```

Every finding cites a file and a line. Every count is reproducible. Nothing is guessed by a
model.

---

## What it detects

33 checks, in three groups.

### Hard blockers — no supported equivalent on modern .NET

| Code | Finding | Why it matters |
| --- | --- | --- |
| NT1001 | ASP.NET WebForms | No successor. The UI layer must be rewritten, not ported. |
| NT1002 | WCF service host (server side) | `CoreWCF` exists but is not a drop-in; bindings and behaviours need rework. |
| NT1003 | .NET Remoting | Removed entirely. Needs a real transport replacement. |
| NT1004 | `AppDomain` creation | Isolated loading is gone; needs `AssemblyLoadContext` or a redesign. |
| NT1005 | Windows Workflow Foundation | Not ported. Rewrite or replace. |
| NT1006 | `BinaryFormatter` | Removed for security; the serialized payload format itself must change. |
| NT1007 | Code Access Security | The whole model was removed. |
| NT1008 | ASMX / `System.Web.Services` | Server-side SOAP hosting is gone; clients can be ported. |
| NT1009 | COM+ / `System.EnterpriseServices` | Windows-only, no modern equivalent. |
| NT2003 | Target framework out of support | Already receiving no security patches. |

### Warnings — ported, but not for free

| Code | Finding |
| --- | --- |
| NT1010 | `System.Drawing` |
| NT1011 | COM interop |
| NT1012 | Windows Registry access |
| NT1013 | WMI / `System.Management` |
| NT1014 | `Thread.Abort` / `Suspend` / `Resume` |
| NT1015 | ASP.NET MVC 5 / Web API 2 |
| NT1016 | `System.Web` `HttpContext` |
| NT1017 | `System.Web.Caching` / `HttpRuntime` |
| NT1018 | `Global.asax` / `HttpApplication` |
| NT1019 | Entity Framework 6 |
| NT1020 | `System.Configuration` `ConfigurationManager` |
| NT1021 | `Assembly.LoadFrom` / `LoadFile` |
| NT1022 | WCF client usage |
| NT1023 | MSMQ / `System.Messaging` |
| NT2004 | Target framework reaches end of support soon |
| NT2005 | Vendored binary reference (`HintPath`) |

### Informational — know about it, act later

NT1024 WPF · NT1025 WinForms · NT1026 P/Invoke · NT1027 reflection-based type loading ·
NT2001 legacy project format · NT2002 `packages.config` · NT2006 no test project in the solution

Run `nettriage detectors` for the full rationale and the effort baseline behind each one.

---

## How the estimate works

The effort model is deliberately simple and fully disclosed, because a black box would be
worse than no number at all:

```
mechanical    = source lines / 1,500 per engineer-day
architectural = Σ (detector baseline × repetition factor × size factor)
range         = 0.7 × total  …  1.6 × total
```

- The **repetition factor** is `1 + ln(1 + occurrences) / 4`, capped at 2.5 — the first
  occurrence carries the structural cost, the 500th does not.
- The **size factor** is `0.35 + 0.65 × min(1, lines / 5000)` — a fifty-line WebForms demo
  should not be priced like a fifty-thousand-line one.
- The floor is two engineer-days.

It is an order-of-magnitude aid for sequencing and for arguing about budget. It is **not a
quote**. Calibrate it against one real project before you commit to anything.

---

## Determinism and privacy

- **No network calls, ever.** There is no `--online` flag in this version. Nothing is
  uploaded, there is no telemetry and no account. The licence check is a signature
  verification against a public key compiled into the binary: it reads one local file and
  nothing else.
- **No code execution.** It parses XML and scans text. It never builds, loads, or runs your
  assemblies.
- **No model in the loop.** The same input produces byte-identical output, which is what
  makes it usable as a CI gate.
- **Comments and string literals are stripped before matching**, so a comment mentioning
  `BinaryFormatter` is not reported as a `BinaryFormatter` usage.

---

## Limitations

Stated plainly, because you will find them:

- **Lexical, not semantic.** Detection is based on referenced names, not on a type system.
  A method called `BinaryFormatter` on your own class is indistinguishable from the framework
  one. This is the deliberate trade for zero dependencies and a two-second scan.
- **Instance calls are not resolved.** `myThread.Abort()` is not matched; `Thread.Abort` is.
  Namespace-qualified static access and `using`-imported type names are.
- **NuGet packages are listed but not verified** against their supported target frameworks.
  Offline, that would require the network. It is the top item on the roadmap.
- **Effort is a heuristic**, as described above.
- **C# and VB.NET only.** F# projects are discovered but their sources are not scanned.
- **Reported paths come from the project files.** Separators are normalised and a
  case-insensitive lookup is attempted, so a project written on Windows scans correctly on
  Linux. If a repository contains two directories differing only in case — which Windows would
  treat as one — the path reported is the one the project declares.
- MSBuild evaluation is approximated: `Directory.Build.props` inheritance is followed, but
  arbitrary `Condition` expressions and property functions are not evaluated. When a target
  framework is inherited, the report says so.

---

## CI usage

Fail a pull request that introduces an unsupported target framework:

```yaml
- name: Check .NET modernization posture
  run: |
    dotnet tool install --global NetTriage
    nettriage scan ./MySolution.sln --fail-on red --format markdown -o triage.md
```

Or keep a machine-readable report as an artifact:

```bash
nettriage scan ./src --format json -o nettriage.json
```

The JSON schema is stable and camelCase; see `ScanReport` in `NetTriage.Core/Model.cs`.

---

## Building from source

```bash
git clone https://github.com/emamia94/nettriage
cd nettriage
dotnet build NetTriage.slnx -c Release
dotnet run --project src/NetTriage.Cli -- scan .
```

Tests (159 of them, no network, no fixtures on disk):

```bash
dotnet run --project tests/NetTriage.Tests
```

---

## Roadmap

- Optional online enrichment (`--online`) that verifies each package's declared target
  frameworks against nuget.org, so "this dependency already abandoned .NET Framework" stops
  being a manual check. This belongs in the free edition: it is an answer to the same
  question, and friction there would cost more than it earns.
- A stable JSON schema version field, and a documented schema file.
- Effort-model calibration notes from real migrations, published as a reference.

The paid features listed above unlock **scale, not correctness**: the same detectors, the same
sequencing and the same effort model run in both editions. A lapsed licence takes the tool back
to the free behaviour; it never produces a wrong answer.

---

## License

MIT. See [LICENSE](LICENSE).
