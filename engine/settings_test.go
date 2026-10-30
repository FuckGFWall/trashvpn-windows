package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// TestSettingsRoundTrip pins the two properties the settings page depends on:
// a written file is read back byte-for-byte equivalent, and a value that came
// from the UI survives a save/load cycle.
func TestSettingsRoundTrip(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", dir)

	in := defaultSettings()
	in.Theme = themeDark
	in.Lang = langEN
	in.MixedPort = 1080
	in.IPFamily = famV6
	in.Pin = "2606:4700::1"
	in.MTU = 1280
	in.DNSCustom = "223.5.5.5, 1.1.1.1"
	in.DNSLeak = false
	in.ProxyMode = modeBlacklist
	in.CaptureMode = captureTun
	in.AllowLan = true

	if err := saveSettings(in); err != nil {
		t.Fatalf("saveSettings: %v", err)
	}
	p := filepath.Join(dir, settingsName)
	if _, err := os.Stat(p); err != nil {
		t.Fatalf("settings file not written: %v", err)
	}

	// A BOM would make encoding/json reject the file and silently reset every
	// preference to its default, so the bytes are checked directly.
	raw, err := os.ReadFile(p)
	if err != nil {
		t.Fatal(err)
	}
	if len(raw) >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF {
		t.Fatal("settings.json written with a UTF-8 BOM")
	}

	out := loadSettings()
	if out.Theme != themeDark || out.Lang != langEN {
		t.Errorf("theme/lang = %q/%q, want dark/en", out.Theme, out.Lang)
	}
	if out.MixedPort != 1080 {
		t.Errorf("mixed port = %d, want 1080", out.MixedPort)
	}
	if out.IPFamily != famV6 || out.MTU != 1280 {
		t.Errorf("family/mtu = %q/%d, want v6/1280", out.IPFamily, out.MTU)
	}
	if out.Pin != in.Pin {
		t.Errorf("pin = %q, want %q", out.Pin, in.Pin)
	}
	if out.DNSCustom != in.DNSCustom {
		t.Errorf("dns = %q, want %q", out.DNSCustom, in.DNSCustom)
	}
	if out.ProxyMode != modeBlacklist {
		t.Errorf("proxy mode = %q, want %q", out.ProxyMode, modeBlacklist)
	}
	if out.DNSLeak {
		t.Error("dns leak = true, want the persisted false")
	}
	if out.CaptureMode != captureTun {
		t.Errorf("capture = %q, want tun", out.CaptureMode)
	}
	if !out.AllowLan {
		t.Error("allow lan = false, want true")
	}
	if out.AutoStart {
		t.Error("autostart = true, want false")
	}
}

// TestSettingsNormalize covers the guards: nothing a hand-edited file can hold
// may reach the renderer as an unusable value.
func TestSettingsNormalize(t *testing.T) {
	s := clientSettings{
		MixedPort:  0,
		APIPort:    0,
		MTU:        -5,
		TimeoutSec: 900,
		Theme:      "neon",
		Lang:       "fr",
		ProxyMode:  "sometimes",
	}
	s.normalize()
	if s.MixedPort <= 0 || s.APIPort <= 0 {
		t.Errorf("ports not defaulted: %d/%d", s.MixedPort, s.APIPort)
	}
	if s.APIPort == s.MixedPort {
		t.Error("api port left equal to the mixed port")
	}
	if s.MTU != 0 {
		t.Errorf("mtu = %d, want 0 (automatic)", s.MTU)
	}
	if s.TimeoutSec != 2 {
		t.Errorf("timeout = %d, want the default", s.TimeoutSec)
	}
	if s.Theme != themeLight || s.Lang != langZH || s.ProxyMode != modeCustom {
		t.Errorf("theme/lang/mode = %q/%q/%q, want light/zh/custom", s.Theme, s.Lang, s.ProxyMode)
	}
	if s.CaptureMode != captureProxy || s.IPFamily != famV4 {
		t.Errorf("capture/family = %q/%q, want proxy/v4", s.CaptureMode, s.IPFamily)
	}
}

// TestSettingsLegacyMigration checks the one-time upgrade path: an old file
// with the independent tun/system_proxy/ipv6 switches becomes a capture mode
// and an address family, and the legacy keys are not written again.
func TestSettingsLegacyMigration(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", dir)
	legacy := `{"tun": true, "system_proxy": true, "ipv6": true, "proto": "awg"}`
	if err := os.WriteFile(filepath.Join(dir, settingsName), []byte(legacy), 0o600); err != nil {
		t.Fatal(err)
	}
	s := loadSettings()
	if s.CaptureMode != captureTun {
		t.Errorf("capture = %q, want tun", s.CaptureMode)
	}
	if s.IPFamily != famV6 {
		t.Errorf("family = %q, want v6", s.IPFamily)
	}
	if err := saveSettings(s); err != nil {
		t.Fatal(err)
	}
	raw, err := os.ReadFile(filepath.Join(dir, settingsName))
	if err != nil {
		t.Fatal(err)
	}
	var written map[string]json.RawMessage
	if err := json.Unmarshal(raw, &written); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"tun", "system_proxy", "ipv6"} {
		if _, ok := written[key]; ok {
			t.Errorf("legacy key %s still written", key)
		}
	}
}

// TestSettingsRoutingMigration maps the pre-preset routing choice onto the new
// dropdown: the old "rule" mode with the region split on was exactly today's
// bypass preset, while "rule" alone becomes custom.
func TestSettingsRoutingMigration(t *testing.T) {
	for _, tc := range []struct {
		raw  string
		want string
	}{
		{`{"proxy_mode": "rule", "geo_split": true}`, modeBypass},
		{`{"proxy_mode": "rule"}`, modeCustom},
		{`{"proxy_mode": "global"}`, modeGlobal},
	} {
		dir := t.TempDir()
		t.Setenv("WARP_CLIENT_HOME", dir)
		if err := os.WriteFile(filepath.Join(dir, settingsName), []byte(tc.raw), 0o600); err != nil {
			t.Fatal(err)
		}
		if got := loadSettings().ProxyMode; got != tc.want {
			t.Errorf("%s: mode = %q, want %q", tc.raw, got, tc.want)
		}
	}
}
