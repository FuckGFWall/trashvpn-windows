package cache

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Store is a tiny TTL cache persisted to a single JSON file. It holds two kinds
// of data the client wants to reuse across runs:
//
//   - the WARP account (so re-registering only happens when the user asks) and
//   - scan results per protocol (endpoint, latency, loss, country, node), so a
//     "connect" is instant when the previous scan is still fresh.
//
// Every field is exported for JSON round-tripping; callers should treat the
// file as the source of truth and the in-memory map only as an index.
type Store struct {
	mu       sync.RWMutex
	path     string
	entries  map[string]Entry
	freshTTL time.Duration
}

// Entry is one raw, opaque payload plus its freshness metadata. Payload is
// arbitrary bytes owned by the caller (e.g. the WARP account JSON, or a
// serialized scan result); the store never interprets it.
type Entry struct {
	Payload   []byte    `json:"payload"`
	StoredAt  time.Time `json:"stored_at"`
	ExpiresAt time.Time `json:"expires_at"`
}

// ScanResult is the canonical record the client caches for one protocol scan.
// It directly echoes what warpscout's endpointResult already computes, so a
// stale-but-fresh cache can feed the UI without a rescan.
type ScanResult struct {
	Proto   string    `json:"proto"`
	IPv6    bool      `json:"ipv6"`
	Count   int       `json:"count"`
	Raw     []byte    `json:"-"` // original report bytes, optional
	BuiltAt time.Time `json:"built_at"`
}

// Options configures a Store.
type Options struct {
	Path string // full path of the JSON cache file (defaults to app config dir)
	TTL  time.Duration
}

// DefaultPath returns the cache file path under the OS config dir, e.g.
// %APPDATA%\xehn\cache.json on Windows.
func DefaultPath() (string, error) {
	if home := os.Getenv("WARP_CLIENT_HOME"); home != "" {
		return filepath.Join(home, "cache.json"), nil
	}
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "xehn", "cache.json"), nil
}

// Open loads an existing cache file if present, otherwise returns an empty
// store that will create it on the first Put.
func Open(o Options) (*Store, error) {
	if o.Path == "" {
		p, err := DefaultPath()
		if err != nil {
			return nil, err
		}
		o.Path = p
	}
	ttl := o.TTL
	if ttl <= 0 {
		ttl = 6 * time.Hour
	}
	s := &Store{path: o.Path, entries: map[string]Entry{}, freshTTL: ttl}

	data, err := os.ReadFile(o.Path)
	if err != nil {
		if os.IsNotExist(err) {
			return s, nil
		}
		return nil, err
	}
	var entries map[string]Entry
	if err := json.Unmarshal(data, &entries); err != nil {
		// A corrupt cache must not block startup; start empty but keep the file
		// so the next Put repairs it.
		return s, nil
	}
	for k, e := range entries {
		if time.Now().Before(e.ExpiresAt) {
			s.entries[k] = e
		}
	}
	return s, nil
}

// Get returns the payload for key if it is still fresh.
func (s *Store) Get(key string) ([]byte, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	e, ok := s.entries[key]
	if !ok || time.Now().After(e.ExpiresAt) {
		return nil, false
	}
	return e.Payload, true
}

// GetTTL returns the payload and how long until it expires, if fresh.
func (s *Store) GetTTL(key string) ([]byte, time.Duration, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	e, ok := s.entries[key]
	if !ok {
		return nil, 0, false
	}
	left := time.Until(e.ExpiresAt)
	if left <= 0 {
		return nil, 0, false
	}
	return e.Payload, left, true
}

// Put stores payload under key with the store's default TTL, persisting it.
func (s *Store) Put(key string, payload []byte) error {
	return s.PutTTL(key, payload, s.freshTTL)
}

// PutTTL stores payload under key with an explicit TTL.
func (s *Store) PutTTL(key string, payload []byte, ttl time.Duration) error {
	if ttl <= 0 {
		ttl = s.freshTTL
	}
	s.mu.Lock()
	s.entries[key] = Entry{Payload: payload, StoredAt: time.Now(), ExpiresAt: time.Now().Add(ttl)}
	s.mu.Unlock()
	return s.persist()
}

// Delete removes a key and persists.
func (s *Store) Delete(key string) error {
	s.mu.Lock()
	delete(s.entries, key)
	s.mu.Unlock()
	return s.persist()
}

// Keys returns all currently-fresh keys.
func (s *Store) Keys() []string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	now := time.Now()
	var out []string
	for k, e := range s.entries {
		if now.Before(e.ExpiresAt) {
			out = append(out, k)
		}
	}
	return out
}

func (s *Store) persist() error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if err := os.MkdirAll(filepath.Dir(s.path), 0o755); err != nil {
		return fmt.Errorf("cache mkdir: %w", err)
	}
	data, err := json.MarshalIndent(s.entries, "", "  ")
	if err != nil {
		return err
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return fmt.Errorf("cache write: %w", err)
	}
	return os.Rename(tmp, s.path)
}
