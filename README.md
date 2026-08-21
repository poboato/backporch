# Backporch

Automatic, publicly trusted TLS certificates for a self-hosted Jellyfin server.

The name: on an analog TV signal, the *back porch* is the quiet interval on
every scanline where the reference burst lives — the part of the broadcast
that keeps the picture honest. It is also the door your own household uses.

Bring your own domain; the plugin obtains and renews a Let's Encrypt
certificate and writes a PKCS#12 bundle that Jellyfin serves directly. By
default it proves ownership the way GitHub Pages does — the certificate
authority fetches a proof file from the server itself — so there is **no DNS
credential anywhere** and renewal is completely hands-off. A DNS-01 fallback
covers servers that cannot open a port at all.

The point of the exercise is a server that is reachable over HTTPS and nothing
else. The plugin holds the plain-HTTP port itself so that opening it publishes
the proof file and a redirect, never Jellyfin — see
[the security posture](#nothing-of-jellyfin-is-served-over-plain-http).

This is the missing half of "zero-config remote access": the part that is
honestly achievable as open source, with no vendor infrastructure — you supply
the one thing only you can own, the domain.

## The experience

The configuration page is a guided setup, not a settings form. You type two
things — your domain and an email — and the page walks you through the rest:

1. **Your address** — domain + email. That's all the typing.
2. **Point it at your server** — the page detects your public IP and shows the
   exact A record to add, with copy buttons, then verifies live that the domain
   resolves to you.
3. **Prove you own the domain** — the recommended way needs **nothing at
   all**: the certificate authority fetches a proof file straight from this
   server (the same mechanism GitHub Pages uses for custom domains), and
   Backporch answers it automatically, now and at every renewal. The only
   requirement is forwarding port 80 — to Backporch's own listener, which
   serves the proof and redirects everything else, so the forward never exposes
   Jellyfin. Can't open ports? Fall back to a Cloudflare API token (with a
   "test the token" check), or any other DNS host via a copy-paste TXT record.
4. **Get your certificate** — one button. A practice run against Let's
   Encrypt's staging service proves the setup without spending production rate
   limits; on success the real certificate is issued immediately. Progress
   streams live and survives page reloads — the state lives on the server.
5. **Turn on HTTPS** — the page hands you the certificate path to paste into
   **Networking → Custom SSL certificate path** (password stays empty), tells
   you to tick **Require HTTPS**, and lists the two port forwards that are the
   entire exposure: 443 for Jellyfin, 80 for the proof listener.

## How it works

(Design rationale — why each of these choices, and the failure each one guards
against — is recorded in [docs/DESIGN.md](docs/DESIGN.md).)

1. The plugin registers an ACME account (key generated locally, reused forever,
   valid on staging and production alike).
2. For each issuance it answers the CA's ownership challenge. By default that
   is **HTTP-01**: the plugin serves the proof from an anonymous
   `/.well-known/acme-challenge` route inside Jellyfin itself — zero
   credentials, fully automatic renewal. Alternatively **DNS-01**: a
   `_acme-challenge` TXT record via the provider API or your own hands, which
   needs no inbound connectivity at all.
3. The certificate and key are written atomically to a PKCS#12 file with
   owner-only permissions. The path defaults to Jellyfin's own data directory;
   no password is set on the bundle — one would have to be stored in plain text
   beside it anyway, so the `0600` file mode is the real boundary.
4. A daily scheduled task renews when expiry is inside the threshold
   (default 30 days). Renewal does nothing when the certificate is healthy.

## Security posture

### Nothing of Jellyfin is served over plain HTTP

The certificate authority has to reach
`http://your-domain/.well-known/acme-challenge/…` over port 80 — that is what
makes the proof tokenless. The obvious way to arrange it, forwarding port 80 to
Jellyfin's HTTP port, is also the wrong one: it publishes Jellyfin's entire
unencrypted interface, login page included, and the forward has to stay open
forever for renewals. "Turn on Require HTTPS afterwards" does not fix it either,
because the *first* issuance has no certificate to redirect to yet.

**So Backporch holds port 80 itself.** The plugin opens its own listener, on its
own socket, with no route to Jellyfin at all. It can produce exactly two
responses:

- the key authorization for a challenge this server started seconds ago — a
  value that is public by design, and useless to anyone else; and
- `301` to `https://your-domain…` for every other request, on every method and
  path, with an empty body.

That is the whole vocabulary. `http://your-domain/web/index.html` gets a
redirect. So does `/System/Info/Public`, which on Jellyfin's own port answers
unauthenticated with the server's name and version. There is no configuration
mistake that turns this listener into a way in, because there is nothing behind
it to get to.

What this asks of you: forward public port **443** to Jellyfin's HTTPS port, and
public port **80** to the port Backporch is listening on. **Do not forward
Jellyfin's HTTP port (8096) at all.** Then turn on Jellyfin's **Require HTTPS**
as well, so that even inside your network nothing is served unencrypted.

If the server cannot bind port 80 — an unprivileged container, or something else
already holding it — set a different listen port under Advanced and forward the
router's port 80 there. If the bind fails anyway, the setup page says so with
the reason, rather than leaving you to find out when a renewal fails two months
later.

Prefer to keep your existing reverse proxy in front? Turn the listener off and
have the proxy forward only `/.well-known/acme-challenge/` through to Jellyfin,
where the plugin's anonymous route answers it. Or avoid inbound HTTP entirely
with the DNS-01 fallback, at the cost of a DNS credential or a manual record per
renewal.

### Strict Transport Security

A browser that is redirected to HTTPS still made one plain-HTTP request to be
told so, and that first request is the one someone on the path can answer
instead. With HSTS on (the default), Jellyfin's HTTPS responses carry
`Strict-Transport-Security`, and after a single visit the browser stops trying
HTTP for your domain altogether — and stops letting anyone click through a
certificate warning for it.

The lifetime defaults to 180 days rather than the customary year, because a
browser cannot be told to forget early: the promise is also how long a mistake
would last. `includeSubDomains` and `preload` are deliberately never sent, since
both make promises about names this plugin does not own.

### The rest

- **Disabled by default.** Nothing runs until you turn it on.
- **Staging CA by default.** First runs go against Let's Encrypt staging, so
  misconfiguration cannot exhaust production rate limits. Switch to production
  only after a staging issuance succeeds.
- The DNS API token is stored in the plugin configuration on your server and
  is never written to logs — but it is stored **in clear text**, as Jellyfin
  plugin settings are, and any Jellyfin administrator can read it back through
  the dashboard. Scope it to the single zone (Cloudflare: *Zone → Read* plus
  *Zone → DNS → Edit*, that zone only) so it is worth as little as possible if
  it leaks. The tokenless default avoids the question entirely.
- The ACME account key never leaves the server.
- **The private key is never readable by anyone else, even briefly.** The
  bundle is created with owner-only permissions from the moment it exists
  (not chmod-ed afterwards), under an unpredictable name that cannot be
  pre-empted by a planted symlink, then renamed into place — so a reader can
  never observe a half-written certificate or catch one at loose permissions.
  A directory the plugin creates for it is owner-only too.
- The challenge route is the only anonymous surface, and it can return exactly
  one thing: an answer to a challenge this server started seconds earlier. Key
  authorizations are public by design — the proof is in serving one at your
  domain, not in knowing it. Anything not shaped like an ACME token is refused
  before the lookup happens.
- The plain-HTTP listener never reflects your request back at you: the host in
  every redirect is the domain you configured, not the `Host` header you sent,
  so it cannot be used as an open redirect. It advertises no `Server` header,
  accepts no request body, and holds a connection for at most fifteen seconds.
- The domain is validated as a hostname before it reaches the resolver, the
  certificate authority, or the challenge record.
- All certificate work runs on a scheduled task or an explicit button press,
  never on the playback path.
- The plugin's API endpoints require an elevated (admin) Jellyfin session.

### Known dependency risk

Certes 3.0.4 (the ACME library, last released early 2024) pulls in
**Portable.BouncyCastle 1.9.0**, the retired 1.x line. Bouncy Castle for .NET
before 2.3.1 is affected by **CVE-2024-29857** — excessive CPU use when
importing an EC certificate with crafted curve parameters. It cannot simply be
upgraded: the 2.x package is a different assembly, and Certes binds to the old
one. The practical exposure here is small — the only certificates parsed come
from the ACME CA over validated TLS, so triggering it would mean being Let's
Encrypt or breaking TLS to it — but it is a real advisory that a scanner will
flag, and it is the strongest argument for eventually replacing Certes.

## Requirements

- Jellyfin **10.11.x** (built and tested against 10.11.11).
- A domain you own. Any DNS host works: the default HTTP-01 proof only needs
  the domain's A record pointing at you plus a port-80 forward; DNS-01 is
  automatic with Cloudflare (the provider interface is pluggable) or manual
  anywhere else.
- No CGNAT if you want inbound remote access itself (a certificate can still
  be issued regardless — DNS-01 needs no inbound connectivity).

## Install (manual, until a repository is published)

1. Run `./package.sh` (or grab a release zip).
2. Unzip into `config/plugins/Backporch_0.1.0.0/` on the server.
3. Restart Jellyfin, then open **Dashboard → Plugins → Backporch** and follow
   the steps on the page.

## Verified so far

Against a disposable `jellyfin/jellyfin:10.11.11` container:

- Assembly loads cleanly (`Loaded plugin: Backporch 0.1.0.0`), with
  Certes and BouncyCastle alongside it.
- The admin API registers (`/Acme/Status`: 401 unauthenticated, 200 as admin)
  and the elevation policy holds.
- The renewal task appears in scheduled tasks (`AcmeRenewCertificate`).
- The configuration page serves (`/web/ConfigurationPage`).
- The issuance service executes end-to-end up to its validation gate and
  persists attempt state.
- The plain-HTTP listener binds inside the container and answers on its own
  port: `/` and `/web/index.html` and `/System/Info/Public` all come back `301`
  with an empty body and no `Server` header, and the challenge path `404`s when
  no proof is live. Changing the port from the plugin API rebinds it live — the
  old port stops answering and the new one starts, with no restart.
- With HTTPS enabled, `Strict-Transport-Security` is present on Jellyfin's own
  responses — API, static files, and 404s alike, including the early
  "server is loading" reply — and absent over plain HTTP.

And against Let's Encrypt's **Pebble** test CA (real ACME, no real DNS):

- Full issuance end-to-end through the plugin's own code path — for **both**
  challenge types: HTTP-01, where Pebble's validation request lands on the
  shipped listener itself rather than a test stand-in (and the same socket is
  then checked to redirect `/web/index.html` and to have kept no answer behind),
  and DNS-01. Account registration, order,
  challenge, validation, finalize, chain download, and a PKCS#12 on disk with
  owner-only permissions that matches the hostname. This runs in CI on every
  push.

- Re-issuing immediately for the same domain against a CA that reuses
  authorizations (`PEBBLE_AUTHZREUSE=100`), which is what Let's Encrypt does
  for about 30 days — the case that must skip challenge validation rather than
  post to an authorization the CA already accepted.

And the guided setup page itself, in headless Chromium
(`tests/ui/configpage.test.mjs`, also in CI): step locking and unlocking, the
A-record display with the detected public IP, the live DNS check, both DNS
modes, the manual TXT-record card with its confirmation handshake, progress
labels during a practice run, and the success banner into step 5.

Not yet exercised: Let's Encrypt staging with a real domain, and the Cloudflare
API against a live zone.

## Known limitations

- One domain, one certificate. No wildcard or SAN list yet (wildcards would
  require DNS-01).
- HTTP-01 needs port 80 reachable from the internet at issuance and renewal
  time, and assumes Jellyfin is served at the domain's root (no reverse-proxy
  path prefix in front of the well-known route).
- Binding port 80 needs the privilege to do so. The official Jellyfin container
  has it; a server running as an unprivileged user does not, and should forward
  the router's port 80 to an unprivileged port set under Advanced.
- HTTP-01 also requires Jellyfin's **Base URL** setting to be empty: with one
  set, the server redirects the challenge path away from the plugin. The setup
  page detects this and says so.
- Cloudflare is the only *automatic* DNS provider so far, and its token needs
  both **Zone → Read** and **DNS → Edit** (Cloudflare's "Edit zone DNS"
  template alone is not enough — it cannot look up the zone). Manual DNS mode
  works anywhere but **cannot renew unattended**: the scheduled task skips it
  and says so rather than hanging, so you must renew from the page by hand.
- Jellyfin loads the certificate at startup, so a renewed certificate is
  picked up at the next restart. (A future core contribution could hot-reload
  via Kestrel's certificate selector.)
