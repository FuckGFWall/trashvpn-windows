package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"

	"github.com/xhwarp/warp-client/internal/core"
)

// runWatchdogCmd is the hidden "watchdog" subcommand the GUI spawns once per
// core start (core.spawnWatchdog): it sleeps until either its client or its
// core exits. A core exit means candidate rotation or a clean disconnect —
// retire quietly, a fresh watchdog owns any new core. A client exit means
// the GUI died without cleanup — run the full disconnect (stop the core,
// restore the system proxy, clear the state) and reap any core the state
// could not reach.
//
// It never touches the network and never shows a window: it is spawned with
// CREATE_NO_WINDOW / no console at all, and logs into xehn.log like
// the GUI it guards.
func runWatchdogCmd(args []string) int {
	fs := flag.NewFlagSet("watchdog", flag.ContinueOnError)
	fs.SetOutput(os.Stderr)
	clientPID := fs.Int("client-pid", 0, "pid of the client process to guard")
	corePID := fs.Int("core-pid", 0, "pid of the core process being guarded")
	if err := fs.Parse(args); err != nil || *clientPID <= 0 || *corePID <= 0 {
		fmt.Fprintln(os.Stderr, "watchdog: --client-pid and --core-pid are required")
		return 2
	}

	// The client this watchdog guards logs to xehn.log; the watchdog's
	// own console is hidden or absent, so it appends there too — the cleanup
	// trace must be readable after the fact.
	if home, err := appDir(); err == nil {
		if f, ferr := os.OpenFile(filepath.Join(home, "xehn.log"),
			os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644); ferr == nil {
			defer f.Close()
			log.SetOutput(f)
			log.SetFlags(log.LstdFlags)
		}
	}

	clientDied, err := core.WaitClientOrCore(*clientPID, *corePID)
	if err != nil {
		log.Printf("watchdog: %v", err)
		return 1
	}
	if !clientDied {
		log.Printf("watchdog: core pid %d exited first (rotation or disconnect) - retiring", *corePID)
		return 0
	}

	log.Printf("watchdog: client pid %d died without disconnecting - cleaning up", *clientPID)

	// The full disconnect through the shared path: kill the recorded core
	// (image verified), restore the pre-connect proxy snapshot, clear the
	// state. Idempotent: a client that died already-disconnected leaves a
	// zeroed state and nothing to do.
	if derr := runDisconnectCmd(context.Background(), options{mixedPort: core.DefaultMixedPort}); derr != nil {
		log.Printf("watchdog: disconnect cleanup: %v", derr)
	}

	// Belt and braces for a core whose pid never reached the state file (the
	// client was killed mid-candidate, or saveState failed). KillByPID
	// verifies the image path, so a recycled pid is never touched.
	cDir, err := coreDir()
	if err != nil {
		log.Printf("watchdog: %v", err)
	} else if kerr := core.KillByPID(*corePID, core.CoreInDir(cDir)); kerr != nil {
		log.Printf("watchdog: reap core pid %d: %v", *corePID, kerr)
	}

	// Last resort for the window between "proxy pointed at the tunnel" and
	// "snapshot persisted": the state restore above never ran, so if the
	// machine proxy is still ours, disable it outright — a browser aimed at a
	// dead mixed port is a broken host.
	port := core.DefaultMixedPort
	if st, serr := loadState(); serr == nil && st.MixedPort > 0 {
		port = st.MixedPort
	}
	if core.SystemProxyEnabledAt(port) {
		if cerr := core.ClearSystemProxy(); cerr != nil {
			log.Printf("watchdog: clear leftover proxy: %v", cerr)
		} else {
			log.Printf("watchdog: cleared leftover system proxy (was 127.0.0.1:%d)", port)
		}
	}

	log.Printf("watchdog: cleanup done")
	return 0
}
