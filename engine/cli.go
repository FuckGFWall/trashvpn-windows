package main

import (
	"context"
	"flag"
	"fmt"
	"math"
	"os"
	"runtime/debug"

	"github.com/xhwarp/warp-client/internal/core"
)

type command struct {
	name   string
	brief  string
	intro  []string
	groups []flagGroup
	setup  func(*flag.FlagSet, *options)
	run    func(context.Context, options) error
}

var commands = []command{
	{name: "desktop-prepare", setup: func(*flag.FlagSet, *options) {}, run: runDesktopPrepare},
	{name: "desktop-connect", setup: func(*flag.FlagSet, *options) {}, run: runDesktopConnect},
	{name: "geo-check", setup: func(*flag.FlagSet, *options) {}, run: runGeoCheck},
	{name: "geo-repair", setup: func(*flag.FlagSet, *options) {}, run: runGeoRepair},
	{
		name:  "scan",
		brief: "scan WARP endpoint pools for working, low-latency endpoints",
		intro: []string{
			"Two-phase scan:",
			"  - Phase 1 finds which WARP ports get through this network",
			"  - Phase 2 verifies each endpoint's real exit colo through a WARP tunnel",
			"",
			"Working endpoints are reported grouped per subnet. ENDPOINT PING is the ICMP",
			"ping to the endpoint address; -tun-ping adds TUN PING, measured inside the tunnel.",
			"Needs a WARP account: run \"xehn register\" first.",
			"",
			"-proto masque scans Cloudflare's MASQUE service instead: it has no endpoint",
			"pools, so the run covers two anycast addresses on a fixed set of ports and",
			"skips phase 1 - a MASQUE dial succeeds on ports that then pass no data, so",
			"only the full tunnel check tells them apart. Those endpoints flap, hence",
			"-masque-attempts, and the SNI is what gets them past DPI, hence -masque-sni.",
		},
		groups: []flagGroup{scanGroup, protoGroup, awgGroup, masqueGroup, nestGroup, outputGroup},
		setup:  setupScanFlags,
		run:    runScanCmd,
	},
	{
		name:  "register",
		brief: "register a WARP account and save it",
		intro: []string{
			"Writes a WARP account to the account file - every other command needs it.",
			"When the file already holds an account, its id and token are reused to",
			"rotate the keys instead of burning a new registration; -fresh forces a",
			"brand-new account.",
			"",
			"Falls back to registering through a WARP tunnel when the Cloudflare API",
			"is unreachable directly, trying AmneziaWG then WireGuard; -proxy skips",
			"that fallback, so the AmneziaWG parameters below only shape it.",
			"",
			"A MASQUE device is registered alongside, because \"-proto masque\" needs its",
			"own one. Failing to get it only prints a warning - the rest still works.",
		},
		groups: []flagGroup{registerGroup, awgGroup, plainGroup},
		setup:  setupRegisterFlags,
		run:    runRegisterCmd,
	},
	{
		name:  "find-junk",
		brief: "search AmneziaWG junk params that unblock every endpoint",
		intro: []string{
			"Rescans with a fresh random junk set (and a fresh I1 when -gen-i1 is set)",
			"until one set brings up the -threshold share of the sampled endpoints,",
			"then prints the scan command to reuse it. Ctrl+C keeps the best set so far.",
			"",
			"Verifies by handshake and in-tunnel ping only - no exit colo is resolved.",
		},
		groups: []flagGroup{findJunkGroup, findJunkI1Group, plainGroup},
		setup:  setupFindJunkFlags,
		run:    runFindJunkCmd,
	},
	{
		name:  "find-sni",
		brief: "search a MASQUE SNI that gets through this network",
		intro: []string{
			"Rescans the MASQUE endpoints with a different SNI each round until one",
			"brings up the -threshold share of them, then prints the scan command to",
			"reuse it. The candidates are a fixed list, so the search ends by itself;",
			"Ctrl+C keeps the best SNI so far.",
			"",
			"Verifies by handshake and in-tunnel ping only - no exit colo is resolved.",
		},
		groups: []flagGroup{findSNIGroup, plainGroup},
		setup:  setupFindSNIFlags,
		run:    runFindSNICmd,
	},
	{
		name:  "socks",
		brief: "serve one endpoint as a local SOCKS5 proxy (for testing it)",
		intro: []string{
			"Brings up a single tunnel to -endpoint and hands it out as a SOCKS5 proxy on",
			"localhost, so curl, a browser or any geo-lookup tool can be pointed at that",
			"one endpoint without installing a VPN client and without root.",
			"",
			"For testing an endpoint, not for daily use: one tunnel, no reconnect, no",
			"failover - when DPI cuts it the proxy just stops working. For everyday use",
			"take \"scan -conf\" into a real client.",
			"",
			"-through works here too, and it is the only way to try a nested tunnel",
			"without building the interface chain by hand.",
		},
		groups: []flagGroup{socksGroup, protoGroup, awgGroup, masqueGroup, nestGroup},
		setup:  setupSocksFlags,
		run:    runSocksCmd,
	},
	{
		name:  "version",
		brief: "print the xehn version",
		intro: []string{"Prints the version alone, so a script can compare it as is."},
		setup: func(*flag.FlagSet, *options) {},
		run:   runVersionCmd,
	},
	{
		name:  "connect",
		brief: "scan for the best WARP endpoint and connect through the local core",
		intro: []string{
			"Registers an account if none is cached, scans (or reuses a cached",
			"scan), picks the best endpoint for the selected protocol, renders a",
			"full mihomo config, starts the embedded Clash core and points Windows'",
			"system proxy at it. Shows exit IP, latency and country.",
			"",
			"With -tun it brings up a TUN device instead of the system proxy",
			"(needs administrator rights).",
		},
		groups: []flagGroup{connectGroup, scanGroup, protoGroup, awgGroup, masqueGroup, nestGroup},
		setup:  setupConnectFlags,
		run:    runConnectCmd,
	},
	{
		name:  "disconnect",
		brief: "stop the core and restore the system proxy",
		intro: []string{
			"Stops the embedded Clash core and restores Windows' previous proxy",
			"settings. Safe to run when nothing is connected.",
		},
		groups: []flagGroup{connectGroup},
		setup:  setupDisconnectFlags,
		run:    runDisconnectCmd,
	},
	{
		name:  "status",
		brief: "print the current connection: exit IP, latency, country",
		intro: []string{
			"Reports whether the client is connected and, when it is, the live",
			"exit IP, country, colo and latency of the tunnel.",
		},
		setup: func(*flag.FlagSet, *options) {},
		run:   runStatusCmd,
	},
	{
		name:  "list",
		brief: "show cached scan results",
		intro: []string{
			"Lists every cached endpoint from the last scan of this protocol,",
			"newest scan first. Use -p to pick the protocol.",
		},
		groups: []flagGroup{protoGroup},
		setup: func(fs *flag.FlagSet, o *options) {
			fs.StringVar(&o.proto, "p", protoAWG, "")
			fs.StringVar(&o.proto, "proto", protoAWG, "")
		},
		run: runListCmd,
	},
	{
		name:  "gui",
		brief: "launch the native graphical client (Proton-style window)",
		intro: []string{
			"Starts the native Windows window: one-click connect/disconnect, live",
			"status (exit IP, latency, country), a WARP-in-WARP toggle, cached",
			"endpoints and a real-time traffic chart. This is the same window a",
			"double-click of the GUI binary opens.",
		},
		groups: []flagGroup{connectGroup},
		setup: func(fs *flag.FlagSet, o *options) {
			fs.IntVar(&o.mixedPort, "port", 7891, "")
		},
		run: runGUICmd,
	},
}

// gvisor allocates per packet, so a -f scan doubles the heap between GCs and the
// OOM killer wins on a small host; measured, the peak plateaus at this limit.
const defaultMemLimit = 32 << 20

func main() {
	// The runtime default is math.MaxInt64, so this leaves a GOMEMLIMIT set
	// from the environment alone.
	if debug.SetMemoryLimit(-1) == math.MaxInt64 {
		debug.SetMemoryLimit(defaultMemLimit)
	}
	stripLoaderArg()
	attachParentConsole()
	enableVirtualTerminal()
	errPal = palette{enabled: colorEnabled(os.Stderr)}

	// Portable mode: when no WARP_CLIENT_HOME is set, anchor the working
	// directory to the folder the executable lives in so account, cache, core
	// and config stay self-contained in the extracted ZIP.
	ensurePortableHome()

	if len(os.Args) < 2 {
		// No subcommand = double-click launch → GUI dashboard.
		ctx, cancel := context.WithCancel(context.Background())
		defer cancel()
		if err := runGUICmd(ctx, options{mixedPort: 7891}); err != nil {
			fmt.Fprintln(os.Stderr, errPal.fail(err.Error()))
			os.Exit(1)
		}
		return
	}
	if len(os.Args) >= 2 && os.Args[1] == "proxy-helper" {
		// Internal: the elevated half of the blocked-proxy fallback. Not in
		// the command list on purpose — users never type this by hand.
		os.Exit(core.RunProxyHelper(os.Args[2:]))
	}
	if len(os.Args) >= 2 && os.Args[1] == "guard" {
		// Internal: the data-plane watcher connect spawns so a session that
		// outlives its own connect command still gets guarded. Hidden like
		// watchdog - nobody types it by hand.
		os.Exit(runHealthGuard(os.Args[2:]))
	}
	if len(os.Args) >= 2 && os.Args[1] == "watchdog" {
		// Internal: the orphan-core guard the GUI spawns once per core
		// start. Not in the command list on purpose either.
		os.Exit(runWatchdogCmd(os.Args[2:]))
	}
	switch os.Args[1] {
	case "-h", "-help", "--help", "help":
		rootUsage(os.Stdout)
		os.Exit(0)
	}
	cmd := lookupCommand(os.Args[1])
	if cmd == nil {
		fmt.Fprintln(os.Stderr, errPal.fail(fmt.Sprintf("unknown command %q", os.Args[1])))
		fmt.Fprintln(os.Stderr)
		rootUsage(os.Stderr)
		os.Exit(2)
	}

	var opts options
	fs := flag.NewFlagSet(cmd.name, flag.ExitOnError)
	fs.Usage = func() { commandUsage(fs.Output(), *cmd, fs) }
	cmd.setup(fs, &opts)
	fs.Parse(os.Args[2:])
	applyCommonFlags(fs, &opts)

	showEmoji = opts.emoji

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// "version" must stay a bare string: install.sh compares its whole output.
	// Desktop fork performs no automatic upstream client-update checks.

	if err := cmd.run(ctx, opts); err != nil {
		fmt.Fprintln(os.Stderr, errPal.fail(err.Error()))
		os.Exit(1)
	}
}

func lookupCommand(name string) *command {
	for i, c := range commands {
		if c.name == name {
			return &commands[i]
		}
	}
	return nil
}
