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

### Configuration is snapshotted, and merged at exactly one point
Issuance runs for minutes; Jellyfin's `UpdateConfiguration` **replaces** the
configuration object rather than mutating it. A run that holds the object it
started with therefore writes its results into a detached instance the moment
anyone saves the page — the expiry silently never persists, which makes the
renewal task re-issue every night until Let's Encrypt's duplicate-certificate
limit locks the domain out for a week. So: the pipeline works on
`Configuration.Clone()`, and `Persist()` re-reads the live object and copies
across only the fields a run owns (account key, expiry, outcome, the proven
flag). The same rule applies in the browser — the page re-reads configuration
before every save, and a control the server also writes (the staging checkbox)
is only posted back once the user actually moves it.
**Cost of change:** losing either half reintroduces silent state loss that no
unit test catches, because it needs a save to race a long run.

### An authorization the CA already accepted is skipped, not answered
Let's Encrypt reuses a successful domain validation for about 30 days. Posting
a challenge validation to such an authorization is an error
(`authorization must be pending`), so every re-issuance inside that window
failed — and on the manual path the user was first sent to publish a TXT
record that nothing would ever read. The pipeline now checks authorization
status before touching a provider, and guards `Dns()` for null the same way
the HTTP path always did (a reused HTTP authorization carries no DNS
challenge). CI forces this state with `PEBBLE_AUTHZREUSE=100`.

### Requests retry on a rejected nonce
RFC 8555 requires a client whose request is refused for a stale anti-replay
nonce to retry with a fresh one; Certes supports it but defaults to a single
retry, which the plugin never raised. Pebble rejects a share of nonces
deliberately to catch exactly this, and it did — intermittently, which is why
several earlier green CI runs proved nothing. Both `AcmeContext`s now allow
five retries. Retrying is safe: a rejected request was never processed, so it
cannot double-issue.

### The private key is created restricted, never restricted afterwards
Writing the bundle and then `chmod`-ing it leaves a window — however short —
where a file containing a private key sits at whatever the umask allowed. The
bundle is now created with `FileStreamOptions.UnixCreateMode` already set, so
it has never existed at looser permissions. Two details go with it: the
temporary name is unpredictable and created exclusively (`CreateNew`), because
the output path is administrator-chosen and may sit somewhere shared — the old
predictable `<path>.tmp` could be pre-empted with a symlink and the key written
through it — and a directory the plugin creates for the bundle is made
owner-only, while a directory that already existed is left alone.
**Cost of change:** reverting reintroduces a local key-disclosure window that
no functional test would notice.

### Untrusted input is validated at the door
The domain reaches the resolver, the CA, and the challenge-record builder, so
it is checked as a hostname once, up front, rather than trusted downstream —
which also turns a confusing late failure into one clear message. The public-IP
lookup is a third-party service, so its response is size-capped and must parse
as an IPv4 address before it is used or displayed. Everything the setup page
renders goes through `esc()` or `textContent`; all HTML attributes are
double-quoted, which is what makes escaping `"` sufficient.

### The plugin owns port 80, so that opening it does not open Jellyfin
The tokenless proof needs port 80 reachable from the internet, and the obvious
way to arrange that — forward it to Jellyfin's HTTP port — publishes Jellyfin's
entire unencrypted interface, login page included, permanently, since the
forward has to stay open for renewals. Two mitigations were considered and both
found wanting. *Require HTTPS* cannot cover the **first** issuance, because
there is no certificate to redirect to yet; and telling every user to stand up a
reverse proxy just to expose one path is not a default, it is a project.

So the plugin binds the port itself, on a Kestrel host of its own with no route
into Jellyfin. Its entire vocabulary is a challenge answer or a `301`, and the
guarantee is structural rather than configural: there is nothing behind the
socket to reach. `/System/Info/Public` — unauthenticated on Jellyfin's own port
— returns a redirect with an empty body here.

Details that are load-bearing rather than incidental:
- **Kestrel, not a hand-rolled listener.** This socket faces the open internet;
  writing our own HTTP parsing for it would be the least defensible line in the
  project. Limits and timeouts are set tight — no request body, 15-second
  header and keep-alive timeouts, 100 concurrent connections — because the only
  legitimate caller sends a handful of small GETs every couple of months.
- **The redirect host is the configured domain, never the `Host` header.** An
  unauthenticated internet-facing port that reflects its input into `Location`
  is an open redirect.
- **`301` for GET/HEAD, `308` otherwise**, so a client retrying a POST is not
  silently downgraded into a GET with the body dropped.
- **The port is configurable and separate from the public one**, since a server
  that cannot bind 1–1023 should forward the router's port 80 to something
  unprivileged. A failed bind is reported on the setup page with the reason —
  otherwise it is invisible until a renewal fails months later.
- **Not gated on `Enabled`.** The listener must already be answering before the
  first issuance runs; a domain typed into step one is the trigger.
- `PreventHostingStartup` and an empty `HostingStartupAssemblies` are both set.
  Calling `Configure` names this assembly as the web host's "application", and
  the host then tries to `Assembly.Load` it by name — which fails for a plugin
  loaded from an unprobed path and logs `Startup assembly ... failed to execute`
  on every server start. Harmless, and indistinguishable from a broken plugin to
  anyone reading the log. Found only by running it in a real container.
**Cost of change:** pointing the forward at Jellyfin again re-exposes the whole
plain-HTTP interface, permanently and for every user.

### HSTS is on by default, at six months, with no `includeSubDomains`
A browser redirected to HTTPS still made one plain-HTTP request to be told so,
and that request is the one an attacker on the path answers instead. HSTS closes
it for every visit after the first. A plugin's only seam into Jellyfin's request
pipeline is an `IStartupFilter`, which does work — verified in a real container,
where the header appears on API responses, static files and 404s alike,
including the early "server is loading" reply, and never over plain HTTP. The
header is set from an `OnStarting` callback precisely so that a response reset
further down the pipeline cannot drop it.

Six months rather than the usual year because the promise cannot be withdrawn
from a browser that has already heard it: the lifetime is also how long a
mistake lasts. `includeSubDomains` and `preload` are never sent — both make
promises about names this plugin neither owns nor can verify.
**Cost of change:** turning it off is safe; *shortening* it does not reach
browsers that already cached the longer value.

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
  - Pebble validates HTTP-01 on port **5002**, not 80 — so the test binds the
    *shipped* listener there. Pebble's validation request therefore lands on
    production code, and the same socket is then checked to redirect
    `/web/index.html` and to have kept no answer behind. An earlier version used
    a hand-written `HttpListener` stand-in, which proved the store worked and
    nothing about the thing users actually run.
- **Headless Chromium** (`tests/ui/configpage.test.mjs`) drives the real
  configPage.html against a stubbed `ApiClient`/`Dashboard`: step locking,
  A-record display, live checks, all three proof modes, the manual-TXT
  handshake, progress labels, success banner. Lesson: Playwright's
  `addInitScript` does **not** run for `setContent` documents — the stubs are
  spliced into the HTML string before the page's own script.
- **Disposable Jellyfin container** proves what unit tests can't: the
  assembly loads against the real server, routes register (`openapi.json`),
  the elevation policy holds, and the well-known route is anonymous
  (404-vs-401 is the discriminating observation). It is also the only thing
  that can prove the two host seams work at all — that Jellyfin starts a
  plugin-registered `IHostedService`, and honours a plugin-registered
  `IStartupFilter` — and it earned its keep by catching the hosting-startup
  exception above, which every unit test was blind to.
  - Two traps when seeding one by hand: the plugin's configuration file is
    named after the **assembly** (`Jellyfin.Plugin.Backporch.xml`), not the
    plugin, so a file named `Backporch.xml` is silently ignored; and the config
    directory is written as root, so replacing a file there needs `docker cp`.

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
10. **Replace or vendor Certes** — it is lightly maintained (3.0.4, early 2024)
    and holds the plugin to the retired Portable.BouncyCastle 1.9.0, which
    carries CVE-2024-29857. Exposure here is small (the only certificates
    parsed come from the CA over validated TLS) but it is the one dependency
    risk that cannot be patched away.
11. **Write-only handling for the DNS token** — Jellyfin's plugin configuration
    model hands the whole configuration, token included, to any administrator's
    browser on every page load and posts it back on every save. Keeping the
    secret server-side would need a dedicated endpoint rather than the generic
    configuration save. Only matters for the DNS-01 fallback.
