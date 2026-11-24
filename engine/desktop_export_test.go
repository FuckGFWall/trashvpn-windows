package main

import (
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/cache"
	"github.com/xhwarp/warp-client/internal/core"
)

func exportFixture(t *testing.T) (clientSettings, account) {
	t.Helper()
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	s := defaultSettings()
	s.Proto = protoWG
	a := account{ID: "DO-NOT-EXPORT-ID", Token: "DO-NOT-EXPORT-TOKEN", PrivateKey: probeWGPrivateKey, PeerPublicKey: probeWGPublicKey, IPv4: "172.16.0.2", IPv6: "2606:4700:110::1234"}
	priv, pub, err := genECKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	a.Masque = &masqueAccount{PrivateKey: base64.StdEncoding.EncodeToString(priv), PeerPublicKey: string(pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: pub})), IPv4: "172.16.0.2", IPv6: a.IPv6}
	a.Outer = &account{PrivateKey: base64.StdEncoding.EncodeToString([]byte(strings.Repeat("o", 32))), PeerPublicKey: a.PeerPublicKey, IPv4: a.IPv4, IPv6: a.IPv6}
	path, _ := accountPath()
	if err = saveAccount(path, a); err != nil {
		t.Fatal(err)
	}
	for _, proto := range []string{protoWG, protoAWG, protoMASQUE, protoMASQUEH2} {
		seedExportCache(t, proto, false, []cachedEndpoint{{Working: true, Endpoint: "192.0.2.1:2408", EpPingMS: 100}, {Working: true, Endpoint: "192.0.2.2:443", EpPingMS: 20}}, time.Hour)
	}
	for _, proto := range []string{protoWG, protoMASQUE} {
		o := options{proto: proto, innerProto: proto, through: "192.0.2.2:443"}
		seedExportCache(t, scanKeyFor(o), false, []cachedEndpoint{{Working: true, Endpoint: "192.0.2.3:443"}}, time.Hour)
	}
	seedExportCache(t, protoWG, true, []cachedEndpoint{{Working: true, Endpoint: "[2001:db8::1]:2408"}}, time.Hour)
	return s, a
}

func seedExportCache(t *testing.T, proto string, v6 bool, endpoints []cachedEndpoint, ttl time.Duration) {
	t.Helper()
	store, err := cache.Open(cache.Options{})
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := json.Marshal(cachedScan{Proto: proto, IPv6: v6, At: time.Now(), Results: endpoints})
	if err := store.PutTTL(scanCacheKey(proto, v6), raw, ttl); err != nil {
		t.Fatal(err)
	}
}

func TestNodeExportClashPreservesEveryProtocolAndChain(t *testing.T) {
	s, a := exportFixture(t)
	home := os.Getenv("WARP_CLIENT_HOME")
	beforeAccount, _ := os.ReadFile(filepath.Join(home, defaultAccount))
	beforeCache, _ := os.ReadFile(filepath.Join(home, "cache.json"))
	for _, proto := range []string{protoWG, protoAWG, protoMASQUE, protoMASQUEH2, protoWWarp, protoMASQUEMASQUE} {
		s.Proto = proto
		raw, err := exportCachedNode(s, "clash")
		if err != nil {
			t.Fatalf("export %s failed: %v", proto, err)
		}
		text := string(raw)
		if !strings.HasPrefix(text, "proxies:\n") || strings.Contains(text, a.Token) || strings.Contains(text, a.ID) || strings.Contains(text, "mixed-port:") || strings.Contains(text, "external-controller:") {
			t.Fatal("export scope or credential separation incorrect")
		}
		if (proto == protoAWG) != strings.Contains(text, "amnezia-wg-option:") {
			t.Fatal("AWG parameters lost or spuriously added")
		}
		if (proto == protoMASQUEH2) != strings.Contains(text, "network: h2") {
			t.Fatal("H2 selector lost")
		}
		if protoChained(proto) && (!strings.Contains(text, "dialer-proxy:") || !strings.Contains(text, "192.0.2.3")) {
			t.Fatal("nested relationship lost")
		}
		if bin := os.Getenv("WARP_TEST_MIHOMO"); bin != "" {
			if err := core.TestConfig(bin, t.TempDir(), raw); err != nil {
				t.Fatalf("core rejected %s export", proto)
			}
		}
	}
	afterAccount, _ := os.ReadFile(filepath.Join(home, defaultAccount))
	afterCache, _ := os.ReadFile(filepath.Join(home, "cache.json"))
	if string(beforeAccount) != string(afterAccount) || string(beforeCache) != string(afterCache) {
		t.Fatal("export modified user data")
	}
	for _, name := range []string{"core", "state.json", "settings.json", "xehn.log"} {
		if _, err := os.Stat(filepath.Join(home, name)); !os.IsNotExist(err) {
			t.Fatal("export created runtime state")
		}
	}
}

func TestNodeExportJSONUsesRegisteredKeysAndSelectedFamily(t *testing.T) {
	s, a := exportFixture(t)
	for _, format := range []string{"xray", "singbox"} {
		for _, family := range []string{famV4, famV6, famBoth} {
			s.IPFamily = family
			raw, err := exportCachedNode(s, format)
			if err != nil {
				t.Fatal(err)
			}
			var root map[string][]map[string]any
			if json.Unmarshal(raw, &root) != nil {
				t.Fatal("invalid JSON")
			}
			if strings.Contains(string(raw), a.Token) || !strings.Contains(string(raw), a.PrivateKey) {
				t.Fatal("wrong identity exported")
			}
			var addresses []any
			if format == "xray" {
				if root["outbounds"][0]["protocol"] != "wireguard" {
					t.Fatal("wrong outbound type")
				}
				settings := root["outbounds"][0]["settings"].(map[string]any)
				addresses = settings["address"].([]any)
			} else {
				ep := root["endpoints"][0]
				if ep["type"] != "wireguard" || ep["system"] != false {
					t.Fatal("legacy or system-interface endpoint exported")
				}
				addresses = ep["address"].([]any)
				peer := ep["peers"].([]any)[0].(map[string]any)
				if family == famV6 && peer["address"] != "2001:db8::1" {
					t.Fatal("IPv6 endpoint mangled")
				}
			}
			want := 1
			if family == famBoth {
				want = 2
			}
			if len(addresses) != want {
				t.Fatal("wrong address family")
			}
		}
	}
	s.IPFamily, s.Pin = famV4, "192.0.2.1:2408"
	raw, err := exportCachedNode(s, "xray")
	if err != nil || !strings.Contains(string(raw), s.Pin) || !strings.Contains(string(raw), "192.0.2.2:443") {
		t.Fatal("pin incorrectly restricted the export to one node")
	}
}

func TestNodeExportIncludesAllWorkingNodesWithUniqueNames(t *testing.T) {
	s, _ := exportFixture(t)
	valid := []cachedEndpoint{
		{Working: true, Endpoint: "192.0.2.1:2408", EpPingMS: 100},
		{Working: true, Endpoint: "192.0.2.2:443", EpPingMS: 20},
		{Working: true, Endpoint: "192.0.2.1:443", EpPingMS: 50},
		{Working: true, Endpoint: "192.0.2.4:8443", EpPingMS: 70},
	}
	// Failed, malformed and duplicate entries must not increase the node count
	// or cause the otherwise usable export to fail.
	results := append(append([]cachedEndpoint(nil), valid...),
		cachedEndpoint{Endpoint: "192.0.2.5:443"},
		cachedEndpoint{Working: true, Endpoint: "bad-address"},
		cachedEndpoint{Working: true, Endpoint: "192.0.2.6:0"},
		valid[0])
	for _, proto := range []string{protoWG, protoAWG, protoMASQUE, protoMASQUEH2} {
		seedExportCache(t, proto, false, results, time.Hour)
	}
	for _, proto := range []string{protoWG, protoMASQUE} {
		o := options{proto: proto, innerProto: proto, through: "192.0.2.2:443"}
		seedExportCache(t, scanKeyFor(o), false, results, time.Hour)
	}
	for _, proto := range []string{protoWG, protoAWG, protoMASQUE, protoMASQUEH2, protoWWarp, protoMASQUEMASQUE} {
		for _, format := range []string{"clash", "xray", "singbox"} {
			if proto != protoWG && format != "clash" {
				continue
			}
			t.Run(proto+"/"+format, func(t *testing.T) {
				s.Proto, s.Pin = proto, "192.0.2.1:2408"
				raw, err := exportCachedNode(s, format)
				if err != nil {
					t.Fatal(err)
				}
				names := map[string]bool{}
				var references []string
				if format == "clash" {
					for _, line := range strings.Split(string(raw), "\n") {
						line = strings.TrimSpace(line)
						for _, prefix := range []string{"- name: ", "dialer-proxy: "} {
							if !strings.HasPrefix(line, prefix) {
								continue
							}
							name, err := strconv.Unquote(strings.TrimPrefix(line, prefix))
							if err != nil || name == "" {
								t.Fatal("invalid node name/reference")
							}
							if prefix == "dialer-proxy: " {
								references = append(references, name)
							} else {
								if names[name] {
									t.Fatal("duplicate node name")
								}
								names[name] = true
							}
						}
					}
					if bin := os.Getenv("WARP_TEST_MIHOMO"); bin != "" {
						if err := core.TestConfig(bin, t.TempDir(), raw); err != nil {
							t.Fatal("core rejected multi-node export")
						}
					}
				} else {
					var root map[string][]map[string]any
					if err := json.Unmarshal(raw, &root); err != nil {
						t.Fatal(err)
					}
					for _, nodes := range root {
						for _, node := range nodes {
							name := node["tag"].(string)
							if names[name] {
								t.Fatal("duplicate tag")
							}
							names[name] = true
						}
					}
				}
				want := len(valid)
				if protoChained(proto) {
					want++ // One shared outer carrier, four selectable inner nodes.
					if len(references) != len(valid) {
						t.Fatal("not every inner node has its carrier")
					}
				}
				if len(names) != want {
					t.Fatalf("exported %d nodes, want %d", len(names), want)
				}
				for _, reference := range references {
					if !names[reference] || reference != references[0] {
						t.Fatal("missing or inconsistent carrier reference")
					}
				}
				for _, endpoint := range valid {
					found := false
					for name := range names {
						if strings.HasSuffix(name, " | "+endpoint.Endpoint) {
							found = true
							break
						}
					}
					if !found {
						t.Fatal("working endpoint omitted from export")
					}
				}
				// Repeated copies remain stable even when the connection pin is stale.
				s.Pin = "192.0.2.99:443"
				again, err := exportCachedNode(s, format)
				if err != nil || string(again) != string(raw) {
					t.Fatal("pin affected full export or order is unstable")
				}
			})
		}
	}
}

func TestNodeExportIPv6DistinctPortsAndCanonicalDuplicates(t *testing.T) {
	s, _ := exportFixture(t)
	s.IPFamily = famV6
	seedExportCache(t, protoWG, true, []cachedEndpoint{
		{Working: true, Endpoint: "[2001:db8::1]:443", EpPingMS: 10},
		{Working: true, Endpoint: "[2001:0db8:0:0:0:0:0:1]:443", EpPingMS: 20},
		{Working: true, Endpoint: "[2001:db8::1]:2408", EpPingMS: 30},
	}, time.Hour)
	for _, format := range []string{"clash", "xray", "singbox"} {
		raw, err := exportCachedNode(s, format)
		if err != nil {
			t.Fatal(err)
		}
		count := strings.Count(string(raw), "XEHN-WARP | ")
		if format == "clash" {
			count = strings.Count(string(raw), "- name: ")
		}
		if count != 2 || !strings.Contains(string(raw), "[2001:db8::1]:443") || !strings.Contains(string(raw), "[2001:db8::1]:2408") {
			t.Fatal("IPv6 endpoints not deduplicated or distinct ports lost")
		}
	}
}

func TestNodeExportRejectsUnavailableOrLossyData(t *testing.T) {
	s, _ := exportFixture(t)
	for _, proto := range []string{protoAWG, protoMASQUE, protoMASQUEH2, protoWWarp, protoMASQUEMASQUE} {
		s.Proto = proto
		for _, format := range []string{"xray", "singbox"} {
			if _, err := exportCachedNode(s, format); err == nil {
				t.Fatal("lossy downgrade accepted")
			}
		}
	}
	s.Proto = protoWG
	if _, err := exportCachedNode(s, "bogus"); err == nil {
		t.Fatal("unknown format accepted")
	}
	seedExportCache(t, protoWG, false, nil, time.Hour)
	if _, err := exportCachedNode(s, "clash"); err == nil {
		t.Fatal("empty cache accepted")
	}
	seedExportCache(t, protoWG, false, []cachedEndpoint{{Working: true, Endpoint: "192.0.2.1:2408"}}, time.Nanosecond)
	ageScanCacheFixture(t, protoWG, false, 8*24*time.Hour)
	if _, err := exportCachedNode(s, "clash"); err == nil {
		t.Fatal("expired cache accepted")
	}
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	if _, err := exportCachedNode(s, "clash"); err == nil {
		t.Fatal("missing account accepted")
	}
}
