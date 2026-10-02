# Vendor tooling

Everything in this folder is the **publisher's** tooling. It is not shipped to customers and it is
not packed into the NuGet package.

## `NetTriage.LicenseIssuer`

Mints the signed licence files that the CLI verifies. It is kept in the repository on purpose: the
trust model should be auditable by anyone who buys a licence, and hiding it would suggest there is
something to hide.

### Why ECDsa P-256

Ed25519 would have been the obvious choice, but it does not exist in the .NET base class library —
not even in .NET 10. The options were to take a third-party dependency (against the whole point of
the free tool, which has zero NuGet dependencies) or to use what the BCL already has. ECDsa P-256
with SHA-256 is in the BCL, is fast, and the public key is a short string that compiles into the
binary.

### The one thing that matters

**The private key is the business.** Anyone holding it can mint a valid licence for any name and
any expiry. It must never be committed, never be in CI, and never be on a machine that does not
need it.

```bash
# Generate a key pair. Refuses to overwrite an existing private key, because doing so would
# invalidate every licence already issued.
nettriage-license-issuer keygen --out /secure/path

# Issue a licence.
nettriage-license-issuer issue \
  --private-key /secure/path/nettriage-signing-private.pem \
  --holder "Acme Ltd" --organisation "Acme" \
  --edition team --days 365 \
  -o acme.json

# A 30-day trial with every feature.
nettriage-license-issuer trial --private-key … --holder "Acme Ltd" -o acme-trial.json

# Check a file before sending it.
nettriage-license-issuer verify acme.json
```

`keygen` prints the base64 SubjectPublicKeyInfo to paste into
`src/NetTriage.Core/Licensing/LicenseKeys.cs`. That constant is the only part of the key material
that ships.

### Options

| Option | Meaning |
| --- | --- |
| `--private-key <pem>` | Required. PKCS#8 PEM private key from `keygen`. |
| `--holder <name>` | Required. The person or company the licence is for. |
| `--organisation <name>` | Optional. Shown in `license status`. |
| `--edition <name>` | `trial`, `team`, `site` (default `team`, or `trial` for the `trial` command). |
| `--days <n>` | Validity in days (default 365, or 30 for `trial`). |
| `--expires <yyyy-MM-dd>` | Explicit expiry, to align with a contract or PO end date. |
| `--features <a,b,c>` | Override the feature list. Known: `baseline`, `estate`, `rules`, `exports`. |
| `-o, --output <file>` | Where to write the licence (default `nettriage-<id>.json`). |

### Guardrails

- The issuer verifies each licence against the embedded public key **before** writing it. If the
  key pair does not match the build, it refuses to produce a file rather than shipping one that
  will not activate.
- `keygen` refuses to overwrite an existing private key.
- On Linux and macOS the private key is written `0600`.

### What this scheme does not do

It does not stop a determined attacker: anyone can patch out the verification in their own copy.
That is true of every offline licence scheme, and chasing it would cost more than it earns. What
the scheme does is make the honest path easy and the dishonest path obviously deliberate, which is
the same trade every offline-licensed tool makes.

### Where the money side lives

The licence file is only half of it; something has to take the payment and issue the file. Merchant
of record is the right shape here — Lemon Squeezy or Polar.sh both handle EU VAT and act as the
seller of record, which matters for a one-person operation selling to companies. The private key
stays wherever the issuer runs; the store only triggers it.
