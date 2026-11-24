# Checks

- Go: use the private Go 1.26.3 toolchain if present; `go test -count=1 ./...` and `go vet ./...` from `engine`.
- UI/workflow: `dotnet run --project tests/Xehn.Checks -c Release -- artifacts/previews`.
- Engine bridge: `dotnet run --project tests/Xehn.EngineChecks -c Release -- <engine.exe> <prepared-data-directory>` (no VPN connection).
- Build: `build.ps1`. Do not overwrite a running client's executable; ask the user to exit it first.