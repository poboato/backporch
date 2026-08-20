# Backporch

Automatic, publicly trusted TLS certificates for a self-hosted Jellyfin server.

The name: on an analog TV signal, the *back porch* is the quiet interval on
every scanline where the reference burst lives — the part of the broadcast
that keeps the picture honest. It is also the door your own household uses.

Bring your own domain; the plugin obtains and renews a Let's Encrypt
certificate via the **DNS-01** challenge and writes a PKCS#12 bundle that
Jellyfin serves directly. Because validation happens in DNS, **no inbound port
is needed to issue a certificate** — only to use it. It works from behind NAT,
and nothing about your server is exposed during issuance.

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
   requirement is forwarding port 80 to Jellyfin. Can't open ports? Fall back
   to a Cloudflare API token (with a "test the token" check), or any other DNS
   host via a copy-paste TXT record.
4. **Get your certificate** — one button. A practice run against Let's
   Encrypt's staging service proves the setup without spending production rate
   limits; on success the real certificate is issued immediately. Progress
   streams live and survives page reloads — the state lives on the server.
5. **Turn on HTTPS** — the page hands you the certificate path to paste into
   **Networking → Custom SSL certificate path** (password stays empty), plus
   the port-forward note for remote access.

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

- **Disabled by default.** Nothing runs until you turn it on.
- **Staging CA by default.** First runs go against Let's Encrypt staging, so
  misconfiguration cannot exhaust production rate limits. Switch to production
  only after a staging issuance succeeds.
- The DNS API token is stored in the plugin configuration on your server and
  is never written to logs. Scope it as tightly as your provider allows
  (Cloudflare: *Zone → DNS → Edit* on the single zone).
- The ACME account key never leaves the server.
- The PFX is written to a temp file, restricted to `0600`, then moved into
  place atomically — a reader can never observe a half-written certificate.
- All certificate work runs on a scheduled task or an explicit button press,
  never on the playback path.
- The plugin's API endpoints require an elevated (admin) Jellyfin session.

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

And against Let's Encrypt's **Pebble** test CA (real ACME, no real DNS):

- Full issuance end-to-end through the plugin's own code path — for **both**
  challenge types: HTTP-01 (a test stand-in serves the answers from the same
  store the plugin's well-known route uses, and the pipeline is checked to
  clean up every answer afterwards) and DNS-01. Account registration, order,
  challenge, validation, finalize, chain download, and a PKCS#12 on disk with
  owner-only permissions that matches the hostname. This runs in CI on every
  push.

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
- Cloudflare is the only *automatic* DNS provider so far; manual DNS mode
  works anywhere but asks for a fresh copy-paste at every renewal.
- Jellyfin loads the certificate at startup, so a renewed certificate is
  picked up at the next restart. (A future core contribution could hot-reload
  via Kestrel's certificate selector.)
