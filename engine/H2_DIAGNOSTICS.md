# H2 investigation (2026-10-29)

The user's scan found H2 exits, but subsequent mihomo HTTPS/DNS checks failed.
A successful scan measures an earlier connection, not the newly opened core
connection. Core controller readiness is not tunnel readiness; the progress
message now explicitly distinguishes them.

## Reproducible checks

`h2_core_integration_test.go` contains opt-in tests:

- `TestMihomoH2UsesTCP`: real core, synthetic loopback TCP/UDP endpoint. Proves
  `network: h2` is not ignored or silently changed to QUIC. Passed.
- `TestMihomoH2StreamSurvivesHandshake`: synthetic TLS/HTTP2 CONNECT endpoint,
  generated disposable keys. Stream remains open beyond the configured five
  second handshake deadline. Passed. This is not an Internet data-plane test.
- `TestH2PairedDiagnostic`: explicit opt-in, reads an existing account, compares
  native H2 and core H2 against the same endpoint and literal-IP HTTPS target.
  Uses random localhost listener ports and a temporary core working directory;
  never registers an account or changes system proxy/TUN settings.

For the first two tests, set `WARP_TEST_MIHOMO` to an absolute core executable
path and run `go test -run 'TestMihomoH2' -count=1 -v .` from the engine folder.
The paired diagnostic additionally requires `WARP_TEST_ACCOUNT` and
`WARP_TEST_ENDPOINT`; it logs outcomes rather than asserting network success.
Its PASS means the diagnostic completed, NOT that a tunnel worked.

At approximately 18:51 Asia/Shanghai, paired checks against
162.159.198.120:8443, 162.159.198.250:443, 162.159.198.71:8443 and
162.159.198.223:8095 all failed native handshake as well as core HTTPS.
Core errors included timeouts and TCP connection reset. This does not prove
whether filtering, remote rejection or transient conditions caused failure,
and does not reproduce a simultaneous native-success/core-failure mismatch.
No timeout inflation, TLS verification bypass, protocol fallback, account
replacement or core replacement was applied on the basis of these results.

Reference implementations consulted (not proof of the exact dirty core build):

- https://github.com/MetaCubeX/mihomo/blob/Meta/adapter/outbound/masque.go
- https://github.com/MetaCubeX/mihomo/blob/Meta/transport/masque/client_h2.go

Bundled core build metadata reports revision
`3f27ef39f8c96bbfbc8b0b9bb10f5c9ef60149d8`, with local modifications.
Behavioral tests above were run against that actual binary, not inferred from
current upstream source.
