# Third-party notices

`warp-client` is this repository's own code. The portable ZIP additionally
**bundles** two independent third-party executables under `core\`. Both are run
as **separate processes and driven over local endpoints**:

- **mihomo** — generated `config.yaml` + its localhost `mixed-port` and REST controller.
- **Psiphon** — generated client config + its localhost SOCKS5 listener.

Neither is linked into, imported by, or statically combined with the
`warp-client` binary.

| component | file in the ZIP | license | upstream |
|---|---|---|---|
| mihomo (Clash Meta) | `core\mihomo.exe` | GPL-3.0 | https://github.com/MetaCubeX/mihomo |
| psiphon-tunnel-core (Psiphon) | `core\psiphon.exe` | GPL-3.0 | https://github.com/Psiphon-Labs/psiphon-tunnel-core |
| warpscout (scanning engine) | — (source-level use only) | see upstream | https://github.com/vernette/warpscout |

The full GNU General Public License v3.0 text lives in
[`third_party/GPL-3.0.txt`](third_party/GPL-3.0.txt) and is shipped in the ZIP as
`licenses/GPL-3.0.txt`.

## Versions recorded for this build

- **mihomo**: `Mihomo Meta v1.19.32 windows amd64 with go1.26.8` (build tag `with_gvisor`)
- **psiphon-tunnel-core**: ConsoleClient build, SHA-256
  `3784500A0F734F7B8EFC93F2A64FE581E4A5E0C82CD91814FB01F402536A81B2`

Record the exact version/hash of whatever you bundle — GPL-3.0 §6 requires the
corresponding source for the binaries you distribute.

## Obligations when you redistribute the ZIP

The ZIP contains GPL-3.0 binaries, so redistributing it is distributing those
programs. At minimum:

1. **Ship the license** — `licenses/GPL-3.0.txt` is included by `pack.ps1`.
2. **Offer the corresponding source** for the exact builds bundled: ship it, or
   give a written offer valid for three years (§6), or point at the upstream
   source **at the matching revision/tag** ("latest" is not a matching revision).
3. **Do not relicense** the bundled binaries or strip their notices.
4. **Keep the integration as-is** — separate executables over localhost. Do not
   `import psiphon-tunnel-core` or link mihomo into your own binary: that would
   make your binary a derivative work of a GPL-3.0 program.

## Status

**LEGAL REVIEW REQUIRED.** This file is an engineering inventory, not legal
advice. The "separate process ⇒ mere aggregation" reasoning above is the position
this project takes; it is not a settled legal conclusion for every jurisdiction.
Have counsel review before any public distribution.
