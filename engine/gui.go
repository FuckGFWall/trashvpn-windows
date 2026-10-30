package main

import (
	"log"
	"os"
	"path/filepath"

	"github.com/xhwarp/warp-client/internal/core"
)

// defaultConnectOptions builds the options struct a `connect` CLI invocation
// would produce, so the GUI and the CLI share the exact same code path.
func defaultConnectOptions(proto string, tun, scanNow bool) options {
	o := options{
		proto:          proto,
		tunMode:        tun,
		scanNow:        scanNow,
		mixedPort:      core.DefaultMixedPort,
		apiPort:        core.DefaultAPIPort,
		timeoutSec:     2,
		tunnelParallel: defaultTunnelJobs,
		perSubnet:      1,
		confType:       confTypeMihomo,
		wantMeta:       true,
	}
	if proto == "" {
		o.proto = protoAWG
	}
	return o
}

// redirectLogs sends stdout+stderr to a log file next to the binary when the
// process has no console (windowsgui build). In a terminal it is a no-op.
func redirectLogs() {
	// If stderr is a real console, keep printing there.
	if fi, err := os.Stderr.Stat(); err == nil && (fi.Mode()&os.ModeCharDevice) != 0 {
		return
	}
	home, err := appDir()
	if err != nil {
		return
	}
	logPath := filepath.Join(home, "xehn.log")
	f, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return
	}
	// Replace the (dead) standard handles wholesale so every child process —
	// the mihomo core included — inherits the log file instead of a console
	// handle that does not exist in the windowsgui subsystem.
	os.Stdout = f
	os.Stderr = f
	log.SetOutput(f)
	log.SetFlags(log.LstdFlags)
}

// ensurePortableHome anchors WARP_CLIENT_HOME to the executable's folder when
// it is not set, so an extracted ZIP keeps account, cache, core binary and
// config.yaml next to the exe — the v2rayN portable layout. It also creates
// a marker so `go test` runs (whose exe lives in a temp dir) can still override
// it via the environment first.
func ensurePortableHome() {
	if os.Getenv("WARP_CLIENT_HOME") != "" {
		return
	}
	exe, err := os.Executable()
	if err != nil {
		return
	}
	dir := filepath.Dir(exe)
	// Only adopt the exe's folder when it is writable: a Program Files install
	// must fall back to %APPDATA% instead of failing every write.
	probe := filepath.Join(dir, ".warp-portable")
	if err := os.WriteFile(probe, nil, 0o644); err != nil {
		_ = os.Remove(probe)
		return
	}
	_ = os.Remove(probe)
	os.Setenv("WARP_CLIENT_HOME", dir)
}
