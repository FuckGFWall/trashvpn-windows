package cache

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestPutGetRoundTrip(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cache.json")
	s, err := Open(Options{Path: path, TTL: time.Hour})
	if err != nil {
		t.Fatalf("open: %v", err)
	}

	type record struct {
		Endpoint string `json:"endpoint"`
		Country  string `json:"country"`
	}
	r := record{Endpoint: "162.159.192.1:2408", Country: "DE"}
	raw, _ := json.Marshal(r)
	if err := s.Put("scan-awg-v4", raw); err != nil {
		t.Fatalf("put: %v", err)
	}

	got, ok := s.Get("scan-awg-v4")
	if !ok {
		t.Fatal("get: miss after put")
	}
	var back record
	if err := json.Unmarshal(got, &back); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if back != r {
		t.Fatalf("round-trip mismatch: got %+v want %+v", back, r)
	}
}

func TestExpiry(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cache.json")
	s, _ := Open(Options{Path: path, TTL: 50 * time.Millisecond})
	_ = s.PutTTL("k", []byte("v"), 20*time.Millisecond)
	if _, ok := s.Get("k"); !ok {
		t.Fatal("expected fresh hit")
	}
	time.Sleep(40 * time.Millisecond)
	if _, ok := s.Get("k"); ok {
		t.Fatal("expected expiry miss")
	}
}

func TestSurvivesReload(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cache.json")
	s, _ := Open(Options{Path: path, TTL: time.Hour})
	_ = s.Put("k", []byte("payload"))
	// Reopen from the same file: a fresh process should see the entry.
	s2, err := Open(Options{Path: path, TTL: time.Hour})
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	if got, ok := s2.Get("k"); !ok || string(got) != "payload" {
		t.Fatalf("reload lost entry: got=%q ok=%v", got, ok)
	}
	// Corrupt file must not break Open.
	_ = s2.Put("k2", []byte("v2"))
	_ = os.WriteFile(path, []byte("{not json"), 0o600)
	s3, err := Open(Options{Path: path, TTL: time.Hour})
	if err != nil {
		t.Fatalf("open corrupt: %v", err)
	}
	if _, ok := s3.Get("k2"); ok {
		t.Fatal("corrupt file should yield empty store")
	}
}

func TestScanLifetimeOverridesOnlyScanEntriesAndPreservesAge(t *testing.T) {
	path := filepath.Join(t.TempDir(), "cache.json")
	original := time.Now().Add(-10 * 24 * time.Hour)
	entries := map[string]Entry{
		"scan-wg-v4":                     {Payload: []byte("scan"), StoredAt: original, ExpiresAt: original.Add(time.Hour)},
		"scan-chain-v2-wg-wg-fixture-v4": {Payload: []byte("chain"), StoredAt: original, ExpiresAt: original.Add(time.Hour)},
		"other":                          {Payload: []byte("expired"), StoredAt: original, ExpiresAt: original.Add(time.Hour)},
	}
	raw, _ := json.Marshal(entries)
	if err := os.WriteFile(path, raw, 0600); err != nil {
		t.Fatal(err)
	}
	forever := time.Duration(0)
	store, err := Open(Options{Path: path, ScanTTL: &forever})
	if err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"scan-wg-v4", "scan-chain-v2-wg-wg-fixture-v4"} {
		if _, ok := store.Get(key); !ok {
			t.Fatal("forever policy ignored", key)
		}
	}
	if _, ok := store.Get("other"); ok {
		t.Fatal("scan policy revived unrelated cache")
	}
	if _, left, ok := store.GetTTL("scan-wg-v4"); !ok || left <= 0 {
		t.Fatal("forever GetTTL mismatch")
	}
	if err := store.Update("scan-wg-v4", []byte("updated")); err != nil {
		t.Fatal(err)
	}
	if !store.entries["scan-wg-v4"].StoredAt.Equal(original) {
		t.Fatal("update renewed scan age")
	}
	// Unrelated writers must retain expired scan payloads so longer/new
	// lifetimes can be applied later without losing another protocol's cache.
	legacy, _ := Open(Options{Path: path})
	_ = legacy.Put("new", []byte("value"))
	days14 := 14 * 24 * time.Hour
	extended, _ := Open(Options{Path: path, ScanTTL: &days14})
	if value, ok := extended.Get("scan-wg-v4"); !ok || string(value) != "updated" {
		t.Fatal("unrelated write destroyed expired scan")
	}
	days7 := 7 * 24 * time.Hour
	shortened, _ := Open(Options{Path: path, ScanTTL: &days7})
	if _, ok := shortened.Get("scan-wg-v4"); ok {
		t.Fatal("shorter policy did not apply to existing entries")
	}
	if len(shortened.Keys()) != 1 {
		t.Fatal("Keys exposes expired entries")
	}
	fresh, _ := Open(Options{Path: filepath.Join(t.TempDir(), "new.json"), ScanTTL: &forever})
	_ = fresh.Put("scan-wg-v4", []byte("forever"))
	if !fresh.entries["scan-wg-v4"].ExpiresAt.IsZero() {
		t.Fatal("never-expire write stored a finite expiry")
	}
}
