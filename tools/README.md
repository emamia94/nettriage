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

## `NetTriage.Fulfilment`

Turns a paid Polar order into a delivered licence file with no human in the loop. Run every few
minutes by a scheduled job; it is idempotent, so a re-run never re-sends.

### Why a pull loop and not a webhook

Polar can call a webhook, and that would be faster. It is the wrong trade here:

- **A webhook needs a public HTTPS endpoint.** That means hosting something, keeping it up, and
  putting the signing key — or a proxy to it — on a third-party machine. The key is the business.
- **A pull loop keeps the key where it already is.** The job runs on the machine that holds
  `/opt/data/.nettriage-keys/`, signs locally, and sends. Nothing new is exposed.
- **It self-heals.** If a webhook endpoint is down, Polar retries a bounded number of times and
  then gives up; the sale is lost until someone notices. A pull loop that was down for a day
  catches up on its next run, because the orders are still there.
- **The latency is irrelevant.** A licence is an annual purchase; arriving a few minutes after the
  payment rather than a few seconds after it costs nothing.

### What one run does

```
GET /v1/orders/?status=paid&product_id=<ours>&created_after=<cursor>
  for each order not already recorded, oldest first:
    organisation := the buyer's answer to the checkout custom field
    expiry       := the subscription's current_period_end + grace days
    mint the licence locally with the private key
    write it to the issued directory
    email it to the customer with the licence attached
    record the order, and advance the cursor only past orders that succeeded
```

Order matters and failures are loud: the loop **stops at the first failure** rather than skipping
ahead, and exits non-zero so the scheduled job surfaces it. An order that failed is not marked
done, so the next run retries it.

### Renewals

A renewal is not a special case. Polar charges the card, which creates another paid order with
`billing_reason = subscription_cycle`, and the loop mints a licence whose expiry comes from the new
`current_period_end`. Nobody has to remember anything, and the customer gets the new file without
asking. A cancellation needs no action at all: the licence already issued simply runs out.

The expiry is the **paid period plus a grace margin** (14 days by default), so a renewal that
settles a day late never locks a working customer out of their own tool.

### Why the licence id is derived, not random

`LicenseId` is a hash of the order id and the expiry. Re-running a crashed run therefore reproduces
a **byte-identical** licence: a duplicate email carries the same file the customer already has,
instead of a second one that means the two disagree.

### Commands

```bash
nettriage-fulfilment check    --config <path>   # config, signing key and mail credentials are usable
nettriage-fulfilment preview  --config <path>   # what `run` would do, touching nothing
nettriage-fulfilment run      --config <path>   # the scheduled job's entry point
nettriage-fulfilment status   --config <path>   # cursor, orders fulfilled, recent failures
nettriage-fulfilment resend   <order-id> --config <path>
nettriage-fulfilment replay   <file> --config <path>   # rehearse over recorded orders
```

### Rehearsing without a live sale

`mail.mode = "file"` writes the exact message to a directory instead of sending it, and `replay`
runs the whole path over orders recorded in Polar's own JSON shape. Between them the entire
pipeline — minting, attachment, state, idempotency, failure handling — is exercised without
contacting a customer or a mail server. `replay.example.json` is a working fixture.

### Configuration

`fulfilment.example.json` documents every field. Copy it to
`~/.nettriage-fulfilment/config.json` (or pass `--config`). **Every secret is referenced by path,
never by value**, so the configuration file can sit next to the keys without becoming one.

| Field | Meaning |
| --- | --- |
| `polar.tokenFile` | File holding the Polar organisation token. |
| `polar.productId` | The licensed product. Orders for anything else are ignored. |
| `signing.privateKeyPath` | The ECDsa private key. Stays on this machine. |
| `mail.mode` | `smtp` sends; `file` writes a `.eml` to a directory. |
| `mail.from` | The sender. Must be an address the mail provider has authorised. |
| `mail.smtp.passwordFile` | File holding the SMTP password. Preferred over an inline value. |
| `licence.graceDays` | Added to the paid period before the licence expires. |
| `statePath` | Cursor and the record of fulfilled orders. |
| `issuedDirectory` | Every licence minted, kept so a re-send never re-mints. |

### Guardrails

- Refuses to start from an empty state if the state file exists but is unreadable: doing so would
  re-issue and re-send every licence in the account.
- Refuses to send a licence the shipped verifier would reject.
- Never prints a credential: `check` reports lengths, never values.
- Writes state after **every** order, not at the end of the run, so a crash cannot cause the
  orders already emailed to be emailed again.

### Where the money side lives

The licence file is only half of it; something has to take the payment. Merchant of record is the
right shape for a one-person operation selling to companies: **Polar.sh** handles EU VAT and is the
seller of record, so there is no VAT registration to think about. The store takes the money and
creates an order; this tool turns the order into a licence. The private key never leaves the machine
that signs.
