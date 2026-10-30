package main

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"github.com/xhwarp/warp-client/internal/core"
)

func TestResourceHTTPExplicitVPNProxy(t *testing.T) {
	requests := 0
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		if r.URL.Host != "fixture.invalid" {
			t.Errorf("proxy did not receive absolute target: %s", r.URL)
		}
		w.WriteHeader(204)
	}))
	defer proxy.Close()
	port := proxy.Listener.Addr().(*net.TCPAddr).Port
	client := resourceHTTP(port)
	defer client.CloseIdleConnections()
	resp, e := client.Get("http://fixture.invalid/geosite.dat")
	if e != nil {
		t.Fatal(e)
	}
	resp.Body.Close()
	if requests != 1 {
		t.Fatal("explicit proxy bypassed")
	}
	if resourceHTTP(0).Transport.(*http.Transport).Proxy != nil {
		t.Fatal("direct/manual client inherited environment proxy")
	}
}
func TestStrictCoreCheckFailsClosed(t *testing.T) {
	dir := t.TempDir()
	if e := strictCoreCheck(context.Background(), filepath.Join(dir, "missing.exe"), dir); e == nil {
		t.Fatal("missing executable counted supported")
	}
	status := inspectResources(context.Background(), dir, true)
	if !status.CoreNeedsDownload || !status.GeoNeedsDownload {
		t.Fatal("missing resources not surfaced")
	}
}

func TestGeoProxySelectionFailsClosed(t *testing.T) {
	st := clientState{MixedPort: 10808, Verification: "verified"}
	if port, e := resourceProxyPort(st, true, true); e != nil || port != 10808 {
		t.Fatalf("verified VPN proxy not selected: %d, %v", port, e)
	}
	for _, port := range []int{0, -1, 65536} {
		st.MixedPort = port
		for _, required := range []bool{false, true} {
			if _, e := resourceProxyPort(st, true, required); e == nil {
				t.Fatal("invalid live proxy allowed a direct download")
			}
		}
	}
	st.MixedPort = 10808
	for _, verification := range []string{"", "checking"} {
		st.Verification = verification
		if _, e := resourceProxyPort(st, true, true); e == nil {
			t.Fatal("unverified VPN permitted an automatic download")
		}
	}
	if _, e := resourceProxyPort(st, false, true); e == nil {
		t.Fatal("disconnected automatic download allowed")
	}
	if port, e := resourceProxyPort(st, false, false); e != nil || port != 0 {
		t.Fatal("manual disconnected download unexpectedly requires VPN")
	}
}

func TestDesktopMissingGeoDataDoesNotDownload(t *testing.T) {
	dir := t.TempDir()
	if e := requireDesktopGeoData(dir); e == nil {
		t.Fatal("missing bypass databases accepted")
	}
	entries, _ := os.ReadDir(dir)
	if len(entries) != 0 {
		t.Fatal("readiness check attempted a live download")
	}
	for _, name := range core.GeoDataNames() {
		if e := os.WriteFile(filepath.Join(dir, name), make([]byte, 512*1024), 0600); e != nil {
			t.Fatal(e)
		}
	}
	if e := requireDesktopGeoData(dir); e != nil {
		t.Fatal(e)
	}
}
