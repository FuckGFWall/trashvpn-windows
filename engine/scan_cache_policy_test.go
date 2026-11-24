package main

import (
	"encoding/json"
	"github.com/xhwarp/warp-client/internal/cache"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func ageScanCacheFixture(t *testing.T, proto string, v6 bool, age time.Duration) {
	t.Helper()
	path := filepath.Join(os.Getenv("WARP_CLIENT_HOME"), "cache.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	entries := map[string]cache.Entry{}
	if err = json.Unmarshal(raw, &entries); err != nil {
		t.Fatal(err)
	}
	key := scanCacheKey(proto, v6)
	entry := entries[key]
	entry.StoredAt = time.Now().Add(-age)
	entry.ExpiresAt = entry.StoredAt.Add(6 * time.Hour)
	var cs cachedScan
	if err = json.Unmarshal(entry.Payload, &cs); err != nil {
		t.Fatal(err)
	}
	cs.At = entry.StoredAt
	entry.Payload, _ = json.Marshal(cs)
	entries[key] = entry
	raw, _ = json.Marshal(entries)
	if err = os.WriteFile(path, raw, 0600); err != nil {
		t.Fatal(err)
	}
}

func TestScanCachePoliciesApplyToReadAndExport(t *testing.T) {
	s, _ := exportFixture(t)
	for _, tc := range []struct {
		age           time.Duration
		days          int
		forever, want bool
	}{
		{6 * 24 * time.Hour, 7, false, true}, {8 * 24 * time.Hour, 7, false, false},
		{2 * 24 * time.Hour, 1, false, false}, {8 * 24 * time.Hour, 10, false, true},
		{3650 * 24 * time.Hour, 7, true, true},
	} {
		ageScanCacheFixture(t, protoWG, false, tc.age)
		s.CacheDays, s.CacheNeverExpires = tc.days, tc.forever
		o := desktopConnectOptions(s)
		_, got := cachedScanFor(protoWG, false, o)
		if got != tc.want {
			t.Fatalf("read cache age %s days %d forever %t: %t", tc.age, tc.days, tc.forever, got)
		}
		for _, format := range []string{"clash", "xray", "singbox"} {
			_, err := exportCachedNode(s, format)
			if (err == nil) != tc.want {
				t.Fatal("export ignored the selected cache policy")
			}
		}
	}
	s.CacheNeverExpires = false
	s.CacheDays = 7
	ageScanCacheFixture(t, protoWG, false, 6*24*time.Hour)
	o := desktopConnectOptions(s)
	path := filepath.Join(os.Getenv("WARP_CLIENT_HOME"), "cache.json")
	before, _ := os.ReadFile(path)
	var entries map[string]cache.Entry
	_ = json.Unmarshal(before, &entries)
	original := entries[scanCacheKey(protoWG, false)].StoredAt
	markEndpointDead(o, cachedEndpoint{Endpoint: "192.0.2.1:2408"})
	after, _ := os.ReadFile(path)
	_ = json.Unmarshal(after, &entries)
	if !entries[scanCacheKey(protoWG, false)].StoredAt.Equal(original) {
		t.Fatal("endpoint health mutation renewed cache age")
	}
	cs, ok := cachedScanFor(protoWG, false, o)
	if !ok || cs.Results[0].Working || !cs.At.Equal(original) {
		t.Fatal("health mutation or original scan timestamp lost")
	}
	s.CacheDays = 5
	if _, ok = cachedScanFor(protoWG, false, desktopConnectOptions(s)); ok {
		t.Fatal("shortening TTL did not expire an existing cache immediately")
	}
}

func TestCachePolicySettingsDefaultsAndMapping(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	s := defaultSettings()
	if s.CacheDays != 1 || s.CacheNeverExpires || s.AutoConnectAfterPrepare {
		t.Fatal("wrong defaults")
	}
	if ttl := scanCacheOptions(desktopConnectOptions(s)).ScanTTL; ttl == nil || *ttl != 24*time.Hour {
		t.Fatal("default scan expiry is not 24 hours")
	}
	s.CacheDays = 14
	s.CacheNeverExpires = true
	s.AutoConnectAfterPrepare = true
	if err := saveSettings(s); err != nil {
		t.Fatal(err)
	}
	got := loadSettings()
	o := desktopConnectOptions(got)
	if got.CacheDays != 14 || !got.AutoConnectAfterPrepare || !o.cacheNeverExpires || o.cacheDays != 14 || *scanCacheOptions(o).ScanTTL != 0 {
		t.Fatal("settings mapping/persistence lost")
	}
	got.CacheDays = -1
	got.normalize()
	if got.CacheDays != 1 {
		t.Fatal("invalid day count not normalized")
	}
}
