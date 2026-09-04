# Test fixtures

`containers.json` is a real Docker container listing, captured from a running
self-hosted machine with 26 containers, trimmed to the fields discovery reads
(names, image, state, ports) with labels emptied and image digests dropped.

It is a real corpus on purpose. An invented list only ever contains the cases
someone thought of, and the ones that matter here are the awkward ones this
machine happened to have: a read-only Docker socket proxy that must never be
offered, a VPN gateway publishing a BitTorrent port beside two web ports, a
dashboard publishing the same application on two ports, containers publishing
nothing at all, and container names ending in `-ui` that collide with each other
once the suffix is stripped.

To refresh it, take `GET /v1.41/containers/json` from a Docker endpoint and keep
the four fields above.
