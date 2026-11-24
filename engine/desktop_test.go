package main

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDesktopOptionsApplyWPFSettings(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	// Same snake_case contract as the C# serializer; no GUI or network involved.
	raw := `{"proto":"masque-h2","capture_mode":"tun","proxy_mode":"bypass","mixed_port":17891,"api_port":19090,"ip_family":"v4","mtu":1280,"dns_custom":"1.1.1.1","dns_leak":true,"rescan":true,"auto_repair":false,"timeout_sec":5}`
	if err := os.WriteFile(filepath.Join(home, settingsName), []byte(raw), 0600); err != nil {
		t.Fatal(err)
	}
	o := desktopConnectOptions(loadSettings())
	if o.proto != "masque-h2" || !o.tunMode || !o.noProxy || o.proxyMode != modeBypass || o.mixedPort != 17891 || o.apiPort != 19090 {
		t.Fatalf("desktop mode/port/protocol mapping failed: %+v", o)
	}
	if o.mtu != 1280 || o.dns != "1.1.1.1" || !o.dnsLeak || !o.scanNow || o.timeoutSec != 5 {
		t.Fatal("desktop network/behaviour mapping failed")
	}
}

func TestDesktopDefaultDNS(t *testing.T) {
	s := defaultSettings()
	s.DNSCustom = "   "
	if got := confDNS(desktopConnectOptions(s)); got != "1.1.1.1" {
		t.Fatalf("empty desktop DNS = %q, want 1.1.1.1", got)
	}
	s.DNSCustom = " 9.9.9.9 "
	if got := confDNS(desktopConnectOptions(s)); got != "9.9.9.9" {
		t.Fatalf("custom DNS override lost: %q", got)
	}
	s.DNSOff = true
	if got := confDNS(desktopConnectOptions(s)); got != "" {
		t.Fatalf("DNS not disabled: %q", got)
	}
}

func TestDesktopReleaseRoute(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	s := defaultSettings()
	s.ProxyMode = modeBypass
	o := desktopConnectOptions(s)
	if rules := routeRules(o, "WARP"); len(rules) == 0 || rules[0] != "DOMAIN,api.github.com,WARP" {
		t.Fatalf("update checks must precede bypass rules: %v", rules)
	}
	o.desktop = false
	for _, rule := range routeRules(o, "WARP") {
		if strings.Contains(rule, "api.github.com") {
			t.Fatal("the desktop update route leaked into ordinary CLI routing")
		}
	}
}

func TestDesktopProxyModeDoesNotEnableTun(t *testing.T) {
	s := defaultSettings()
	s.CaptureMode, s.ProxyMode = captureProxy, modeGlobal
	o := desktopConnectOptions(s)
	if o.tunMode || o.noProxy || o.proxyMode != modeGlobal {
		t.Fatal("proxy/global mapping failed")
	}
}

func TestDesktopGeoCheckMissing(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	if err := runGeoCheck(context.Background(), options{}); err == nil || !strings.Contains(err.Error(), "GeoIP") {
		t.Fatalf("missing databases must report an error: %v", err)
	}
}

func TestDesktopVersionIsHTTPHeaderSafe(t *testing.T) {
	for _, c := range shortVersion() {
		if c < 0x21 || c > 0x7e {
			t.Fatal("non-ASCII version in HTTP User-Agent")
		}
	}
}
