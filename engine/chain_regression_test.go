package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"net"
	"net/netip"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/cache"
	"github.com/xhwarp/warp-client/internal/core"
)

func TestH2ProbeIgnoresProxyEnvironment(t *testing.T) {
	t.Setenv("HTTPS_PROXY", "http://127.0.0.1:1")
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	accepted := make(chan struct{})
	go func() {
		c, err := listener.Accept()
		if err == nil {
			close(accepted)
			c.Close()
		}
	}()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	_, closeAll, _ := dialMasqueTransport(ctx, &tls.Config{ServerName: "localhost"}, nil, listener.Addr(), true, nil)
	closeAll()
	select {
	case <-accepted:
	default:
		t.Fatal("probe did not dial selected endpoint directly")
	}
}

func TestCachedScanRejectsMismatchedMetadata(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	store, err := cache.Open(cache.Options{})
	if err != nil {
		t.Fatal(err)
	}
	for _, cs := range []cachedScan{{Proto: protoMASQUE}, {Proto: protoWG, IPv6: true}} {
		raw, _ := json.Marshal(cs)
		if err := store.Put(scanCacheKey(protoWG, false), raw); err != nil {
			t.Fatal(err)
		}
		if _, ok := cachedScanFor(protoWG, false); ok {
			t.Fatal("wrong protocol/family cache accepted")
		}
	}
}

func TestCancelledConnectDoesNotStart(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := runConnectCmd(ctx, options{}); err != context.Canceled {
		t.Fatalf("got %v", err)
	}
}

func TestChainCacheIdentity(t *testing.T) {
	seen := map[string]bool{}
	for _, proto := range []string{protoWG, protoAWG, protoMASQUE} {
		for _, inner := range []string{protoWG, protoAWG, protoMASQUE} {
			for _, endpoint := range []string{"162.159.198.1:443", "162.159.198.2:443"} {
				key := scanKeyFor(options{proto: proto, innerProto: inner, through: endpoint})
				if seen[key] || key == protoWWarp {
					t.Fatalf("cache collision: %s", key)
				}
				seen[key] = true
			}
		}
	}
	if scanKeyFor(options{proto: protoWG}) != protoWG {
		t.Fatal("plain cache identity changed")
	}
}

func TestChainRetryPreservesSelection(t *testing.T) {
	for _, proto := range []string{protoWWarp, protoMASQUEMASQUE} {
		for _, auto := range []bool{true, false} {
			o := retryConnectOptions(options{proto: protoWG, through: "162.159.198.2:443", outerAutoPicked: auto, innerProto: protoWG}, proto)
			if o.proto != proto || !o.scanNow || (o.through == "") != auto || o.outerAutoPicked {
				t.Fatalf("incorrect retry: %+v", o)
			}
		}
	}
}

func TestCacheUsesApplicationHome(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	path, err := cache.DefaultPath()
	if err != nil || !strings.HasPrefix(path, home) {
		t.Fatalf("cache escaped app home: %s %v", path, err)
	}
}

func TestH2DefaultScanIncludesSeed(t *testing.T) {
	saved := pools
	defer func() { pools = saved }()
	o := defaultConnectOptions(protoMASQUEH2, false, false)
	o.through = "162.159.198.1:443"
	o.innerProto = protoMASQUEH2 // avoid host interface dependency
	_, ips, err := setupScan(o)
	if err != nil {
		t.Fatal(err)
	}
	if len(ips) < 8 || len(ips) > 9 {
		t.Fatalf("unexpected default scan size: %d", len(ips))
	}
	found := false
	for _, ip := range ips {
		if ip == netip.MustParseAddr("162.159.198.2") {
			found = true
		}
	}
	if !found {
		t.Fatal("known H2 seed absent")
	}
}

// Full rendered configs, not capability stubs. -t only: no listener, routes or proxy changes.
func TestFullChainConfigs(t *testing.T) {
	oldOuter, oldAcct, oldMasque := outer, outerAcct, masqueAcct
	defer func() { outer, outerAcct, masqueAcct = oldOuter, oldAcct, oldMasque }()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	priv, _ := x509.MarshalECPrivateKey(key)
	pub, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	masqueAcct = &masqueAccount{PrivateKey: base64.StdEncoding.EncodeToString(priv), PeerPublicKey: string(pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: pub})), IPv4: "172.16.0.2"}
	outerAcct = &account{PrivateKey: warpPrivateKey, PeerPublicKey: warpPublicKey, IPv4: "172.16.0.2"}
	for _, proto := range []string{protoWG, protoMASQUE} {
		for _, routing := range []bool{false, true} {
			run := mustParseProto(proto)
			outer = &nest{run: run, endpoint: "162.159.198.1:443"}
			opts := defaultConnectOptions(proto, false, false)
			if routing {
				opts.proxyMode = modeBypass
			}
			cfg, err := renderClientConfig(opts, cachedEndpoint{Endpoint: "162.159.198.2:443", Working: true}, run)
			if err != nil {
				t.Fatal(err)
			}
			name := mihomoEgressName(run)
			if strings.Count(string(cfg), name) < 2 {
				t.Fatalf("group does not reference declared egress %s", name)
			}
			if bin := os.Getenv("WARP_TEST_MIHOMO"); bin != "" {
				if err := core.TestConfig(bin, t.TempDir(), cfg); err != nil {
					t.Fatalf("%s routing=%t: %v", proto, routing, err)
				}
			}
		}
	}
}

// Opt-in read-only account diagnostic. Direct sockets, no registration or proxy changes.
func TestH2DirectDiagnostic(t *testing.T) {
	path := os.Getenv("WARP_TEST_ACCOUNT")
	if path == "" {
		t.Skip("set WARP_TEST_ACCOUNT for bounded direct H2 diagnosis")
	}
	a, err := loadAccount(path)
	if err != nil {
		t.Fatal(err)
	}
	if a.Masque == nil {
		t.Fatal("no MASQUE identity")
	}
	cfg, err := a.Masque.tlsConfig()
	if err != nil {
		t.Fatal(err)
	}
	for _, endpoint := range []string{"162.159.198.2:443", "162.159.198.2:4443", "162.159.199.2:443"} {
		addr, _ := net.ResolveTCPAddr("tcp", endpoint)
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		c, closeAll, err := dialMasqueTransport(ctx, cfg, nil, addr, true, nil)
		if err != nil {
			t.Logf("%s: %v", endpoint, err)
		} else {
			t.Logf("%s: CONNECT-IP accepted=%t", endpoint, c != nil)
		}
		cancel()
		closeAll()
	}
}
