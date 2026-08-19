# Jellyfin ACME Plugin

Automatic, publicly trusted TLS certificates for a self-hosted Jellyfin server.

Bring your own domain; the plugin obtains and renews a Let's Encrypt
certificate via the **DNS-01** challenge and writes a PKCS#12 bundle that
Jellyfin serves directly. Because validation happens in DNS, **no inbound port
is needed to issue a certificate** — only to use it. It works from behind NAT,
and nothing about your server is exposed during issuance.

This is the missing half of "zero-config remote access": the part that is
honestly achievable as open source, with no vendor infrastructure — you supply
the one thing only you can own, the domain.

## How it works

1. You configure a domain, a contact email, and a DNS provider API token.
2. The plugin registers an ACME account (key generated locally, reused forever).
3. For each issuance it publishes a `_acme-challenge` TXT record, waits for
   propagation, asks the CA to validate, then removes the record again.
4. The certificate and key are written atomically to a PKCS#12 file with
   owner-only permissions, at the path you point Jellyfin's
   **Networking → Certificate path** setting to.
5. A daily scheduled task renews when expiry is inside the threshold
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
- A domain you own, with DNS hosted at a supported provider
  (currently **Cloudflare**; the provider interface is pluggable).
- No CGNAT if you want inbound remote access itself (the certificate can be
  issued regardless — DNS-01 needs no inbound connectivity).

## Install (manual, until a repository is published)

1. Run `./package.sh` (or grab a release zip).
2. Unzip into `config/plugins/ACME Certificates_0.1.0.0/` on the server.
3. Restart Jellyfin, then open **Dashboard → Plugins → ACME Certificates**.
4. Fill in domain, email, provider, token, and a certificate path. Leave
   **staging** on. Press **Request a certificate now**.
5. When staging succeeds, switch staging off, request again, then set
   **Networking → Certificate path** to the same file and restart.

## Verified so far

Against a disposable `jellyfin/jellyfin:10.11.11` container:

- Assembly loads cleanly (`Loaded plugin: ACME Certificates 0.1.0.0`), with
  Certes and BouncyCastle alongside it.
- The admin API registers (`/Acme/Status`: 401 unauthenticated, 200 as admin)
  and the elevation policy holds.
- The renewal task appears in scheduled tasks (`AcmeRenewCertificate`).
- The configuration page serves (`/web/ConfigurationPage`).
- The issuance service executes end-to-end up to its validation gate and
  persists attempt state.

Not yet exercised: a real ACME order against staging (needs a domain), and the
Cloudflare API calls against a live zone.

## Known limitations

- One domain, one certificate. No wildcard or SAN list yet.
- Cloudflare is the only DNS provider implemented so far.
- Jellyfin loads the certificate at startup, so a renewed certificate is
  picked up at the next restart. (A future core contribution could hot-reload
  via Kestrel's certificate selector.)
