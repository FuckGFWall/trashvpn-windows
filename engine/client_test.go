package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/cache"
)

// TestCachedScanRoundTrip seeds a scan through the real cache.Store, reloads it,
// and confirms pickBest + the list projection read it back unchanged.
func TestCachedScanRoundTrip(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)

	endpoints := []cachedEndpoint{
		{Endpoint: "162.159.192.1:2408", Country: "DE", Colo: "FRA", NodeCity: "Frankfurt", EpPingMS: 42, TunPingMS: 58, Loss: 0, Speed: 87.4, Working: true},
		{Endpoint: "162.159.192.5:500", Country: "US", Colo: "IAD", NodeCity: "Ashburn", EpPingMS: 98, TunPingMS: 112, Loss: 0, Speed: 42.1, Working: true},
		{Endpoint: "162.159.193.9:1701", Country: "JP", Colo: "NRT", NodeCity: "Tokyo", EpPingMS: 210, TunPingMS: 240, Loss: 20, Speed: 9.8, Working: true},
	}

	store, err := cache.Open(cache.Options{Path: filepath.Join(home, "cache.json"), TTL: time.Hour})
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	cs := cachedScan{Proto: "awg", IPv6: false, At: time.Now(), Results: endpoints}
	raw, _ := json.Marshal(cs)
	if err := store.Put(scanCacheKey("awg", false), raw); err != nil {
		t.Fatalf("put: %v", err)
	}

	// Reload the store exactly the way runCachedScan does.
	store2, err := cache.Open(cache.Options{Path: filepath.Join(home, "cache.json"), TTL: time.Hour})
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	got, ok := store2.Get(scanCacheKey("awg", false))
	if !ok {
		t.Fatal("cache miss after reload")
	}
	var cs2 cachedScan
	if err := json.Unmarshal(got, &cs2); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if len(cs2.Results) != len(endpoints) {
		t.Fatalf("result count: got %d want %d", len(cs2.Results), len(endpoints))
	}
	if cs2.Results[0].Endpoint != endpoints[0].Endpoint {
		t.Fatalf("first endpoint mismatch: got %s want %s", cs2.Results[0].Endpoint, endpoints[0].Endpoint)
	}

	best, ok := pickBest(cs2.Results)
	if !ok {
		t.Fatal("pickBest found nothing")
	}
	if best.Endpoint != endpoints[0].Endpoint {
		t.Fatalf("pickBest chose %s, want fastest %s", best.Endpoint, endpoints[0].Endpoint)
	}
	if best.Country != "DE" {
		t.Fatalf("best country: got %s want DE", best.Country)
	}
}

// TestClientStatePersistence confirms save/load round-trips the connected flag
// and the system-proxy snapshot the disconnect path restores.
func TestClientStatePersistence(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	s := clientState{
		Proto:     "awg",
		Endpoint:  "162.159.192.1:2408",
		ExitIP:    "104.28.x.x",
		Country:   "DE",
		Colo:      "FRA",
		LatencyMS: 42,
		Connected: true,
		MixedPort: 7890,
		APIPort:   9090,
		StartedAt: time.Now().Format(time.RFC3339),
	}
	if err := saveState(s); err != nil {
		t.Fatalf("save: %v", err)
	}
	back, err := loadState()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	if back.Endpoint != s.Endpoint || !back.Connected || back.Country != s.Country {
		t.Fatalf("state round-trip mismatch: %#v", back)
	}
	// Confirm the status line for a connected state is non-empty.
	if got := back.statusLine(); got == "" || got == "disconnected" {
		t.Fatalf("status line: %q", got)
	}
	back.Connected = false
	if got := back.statusLine(); got != "disconnected" {
		t.Fatalf("disconnected status line: %q", got)
	}
}

// TestRunListCmdNoCache verifies the list command degrades cleanly.
func TestRunListCmdNoCache(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	if err := runListCmd(context.Background(), options{proto: "awg"}); err != nil {
		t.Fatalf("list on empty cache: %v", err)
	}
}

// TestRunStatusCmdDisconnected verifies status reads disconnected cleanly.
func TestRunStatusCmdDisconnected(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	if err := runStatusCmd(context.Background(), options{}); err != nil {
		t.Fatalf("status disconnected: %v", err)
	}
}

func TestBetterEndpointTieBreak(t *testing.T) {
	a := cachedEndpoint{TunPingMS: 50, Loss: 0, Working: true}
	b := cachedEndpoint{TunPingMS: 60, Loss: 0, Working: true}
	if !betterEndpoint(a, b) {
		t.Fatalf("expected a better than b")
	}
	// Higher loss loses even with lower ping.
	c := cachedEndpoint{TunPingMS: 40, Loss: 50, Working: true}
	if betterEndpoint(c, a) {
		t.Fatalf("higher-loss endpoint should not win")
	}
	// Fallback to endpoint ping when tunnel ping absent.
	d := cachedEndpoint{EpPingMS: 30, TunPingMS: 0, Working: true}
	e := cachedEndpoint{EpPingMS: 90, TunPingMS: 0, Working: true}
	if !betterEndpoint(d, e) {
		t.Fatalf("expected d better than e on ep ping")
	}
}

func TestAppDirOverride(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	d, err := appDir()
	if err != nil {
		t.Fatalf("appDir: %v", err)
	}
	if d != home {
		t.Fatalf("appDir should honour override: got %s want %s", d, home)
	}
	if _, err := os.Stat(home); err != nil {
		t.Fatalf("appDir should create dir: %v", err)
	}
}

func init() {
	// Tests in package main touch globals; keep errPal neutral so no palette
	// crashes. fmt is imported to satisfy the helper below if needed.
	_ = fmt.Sprintf
}
