package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/core"
)

// TestMihomoAcceptsRenderedConfig is the core-integration proof: it renders a
// full config exactly the way `connect` does (placeholder WARP keys — mihomo
// validates structure at startup, the handshake only happens on first dial),
// starts a REAL mihomo binary on it and asserts:
//
//  1. mihomo accepts the config and comes up (controller answers),
//  2. the WARP proxy is registered under the name the groups reference,
//  3. the mixed-port is actually listening.
//
// Skipped unless WARP_TEST_MIHOMO points at a mihomo executable, so a plain
// `go test ./...` stays hermetic.
func TestMihomoAcceptsRenderedConfig(t *testing.T) {
	bin := os.Getenv("WARP_TEST_MIHOMO")
	if bin == "" {
		t.Skip("set WARP_TEST_MIHOMO=<path to mihomo> to run the core integration test")
	}

	// Placeholder WARP identity: valid base64 32-byte keys satisfy mihomo's
	// parse; they only fail later when a real handshake is attempted.
	savedPriv, savedPub, savedAcct := warpPrivateKey, warpPublicKey, masqueAcct
	defer func() {
		warpPrivateKey, warpPublicKey, masqueAcct = savedPriv, savedPub, savedAcct
	}()
	warpPrivateKey = base64.StdEncoding.EncodeToString(make([]byte, 32))
	warpPublicKey = base64.StdEncoding.EncodeToString(make([]byte, 32))

	// MASQUE keys are parsed eagerly at startup (ParseECPrivateKey +
	// ParsePKIXPublicKey in NewMasque), so they must be real EC P-256
	// material — they just never need to match Cloudflare's side.
	priv, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate EC key: %v", err)
	}
	privDER, _ := x509.MarshalECPrivateKey(priv)
	pubDER, _ := x509.MarshalPKIXPublicKey(&priv.PublicKey)
	masqueAcct = &masqueAccount{
		PrivateKey: base64.StdEncoding.EncodeToString(privDER),
		PeerPublicKey: "-----BEGIN PUBLIC KEY-----\n" +
			base64.StdEncoding.EncodeToString(pubDER) +
			"\n-----END PUBLIC KEY-----\n",
		IPv4: "172.16.0.2",
	}

	for _, proto := range []string{"awg", "wg", "masque", "masque-h2"} {
		t.Run(proto, func(t *testing.T) {
			mixed, api := freePort(t), freePort(t)
			opts := defaultConnectOptions(proto, false, false)
			opts.mixedPort = mixed
			opts.apiPort = api

			run := mustParseProto(proto)
			ce := cachedEndpoint{Endpoint: "162.159.193.10:443", Country: "DE", Colo: "FRA", Working: true}
			cfg, err := renderClientConfig(opts, ce, run)
			if err != nil {
				t.Fatalf("render: %v", err)
			}
			assertConfigShape(t, string(cfg), proto)

			dir := t.TempDir()
			c, err := core.New(core.Config{
				BinaryPath: bin,
				WorkDir:    dir,
				MixedPort:  mixed,
				APIPort:    api,
			})
			if err != nil {
				t.Fatalf("new core: %v", err)
			}
			if err := c.WriteConfig(cfg); err != nil {
				t.Fatalf("write config: %v", err)
			}
			if err := c.Start(); err != nil {
				t.Fatalf("start core: %v", err)
			}
			defer c.Stop()

			ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
			defer cancel()
			if err := c.WaitReady(ctx); err != nil {
				cfgPath := filepath.Join(dir, "config.yaml")
				cfgDump, _ := os.ReadFile(cfgPath)
				t.Fatalf("mihomo did not come up: %v\n--- rendered config ---\n%s", err, cfgDump)
			}

			// The proxy the group references must exist under the controller.
			wantName := mihomoName(run)
			if outer != nil {
				wantName += mihomoChainSuffix
			}
			proxies, err := controllerProxies(api)
			if err != nil {
				t.Fatalf("controller /proxies: %v", err)
			}
			if _, ok := proxies[wantName]; !ok {
				t.Fatalf("proxy %q missing from controller (have: %v)", wantName, proxyNames(proxies))
			}

			// The mixed-port must be listening.
			conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", mixed), 3*time.Second)
			if err != nil {
				t.Fatalf("mixed-port not listening: %v", err)
			}
			conn.Close()
		})
	}

	// WARP-in-WARP: two wireguard outbounds chained via dialer-proxy — the
	// fifth first-class protocol, rendered by the same mihomoProxies path a
	// -through connect uses. Placeholder keys both halves.
	t.Run("wwarp", func(t *testing.T) {
		savedOuter, savedOuterAcct := outer, outerAcct
		defer func() { outer, outerAcct = savedOuter, savedOuterAcct }()

		outerRun := mustParseProto(protoWG)
		ep, err := parseEndpointSpec("-through", "162.159.193.10:443")
		if err != nil {
			t.Fatalf("parse endpoint: %v", err)
		}
		outer = &nest{
			run:      outerRun,
			endpoint: ep,
			label:    "162.159.193.10:443 (wg)",
		}
		outerAcct = &account{
			PrivateKey:    base64.StdEncoding.EncodeToString(make([]byte, 32)),
			PeerPublicKey: base64.StdEncoding.EncodeToString(make([]byte, 32)),
			IPv4:          "172.16.0.3",
		}

		mixed, api := freePort(t), freePort(t)
		opts := defaultConnectOptions(protoWWarp, false, false)
		opts.outerProto = protoWG
		opts.innerProto = protoWG
		opts.through = "162.159.193.10:443"
		opts.proto = protoWG
		opts.mixedPort = mixed
		opts.apiPort = api

		run := mustParseProto(opts.innerProto)
		ce := cachedEndpoint{Endpoint: "162.159.192.10:443", Country: "DE", Colo: "FRA", Working: true}
		cfg, err := renderClientConfig(opts, ce, run)
		if err != nil {
			t.Fatalf("render: %v", err)
		}
		assertConfigShape(t, string(cfg), protoWWarp)

		dir := t.TempDir()
		c, err := core.New(core.Config{
			BinaryPath: bin,
			WorkDir:    dir,
			MixedPort:  mixed,
			APIPort:    api,
		})
		if err != nil {
			t.Fatalf("new core: %v", err)
		}
		if err := c.WriteConfig(cfg); err != nil {
			t.Fatalf("write config: %v", err)
		}
		if err := c.Start(); err != nil {
			t.Fatalf("start core: %v", err)
		}
		defer c.Stop()

		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		if err := c.WaitReady(ctx); err != nil {
			cfgPath := filepath.Join(dir, "config.yaml")
			cfgDump, _ := os.ReadFile(cfgPath)
			t.Fatalf("mihomo did not come up: %v\n--- rendered config ---\n%s", err, cfgDump)
		}

		// The proxy the group references must exist under the controller.
		wantName := mihomoName(run)
		if outer != nil {
			wantName += mihomoChainSuffix
		}
		proxies, err := controllerProxies(api)
		if err != nil {
			t.Fatalf("controller /proxies: %v", err)
		}
		if _, ok := proxies[wantName]; !ok {
			t.Fatalf("proxy %q missing from controller (have: %v)", wantName, proxyNames(proxies))
		}

		// The mixed-port must be listening.
		conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", mixed), 3*time.Second)
		if err != nil {
			t.Fatalf("mixed-port not listening: %v", err)
		}
		conn.Close()
	})
}

// TestCoreCapabilityProbe proves the probe configs the capability gate feeds
// mihomo parse exactly like the full renders above: a probe YAML whose keys
// drift to a deeper mapping level fails as invalid YAML and would mark every
// protocol unsupported, blocking all connects with "请升级 Core".
func TestCoreCapabilityProbe(t *testing.T) {
	bin := os.Getenv("WARP_TEST_MIHOMO")
	if bin == "" {
		t.Skip("set WARP_TEST_MIHOMO=<path to mihomo> to run the core integration test")
	}
	ver, supported, err := probeCoreCapabilities(bin, t.TempDir())
	if err != nil {
		t.Fatalf("probe: %v", err)
	}
	if ver == "" {
		t.Fatal("no version parsed from core output")
	}
	for _, r := range protoRuns {
		if !supported[r.name] {
			t.Errorf("core %s reports %s unsupported", ver, r.name)
		}
	}
}

// assertConfigShape pins the structural invariants of a rendered config.
func assertConfigShape(t *testing.T, cfg, proto string) {
	t.Helper()
	common := []string{
		"mixed-port:",
		"allow-lan: false",
		"external-controller: 127.0.0.1:",
		"proxies:",
		"proxy-groups:",
		"rules:",
		"MATCH,WARP",
	}
	if proto == "wg" || proto == "awg" {
		common = append(common, "type: wireguard")
	}
	if strings.HasPrefix(proto, "masque") {
		// The transport tuning is part of the shape: dropping it silently
		// returns MASQUE to CUBIC-over-mips and the exit's throughput with it.
		common = append(common, "type: masque", "congestion-controller: bbr", "ip-stack:", "mode: gvisor")
		if proto == "masque-h2" {
			common = append(common, "network: h2")
		}
	}
	if proto == "awg" {
		common = append(common, "amnezia-wg-option:")
	}
	if proto == protoWWarp {
		common = append(common, "dialer-proxy:", "type: wireguard")
	}
	for _, want := range common {
		if !strings.Contains(cfg, want) {
			t.Errorf("config missing %q:\n%s", want, cfg)
		}
	}
}

func freePort(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("free port: %v", err)
	}
	defer ln.Close()
	return ln.Addr().(*net.TCPAddr).Port
}

func controllerProxies(apiPort int) (map[string]json.RawMessage, error) {
	client := &http.Client{Timeout: 5 * time.Second}
	resp, err := client.Get(fmt.Sprintf("http://127.0.0.1:%d/proxies", apiPort))
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	var parsed struct {
		Proxies map[string]json.RawMessage `json:"proxies"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&parsed); err != nil {
		return nil, err
	}
	return parsed.Proxies, nil
}

func proxyNames(proxies map[string]json.RawMessage) []string {
	var names []string
	for k := range proxies {
		names = append(names, k)
	}
	return names
}
