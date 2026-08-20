# Backporch — design record

Why the plugin is shaped the way it is: every load-bearing decision, the reason
behind it, and what it would cost to change. Read this before "simplifying"
anything — most of the odd-looking choices are scars from a specific failure.

Companion to the [README](../README.md), which describes what the plugin does;
this file records *why*.

## Scope, stated once

Backporch exists for **one person connecting their own devices to their own
server** over a domain they own. That scope drives everything: no multi-tenant
concerns, no wildcard-by-default, no external services beyond the CA and (at
most) one DNS API. Guardrails set at the start and still binding:

- Security first: ships disabled, staging CA first, secrets never logged.
- Simple: the config page is a guided walkthrough, not a settings form.
- Lag-free: all certificate work runs on a scheduled task or an explicit
  button press — never on the playback path.

## Architecture

```
configPage.html  ──polls──▶  BackporchController (admin-only, RequiresElevation)
                                   │  Status / Request / ConfirmDns / Check
                                   ▼
                             AcmeService ──▶ Certes 3.0.4 ──▶ ACME CA
                                   │
                     ┌─────────────┼──────────────────┐
                     ▼             ▼                  ▼
             IssuanceState   HttpChallengeStore   IDnsProvider
             (phase machine) (token → keyauth)    (Cloudflare | Manual)
                                   ▲
                    AcmeChallengeController (anonymous)
                    GET /.well-known/acme-challenge/{token}
```

- **`AcmeService`** owns the whole pipeline: account (find-or-register per
  CA), order, challenge, validation, finalize, chain download, PFX write.
- **`IssuanceState`** is a server-side singleton phase machine. The page only
  *polls* it, so progress survives page reloads, browser crashes, and a second
  admin opening the page mid-run. It also carries the manual-DNS handshake (a
  `TaskCompletionSource` the ConfirmDns endpoint releases).
- **`HttpChallengeStore`** is a concurrent dictionary of pending HTTP-01
  answers; the anonymous controller reads from it, the pipeline writes and —
  verified by test — always removes.
- **`IDnsProvider`** is the pluggable seam for DNS-01. Two implementations:
  Cloudflare (API) and Manual (parks on the state machine until the human
  confirms the TXT record).

## Decision log

Each entry: the decision, why, and the cost of reversing it.

### HTTP-01 is the default; DNS-01 is the fallback
The original design was DNS-01-only ("no inbound port needed to issue").
Reversed after asking what GitHub Pages actually does for custom domains: it
serves the proof over HTTP from the host itself — no credential, no DNS API,
renewal fully automatic. For a Jellyfin server, *Jellyfin is the web server*,
so the plugin can answer HTTP-01 natively. The default path now requires zero
credentials; the token is gone from the happy path entirely. DNS-01 stays for
closed-port/CGNAT setups and is the only route to wildcards.
**Cost of change:** reverting to DNS-01-default reintroduces a stored
credential (or a manual step at every renewal) for every user.

### The challenge route is `[AllowAnonymous]` — deliberately
`GET /.well-known/acme-challenge/{token}` must be world-readable or HTTP-01
cannot work. This is safe because a key authorization is *public by design*:
it proves possession of the account key only to the CA that generated the
token, and the store only holds entries during an active validation. Proven
under Jellyfin's real auth stack in a disposable 10.11.11 container: the route
answers **404, not 401**, with an empty store (so the anonymous attribute is
honored), while `/Backporch/*` still returns 401; both appear in
`openapi.json`, which is the decisive proof the route registration worked.
**Cost of change:** adding auth breaks issuance completely — the CA never
authenticates.

### PKCS#12 is assembled by hand, not by Certes' `PfxBuilder`
Certes resolves the issuer chain against an **embedded root store**. Any root
it doesn't know — Pebble's test root today, a rotated Let's Encrypt root
tomorrow — crashes issuance. The plugin builds the PKCS#12 with .NET crypto
from exactly the chain the CA returned.
**Cost of change:** switching back reintroduces a latent, time-delayed outage
that only fires when Let's Encrypt rotates roots.

### The PFX has no password
A password would have to be stored in plain text in the plugin config beside
the file, adding a step and no security. The real boundary is the `0600` file
mode plus the atomic temp-file-then-rename write (a reader can never observe a
half-written bundle).
**Cost of change:** none for security; only added friction.

### Certificates are validated by SAN, never Subject
Modern CAs issue an **empty Subject** — identity lives in the SAN only. All
assertions use `MatchesHostname`. Any check that reads `Subject` will pass on
old fixtures and fail on real certificates.

### Guided issuance = invisible staging dry run, then production
`RunGuidedAsync` clones the config with `UseStaging=true` and a `.test` cert
path, proves the whole setup against Let's Encrypt staging (which has separate,
generous rate limits), then immediately runs production. The user sees one
button and one progress stream; staging is plumbing.
**Hazard captured in code:** Jellyfin's `UpdateConfiguration` swaps the whole
config object, so persisting the *clone* would silently replace the user's
real config. `GetOrCreateAccountAsync` therefore persists only when
`ReferenceEquals(plugin.Configuration, config)`. Do not remove that guard.

### One ACME account key, find-or-registered per CA
The stored account key is reused forever and registered on whichever directory
it meets (staging and production accept the same key), so the staging → 
production flip inside a guided run never creates a second identity.

### Manual DNS mode is a server-side handshake, not a page timer
`ManualDnsProvider.CreateTxtRecordAsync` parks on
`IssuanceState.WaitForDnsConfirmationAsync` (15-minute timeout). The page shows
the TXT record with copy buttons; `POST /Backporch/ConfirmDns` releases the
wait. Because the wait lives on the server, closing the browser mid-setup
loses nothing.

### No Cloudflare token deep link
Cloudflare's pre-filled token URL (`permissionGroupKeys`) is undocumented and
third-party descriptions of it contradict each other. A silently-wrong prefill
is worse than a manual step, so the page links the official token page and
names the built-in **"Edit zone DNS"** template instead.

### Packaging ships exactly three assemblies
`package.sh` ships the plugin DLL + Certes + BouncyCastle, nothing else.
`dotnet publish` output includes Jellyfin's own assemblies, which must never
ship inside a plugin (ABI clashes at load).

### Target framework is net9.0
Jellyfin 10.11.x runs on net9 — jellyfin *master* is net10, and building
against it produces a plugin the released server cannot load. Local test runs
on a .NET 10 SDK need `DOTNET_ROLL_FORWARD=Major`.

### The plugin GUID is `ec59d7bc-0644-4bfe-a924-b6ec7b88c1fb`
Never change it — Jellyfin identifies the plugin (and its stored config) by
GUID; changing it orphans every existing install.

## Testing strategy, and what it taught

Three CI jobs, all required: unit tests + package, browser UI test, and
end-to-end issuance against a real ACME implementation.

- **Pebble** (Let's Encrypt's test CA) exercises the *plugin's own* pipeline
  end to end for **both** challenge types — account, order, challenge,
  validation, finalize, chain, PFX-on-disk with `0600` and a hostname match.
  The HTTP-01 test also asserts every served token is removed from the store
  afterwards.
  - Pebble and challtestsrv run with `--network host` because this class of
    host firewall drops docker-bridge → host-gateway traffic; bridge-mode
    HTTP-01 tests time out with "context deadline exceeded" and nothing in
    the plugin is wrong.
  - challtestsrv runs its **own** HTTP-01 responder on port 5002 by default;
    pass `-http01 ""` or the test's listener gets "address already in use".
  - Pebble validates HTTP-01 on port **5002**, not 80 — the test serves the
    store through a plain `HttpListener` there.
- **Headless Chromium** (`tests/ui/configpage.test.mjs`) drives the real
  configPage.html against a stubbed `ApiClient`/`Dashboard`: step locking,
  A-record display, live checks, all three proof modes, the manual-TXT
  handshake, progress labels, success banner. Lesson: Playwright's
  `addInitScript` does **not** run for `setContent` documents — the stubs are
  spliced into the HTML string before the page's own script.
- **Disposable Jellyfin container** proves what unit tests can't: the
  assembly loads against the real server, routes register (`openapi.json`),
  the elevation policy holds, and the well-known route is anonymous
  (404-vs-401 is the discriminating observation).

## Polish roadmap (not yet done)

Rough order of value:

1. **Prove it live** — one staging + production issuance with a real domain.
   The only layer no test covers. Needs: the domain, an A record, and either
   a port-80 forward (HTTP-01) or the DNS host's identity (DNS-01).
2. **Certificate hot-reload** — Jellyfin loads the PFX at startup, so a
   renewed certificate waits for the next restart. Fix belongs in core
   (Kestrel certificate selector); until then the renewal task should surface
   "restart pending" in Status.
3. **Status for dashboards** — expose a small read-only status surface
   (domain, days-to-expiry, last renewal outcome) consumable by dashboard
   tools (e.g. Homepage's customapi widget with a Jellyfin API key), so the
   certificate becomes a glanceable tile next to the rest of a media stack.
4. **Renewal UX for manual DNS** — manual mode re-asks for a TXT record at
   every renewal; today that parks for 15 minutes and fails quietly if nobody
   is watching. Needs a notification path or a "switch to HTTP-01" nudge.
5. **More automatic DNS providers** — the `IDnsProvider` seam is ready;
   Cloudflare is the only implementation.
6. **Wildcard / SAN support** — requires DNS-01; deliberately out of scope
   for v0.1.
7. **Reverse-proxy path prefix** — HTTP-01 assumes Jellyfin at the domain
   root; a proxy that prefixes paths breaks the well-known route.
8. **Plugin repository manifest** — publish a repo JSON so install/update is
   one click inside Jellyfin instead of unzip-by-hand.
9. **UI polish pass** — wording, error-state styling, accessibility audit of
   the guided flow.
