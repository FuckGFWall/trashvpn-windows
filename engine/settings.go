package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"

	"github.com/xhwarp/warp-client/internal/core"
)

// Pages of the shell. The window opens on the page the user left, so these
// values are persisted and must stay stable across releases.
const (
	pageHome     = "home"
	pageNodes    = "nodes"
	pageRules    = "rules"
	pageSettings = "settings"
	pageAbout    = "about"
	pageLogs     = "logs"
)

// Appearance. ThemeAuto follows the system's light/dark setting.
const (
	themeLight = "light"
	themeDark  = "dark"
	themeAuto  = "auto"
)

// UI languages.
const (
	langZH = "zh"
	langEN = "en"
)

// Routing presets. Four map onto rule sets around the exit; direct is the
// core's own `mode: direct`. legacyModeRule is what the old "rule" choice was
// called on disk and is migrated to custom when read.
const (
	modeBypass     = "bypass"
	modeBlacklist  = "blacklist"
	modeGlobal     = "global"
	modeDirect     = "direct"
	modeCustom     = "custom"
	legacyModeRule = "rule"
)

// Traffic capture: how applications reach the tunnel. The system proxy only
// catches apps that honour WinINET; a TUN device routes the whole machine and
// needs administrator rights (the core embeds wintun, so no extra DLL is needed).
const (
	captureProxy = "proxy"
	captureTun   = "tun"
)

// Tunnel address families. v4 and v6 restrict the tunnel to one family; both
// carries the two side by side. Nested protocols (wwarp, masque-mm) cannot use
// anything but v4: their inner MTU is below IPv6's 1280-byte minimum.
const (
	famV4   = "v4"
	famV6   = "v6"
	famBoth = "both"
)

// clientSettings is the persisted preference set. It is deliberately separate
// from clientState: a session is ephemeral (it can end at any moment, and
// disconnect clears it), while these choices are the user's and must survive a
// restart, a crash, or a reconnect. Everything a settings page can change lives
// here, so the file is also the single place to look when asking "what is this
// install configured to do".
type clientSettings struct {
	VerifyBeforeConnect bool   `json:"verify_before_connect"`
	ScanIntensity       string `json:"scan_intensity"`

	// Appearance. Applied live — switching theme or language repaints the
	// window, it does not restart it.
	Theme string `json:"theme"`
	Lang  string `json:"lang"`

	// Session choices, remembered so a restart starts where you left off.
	Proto      string `json:"proto"`
	OuterProto string `json:"outer_proto"`
	InnerProto string `json:"inner_proto"`
	Rescan     bool   `json:"rescan"`
	Psiphon    bool   `json:"psiphon"`
	// OpenVPN is the V2 egress switch: a VPN Gate relay dialled from inside the
	// base protocol. Mutually exclusive with Psiphon — both replace the final
	// exit, and a session can only land in one place.
	OpenVPN    bool `json:"openvpn"`
	AutoRepair bool `json:"auto_repair"`
	// PsiphonRegion is the preferred Psiphon exit country (ISO alpha-2, upper
	// case). Empty means "automatic" — Psiphon picks the region itself.
	PsiphonRegion string `json:"psiphon_region"`
	// OpenVPNRegion is the preferred VPN Gate exit country (ISO alpha-2, upper
	// case). Empty means "automatic" — the fastest reachable node wins.
	OpenVPNRegion string `json:"openvpn_region"`
	// OpenVPNNode pins one relay inside that country (country-ip-port, the same
	// id the picker shows). Empty means "auto": the connect picks by latency.
	OpenVPNNode string `json:"openvpn_node"`
	// Pin is the endpoint the user picked as the default node; empty means the
	// fastest cached endpoint wins.
	Pin string `json:"pin"`

	// Local proxy: where clients point, where the core's API listens, and
	// whether other devices on the LAN may use the mixed port.
	MixedPort   int    `json:"mixed_port"`
	APIPort     int    `json:"api_port"`
	AllowLan    bool   `json:"allow_lan"`
	CaptureMode string `json:"capture_mode"`

	// Network. IPFamily is the tunnel's address family (v4/v6/both); it is
	// separate from the endpoint scan family, which only ever asks whether the
	// cached endpoint pool is v4 or v6.
	IPFamily   string `json:"ip_family"`
	MTU        int    `json:"mtu"`
	TimeoutSec int    `json:"timeout_sec"`

	// DNS: DNSCustom is a comma separated resolver list (empty = not set);
	// DNSOff stops the client from handing any resolver to the tunnel at all;
	// DNSLeak routes resolver queries through the tunnel and, in TUN mode,
	// hijacks every port-53 packet so no query escapes outside the tunnel.
	DNSCustom string `json:"dns_custom"`
	DNSOff    bool   `json:"dns_off"`
	DNSLeak   bool   `json:"dns_leak"`

	// ProxyMode is the routing preset: bypass (mainland direct, rest proxied),
	// blacklist (only DomainProxy is proxied), global (everything proxied),
	// direct (nothing proxied), or custom (domain lists, rest proxied).
	ProxyMode string `json:"proxy_mode"`

	// Domain lists: comma separated suffixes routed straight (DomainDirect) or
	// through the exit (DomainProxy). They become DOMAIN-SUFFIX rules, which
	// need no geo data. The list order decides: proxy rules are emitted first,
	// so a domain in both lists goes through the tunnel.
	DomainDirect string `json:"domain_direct"`
	DomainProxy  string `json:"domain_proxy"`

	// Behaviour.
	AutoConnect bool `json:"auto_connect"`
	AutoStart   bool `json:"auto_start"`

	// Starred holds endpoints marked as favourites; the node list shows them
	// first and they survive a rescan.
	Starred []string `json:"starred"`

	// Pre-capture-mode fields: read once to migrate an old settings.json, then
	// cleared by normalize so they are never written again. Pointers keep
	// "absent" distinguishable from an explicit false.
	Tun         *bool `json:"tun,omitempty"`
	SystemProxy *bool `json:"system_proxy,omitempty"`
	IPv6        *bool `json:"ipv6,omitempty"`
}

const settingsName = "settings.json"

func defaultSettings() clientSettings {
	return clientSettings{
		Theme:         themeLight,
		ScanIntensity: "medium",
		Lang:          langZH,
		Proto:         protoAWG,
		OuterProto:    protoWG,
		InnerProto:    protoWG,
		AutoRepair:    true,
		MixedPort:     core.DefaultMixedPort,
		APIPort:       core.DefaultAPIPort,
		CaptureMode:   captureProxy,
		IPFamily:      famV4,
		TimeoutSec:    2,
		DNSLeak:       true,
		ProxyMode:     modeCustom,
	}
}

func settingsPath() (string, error) {
	d, err := appDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(d, settingsName), nil
}

// loadSettings returns the persisted preferences. A missing file is normal (a
// fresh install), and a corrupt one is never fatal: either way the caller gets
// defaults and the next save rewrites the file.
func loadSettings() clientSettings {
	s := defaultSettings()
	p, err := settingsPath()
	if err != nil {
		return s
	}
	raw, err := os.ReadFile(p)
	if err != nil {
		return s
	}
	if err := json.Unmarshal(raw, &s); err != nil {
		return defaultSettings()
	}
	// A file written before capture modes and IP families existed carries the
	// old independent switches instead. Migrate only when the new key is truly
	// absent: after this release writes the file, an explicit value wins even
	// when it matches the default.
	var keys map[string]json.RawMessage
	if json.Unmarshal(raw, &keys) == nil {
		if _, ok := keys["capture_mode"]; !ok && s.Tun != nil && *s.Tun {
			s.CaptureMode = captureTun
		}
		if _, ok := keys["ip_family"]; !ok && s.IPv6 != nil && *s.IPv6 {
			s.IPFamily = famV6
		}
	}
	// The old rule mode plus the geo switch is today's bypass preset; plain
	// rule mode becomes custom (normalize handles that mapping).
	var legacy struct {
		GeoSplit bool `json:"geo_split"`
	}
	if json.Unmarshal(raw, &legacy) == nil && legacy.GeoSplit && s.ProxyMode == legacyModeRule {
		s.ProxyMode = modeBypass
	}
	s.normalize()
	return s
}

func saveSettings(s clientSettings) error {
	p, err := settingsPath()
	if err != nil {
		return err
	}
	s.normalize()
	raw, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(p, raw, 0o600)
}

// normalize clamps everything that reaches the renderer. A hand-edited file
// must not be able to produce a core config that fails to parse, nor an
// unopenable window — a bad port, MTU or page name here would look like "the
// client is broken", not "the file is wrong".
func (s *clientSettings) normalize() {
	switch s.Theme {
	case themeLight, themeDark, themeAuto:
	default:
		s.Theme = themeLight
	}
	switch s.Lang {
	case langZH, langEN:
	default:
		s.Lang = langZH
	}
	if s.Proto == "" {
		s.Proto = protoAWG
	}
	if _, err := parseProto(s.Proto); err != nil {
		s.Proto = protoAWG
	}
	if s.OuterProto == "" {
		s.OuterProto = protoWG
	}
	if s.InnerProto == "" {
		s.InnerProto = protoWG
	}
	if s.CaptureMode == "" {
		if s.Tun != nil && *s.Tun {
			s.CaptureMode = captureTun
		} else {
			s.CaptureMode = captureProxy
		}
	}
	switch s.CaptureMode {
	case captureProxy, captureTun:
	default:
		s.CaptureMode = captureProxy
	}
	if s.IPFamily == "" {
		if s.IPv6 != nil && *s.IPv6 {
			s.IPFamily = famV6
		} else {
			s.IPFamily = famV4
		}
	}
	switch s.IPFamily {
	case famV4, famV6, famBoth:
	default:
		s.IPFamily = famV4
	}
	// A nested tunnel's inner MTU (1220) is below IPv6's 1280-byte minimum, so
	// those protocols cannot carry v6 at all. Clamp rather than render a
	// config whose v6 half silently blackholes.
	if !protoV6Capable(s.Proto) {
		s.IPFamily = famV4
	}
	// OpenVPN is IPv4-only end to end: VPN Gate hands out v4 relays and mihomo's
	// openvpn outbound carries no v6 half. The two exits also replace the same
	// thing, so a file with both on is resolved to the newer switch.
	if s.OpenVPN {
		s.Psiphon = false
		s.IPFamily = famV4
	}
	s.Pin = strings.TrimSpace(s.Pin)
	if s.Pin != "" && s.IPFamily != famBoth {
		v6 := endpointIsV6(s.Pin)
		if (s.IPFamily == famV4 && v6) || (s.IPFamily == famV6 && !v6) {
			s.Pin = ""
		}
	}
	s.Tun, s.SystemProxy, s.IPv6 = nil, nil, nil
	switch s.ProxyMode {
	case modeBypass, modeBlacklist, modeGlobal, modeDirect, modeCustom:
	case legacyModeRule:
		s.ProxyMode = modeCustom
	default:
		s.ProxyMode = modeCustom
	}
	if s.MixedPort < 1 || s.MixedPort > 65535 {
		s.MixedPort = core.DefaultMixedPort
	}
	if s.APIPort < 1 || s.APIPort > 65535 {
		s.APIPort = core.DefaultAPIPort
	}
	if s.APIPort == s.MixedPort {
		s.APIPort = s.MixedPort + 1
	}
	// 0 means "let the core pick" (mihomo's own default), which is what an
	// unset MTU has always meant here.
	if s.MTU < 0 || s.MTU > 9000 {
		s.MTU = 0
	}
	if s.TimeoutSec < 1 || s.TimeoutSec > 60 {
		s.TimeoutSec = 2
	}
	// The egress region is an ISO alpha-2 hint or empty for automatic.
	s.PsiphonRegion = strings.ToUpper(strings.TrimSpace(s.PsiphonRegion))
	if len(s.PsiphonRegion) != 2 {
		s.PsiphonRegion = ""
	}
	s.OpenVPNRegion = strings.ToUpper(strings.TrimSpace(s.OpenVPNRegion))
	if len(s.OpenVPNRegion) != 2 {
		s.OpenVPNRegion = ""
	}
	s.OpenVPNNode = strings.TrimSpace(s.OpenVPNNode)
	out := s.Starred[:0]
	for _, e := range s.Starred {
		if e = strings.TrimSpace(e); e != "" {
			out = append(out, e)
		}
	}
	s.Starred = out
}

// domainDirectList / domainProxyList are the two domain suffix lists.
func (s clientSettings) domainDirectList() []string { return fieldList(s.DomainDirect) }
func (s clientSettings) domainProxyList() []string  { return fieldList(s.DomainProxy) }

// fieldList splits a comma/semicolon/whitespace separated list, de-duplicated.
func fieldList(raw string) []string {
	fields := strings.FieldsFunc(raw, func(r rune) bool {
		return r == ',' || r == ';' || r == '\n' || r == '\r' || r == '\t' || r == ' '
	})
	seen := make(map[string]bool, len(fields))
	out := make([]string, 0, len(fields))
	for _, f := range fields {
		f = strings.TrimSpace(f)
		if f == "" {
			continue
		}
		key := strings.ToLower(f)
		if seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, f)
	}
	return out
}

// starred reports whether an endpoint is a favourite.
func (s clientSettings) starred(endpoint string) bool {
	for _, e := range s.Starred {
		if e == endpoint {
			return true
		}
	}
	return false
}

// toggleStar adds or removes an endpoint from the favourites, returning the new
// list. Sorted on write so the file stays diff-friendly.
func toggleStar(list []string, endpoint string) []string {
	out := make([]string, 0, len(list)+1)
	found := false
	for _, e := range list {
		if e == endpoint {
			found = true
			continue
		}
		out = append(out, e)
	}
	if !found {
		out = append(out, endpoint)
	}
	sortStrings(out)
	return out
}

func sortStrings(v []string) {
	for i := 1; i < len(v); i++ {
		for j := i; j > 0 && v[j] < v[j-1]; j-- {
			v[j], v[j-1] = v[j-1], v[j]
		}
	}
}

// apply folds the preference file into the options a connect will run with.
// Session choices (proto, rescan, psiphon, pinned endpoint, protocol pair)
// stay the caller's: they describe this connect, not the preference file.
func (s clientSettings) apply(o *options) {
	if s.MixedPort > 0 {
		o.mixedPort = s.MixedPort
	}
	if s.APIPort > 0 {
		o.apiPort = s.APIPort
	}
	o.allowLan = s.AllowLan
	o.family = s.IPFamily
	// The scan cache and the endpoint pools are keyed by a single family; a
	// dual-stack tunnel still scans v4 endpoints (it carries both either way).
	o.ipv6 = s.IPFamily == famV6
	o.mtu = s.MTU
	if s.TimeoutSec > 0 {
		o.timeoutSec = s.TimeoutSec
	}
	o.dns = strings.TrimSpace(s.DNSCustom)
	o.noDNS = s.DNSOff
	o.dnsLeak = s.DNSLeak && !s.DNSOff
	// Capture mode, not the old proxy switch: TUN also leaves WinINET alone.
	o.tunMode = s.CaptureMode == captureTun
	o.noProxy = s.CaptureMode != captureProxy
	o.proxyMode = s.ProxyMode
	o.domainDirect = s.domainDirectList()
	o.domainProxy = s.domainProxyList()
	o.noGuard = !s.AutoRepair
	o.psiphonRegion = s.PsiphonRegion
	o.openvpnRegion = s.OpenVPNRegion
	o.openvpnNodePin = s.OpenVPNNode
}
