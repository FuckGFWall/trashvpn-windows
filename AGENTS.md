# Repository workflow

- After finishing requested changes and relevant tests, commit as `FuchGFWall <FuckGFWall@users.noreply.github.com>` and push to `https://github.com/FuckGFWall/trashvpn-windows` (the user explicitly requested this workflow).
- Keep changes scoped to the task. Never include runtime accounts, private keys, logs, caches, build outputs, or local user data in commits.
- Do not force-push, rewrite history, or change an unexpected remote. Verify the remote and the resulting remote commit.
- Do not stop a running user client, change the system proxy, or run live WARP registration / VPN tests without specific authorization. Prefer isolated fixtures and loopback services.
- Preserve `.git` and use only normal, non-destructive Git operations authorized by the user. Follow higher-priority rules about Git metadata and file deletion.

## Checks

- Go: use the private Go 1.26.3 toolchain if present; `go test -count=1 ./...` and `go vet ./...` from `engine`.
- UI/workflow: `dotnet run --project tests/Xehn.Checks -c Release -- artifacts/previews`.
- Engine bridge: `dotnet run --project tests/Xehn.EngineChecks -c Release -- <engine.exe> <prepared-data-directory>` (no VPN connection).
- Build: `build.ps1`. Do not overwrite a running client's executable; ask the user to exit it first.
