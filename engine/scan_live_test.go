//go:build livewarp

package main

import (
	"context"
	"math/rand"
	"net/netip"
	"os"
	"path/filepath"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/xhwarp/warp-client/internal/cache"
)

// Opt-in comparison only. Loads an existing identity read-only, never runs
// registration, TUN, or system proxy commands. The optional CORE check uses
// only an isolated mihomo instance on two temporary loopback ports.
// Pin GODEBUG=randseednop=0 for matched address samples across executables.
func TestLiveSelection(t *testing.T) {
	dir := os.Getenv("WARP_BENCH_SOURCE")
	if dir == "" {
		t.Skip("set WARP_BENCH_SOURCE to an existing data directory")
	}
	a, err := loadAccount(filepath.Join(dir, "warpscout-account.json"))
	if err != nil {
		t.Fatal("existing WARP account required")
	}
	s := defaultSettings()
	s.Proto, s.ScanIntensity = os.Getenv("WARP_BENCH_PROTO"), os.Getenv("WARP_BENCH_LEVEL")
	if s.Proto != protoWG && s.Proto != protoAWG && s.Proto != protoMASQUE && s.Proto != protoMASQUEH2 {
		t.Fatal("choose a single-hop protocol")
	}
	if !accountReadyFor(a, s.Proto) {
		t.Fatal("protocol credentials unavailable; not registering")
	}
	applyAccount(a)
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	if os.Getenv("WARP_BENCH_HISTORY") == "1" {
		var forever time.Duration
		source, err := cache.Open(cache.Options{Path: filepath.Join(dir, "cache.json"), ScanTTL: &forever})
		if err != nil {
			t.Fatal(err)
		}
		key := scanCacheKey(s.Proto, false)
		if raw, ok := source.Get(key); ok {
			copied, err := cache.Open(cache.Options{})
			if err != nil {
				t.Fatal(err)
			}
			if err = copied.Put(key, raw); err != nil {
				t.Fatal(err)
			}
		}
	}
	o := desktopConnectOptions(s)
	rand.Seed(4021)
	start := time.Now()
	run, ips, err := setupScan(o)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Second)
	defer cancel()
	var final scanProgressMsg
	ph, err := runScan(ctx, o, run, ips, time.Duration(o.timeoutSec)*time.Second, func(m tea.Msg) {
		if p, ok := m.(scanProgressMsg); ok && p.stage == "verify" && p.finished {
			final = p
		}
	})
	nodes := projectCacheEndpoints(ph)
	if len(nodes) > o.earlyStop {
		nodes = nodes[:o.earlyStop]
	}
	t.Logf("RESULT proto=%s level=%s ms=%d completed=%d selected=%d skipped=%d err=%v", s.Proto, s.ScanIntensity, time.Since(start).Milliseconds(), final.completed, len(nodes), final.skipped, err)
	if os.Getenv("WARP_BENCH_CORE") == "1" && len(nodes) > 0 {
		testSelectedCore(t, filepath.Join(dir, "core", "mihomo.exe"), o, run, nodes[0])
		return
	}
	// Independent fresh session per selected endpoint, same test for both
	// binaries. Keep the best endpoint's original session up across three
	// five-second gaps: not a reconnect and not a long-term uptime test.
	for i, n := range nodes {
		func() {
			ctx, cancel := context.WithTimeout(context.Background(), 38*time.Second)
			defer cancel()
			tn, err := newTunnel(run)
			if err != nil {
				t.Fatal(err)
			}
			defer tn.Close()
			check := func() bool { body, ok := tn.stack().fetch(ctx, 4*time.Second); return ok && netipValidMeta(body) }
			if !tn.handshake(ctx, n.Endpoint, 4*time.Second) || !check() {
				t.Logf("RETEST rank=%d https=false", i+1)
				return
			}
			rtt, loss, _ := tn.stack().tunnelPing(ctx, 10, 2*time.Second)
			stable, rounds := 0, 0
			if i == 0 {
				for range 3 {
					time.Sleep(5 * time.Second)
					rounds++
					if check() {
						stable++
					}
				}
			}
			t.Logf("RETEST rank=%d https=true rtt_ms=%.1f icmp_loss=%.2f stable=%d/%d", i+1, float64(rtt)/float64(time.Millisecond), loss, stable, rounds)
		}()
	}
}
func netipValidMeta(body string) bool {
	_, err := netip.ParseAddr(parseMeta(body).ip)
	return err == nil
}

// Exercise the production two-hop preparation path, including selecting the
// outer and keying its inner cache. Only the temporary home is writable.
func TestLiveChainPreparation(t *testing.T) {
	dir := os.Getenv("WARP_BENCH_SOURCE")
	if dir == "" {
		t.Skip("existing data directory required")
	}
	s := defaultSettings()
	s.Proto, s.ScanIntensity = os.Getenv("WARP_BENCH_PROTO"), "medium"
	if !protoChained(s.Proto) {
		t.Skip("chain protocol required")
	}
	a, err := loadAccount(filepath.Join(dir, "warpscout-account.json"))
	if err != nil || !accountReadyFor(a, s.Proto) {
		t.Fatal("complete chain identity required; not registering")
	}
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	scratch, err := accountPath()
	if err != nil {
		t.Fatal(err)
	}
	if err := saveAccount(scratch, a); err != nil {
		t.Fatal(err)
	}
	o := desktopConnectOptions(s)
	o.prepareOnly, o.scanNow, o.noProxy, o.tunMode = true, true, true, false
	defer func() { outer = nil }()
	rand.Seed(4021)
	start := time.Now()
	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Second)
	defer cancel()
	if err = runConnectCmd(ctx, o); err != nil {
		t.Logf("CHAIN proto=%s ms=%d ready=false err=%v", s.Proto, time.Since(start).Milliseconds(), err)
		return
	}
	if outer == nil {
		t.Fatal("missing outer hop")
	}
	o.through, o.proto = outer.endpoint, outer.run.name
	if s.Proto == protoMASQUEMASQUE {
		o.innerProto = protoMASQUE
	}
	cs, ok := cachedScanFor(scanKeyFor(o), false, o)
	if !ok || len(cs.Results) == 0 {
		t.Fatal("missing verified inner endpoints")
	}
	t.Logf("CHAIN proto=%s ms=%d ready=true inner_nodes=%d", s.Proto, time.Since(start).Milliseconds(), len(cs.Results))
}
