package core

import (
	"os"
	"path/filepath"
	"testing"
)

// GeoDataReady is the gate the settings page uses to offer the "bypass
// mainland" preset, so it has to reject a truncated download: a present but
// empty geosite.dat fails mihomo's config exactly like a missing one, only
// later and with a much worse message.
func TestGeoDataReady(t *testing.T) {
	dir := t.TempDir()

	if GeoDataReady(dir) {
		t.Fatal("empty dir reported ready")
	}

	write := func(name string, size int) {
		if err := os.WriteFile(filepath.Join(dir, name), make([]byte, size), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	// Both present but too small: a failed download that left .part content.
	write("geosite.dat", 1024)
	write("geoip.metadb", 1024)
	if GeoDataReady(dir) {
		t.Fatal("truncated files reported ready")
	}

	write("geosite.dat", 600*1024)
	if GeoDataReady(dir) {
		t.Fatal("ready with geoip.metadb still truncated")
	}

	write("geoip.metadb", 600*1024)
	if !GeoDataReady(dir) {
		t.Fatal("complete pair reported not ready")
	}

	// geoip.dat is the legacy name; the client accepts it, the core helper
	// only knows the metadb one.
	if err := os.Remove(filepath.Join(dir, "geoip.metadb")); err != nil {
		t.Fatal(err)
	}
	if GeoDataReady(dir) {
		t.Fatal("missing geoip.metadb reported ready")
	}
}
