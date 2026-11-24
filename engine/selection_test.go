package main

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"slices"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"
)

func TestPairPlanHistoryScopeAndBreadth(t *testing.T) {
	h := []cachedEndpoint{
		{Endpoint: "8.34.70.4:859", Working: true, TunPingMS: 130},
		{Endpoint: "8.39.204.2:880", Working: true, TunPingMS: 90},
		{Endpoint: "8.39.204.9:880", Working: false, TunPingMS: 1},
		{Endpoint: "127.0.0.1:10808", Working: true, TunPingMS: 1},
		{Endpoint: "[2606:4700:d0::2]:880", Working: true, TunPingMS: 1},
		{Endpoint: "8.39.204.5:53", Working: true, TunPingMS: 1},
	}
	sample := make([]netip.Addr, len(poolsV4))
	for i, p := range poolsV4 {
		sample[i] = p.Addr().Next().Next()
	}
	for _, level := range []string{"low", "medium", "high"} {
		o := options{scanIntensity: level}
		got := desktopTargets(o, mustParseProto(protoAWG), sample, h)
		if len(got) != selectionFor(level).limit || got[0].ip.String() != "8.39.204.2" || got[0].port != 880 || got[1].ip.String() != "8.34.70.4" {
			t.Fatalf("bad %s history/limit: %v", level, got[:3])
		}
		seen := map[probeTarget]bool{}
		for _, p := range got {
			if seen[p] || !slices.Contains(allWarpPorts(), p.port) || !p.ip.Is4() || p.ip.IsLoopback() {
				t.Fatalf("bad pair %v", p)
			}
			seen[p] = true
		}
		o.port = 2408
		o.targets = []netip.Prefix{poolsV4[0]}
		got = desktopTargets(o, mustParseProto(protoWG), sample, h)
		if len(got) != 1 || got[0].ip != sample[0] || got[0].port != 2408 {
			t.Fatalf("pin/scope ignored: %v", got)
		}
	}
}

func TestMasquePlanExploresBeyondKnownPairs(t *testing.T) {
	for _, proto := range []string{protoMASQUE, protoMASQUEH2} {
		r := mustParseProto(proto)
		seeds := []netip.Addr{netip.MustParseAddr("162.159.198.1"), netip.MustParseAddr("162.159.198.2"), netip.MustParseAddr("162.159.199.24")}
		h := []cachedEndpoint{{Endpoint: "8.34.70.4:859", Working: true}}
		got := desktopTargets(options{scanIntensity: "low"}, r, seeds, h)
		if len(got) < 15 || got[0].ip.String() != "162.159.198.2" {
			t.Fatalf("%s: %v", proto, got)
		}
		for _, p := range got {
			if !slices.Contains(masqueEndpointPorts, p.port) || !(masqueH2PoolsV4[0].Contains(p.ip) || masqueH2PoolsV4[1].Contains(p.ip)) {
				t.Fatal(p)
			}
		}
		v6 := netip.MustParseAddr("2606:4700:103::1")
		got = desktopTargets(options{ipv6: true}, r, []netip.Addr{v6}, h)
		if len(got) != len(masqueEndpointPorts) {
			t.Fatalf("v6 pair coverage: %v", got)
		}
		for _, pair := range got {
			if pair.ip != v6 {
				t.Fatal("v4 hints leaked into v6")
			}
		}
	}
}

type fixturePlane struct {
	https          []bool
	checks, bursts int
	loss           float32
	rtt            time.Duration
	torn           bool
}

func (f *fixturePlane) selectionMeta(context.Context, time.Duration) (metaResult, bool) {
	ok := f.https[f.checks]
	f.checks++
	return metaResult{}, ok
}
func (f *fixturePlane) tunnelPing(context.Context, int, time.Duration) (time.Duration, float32, bool) {
	f.bursts++
	return f.rtt, f.loss, f.torn
}

func TestQualityGateOnOneSession(t *testing.T) {
	for _, tc := range []struct {
		name   string
		f      fixturePlane
		ok     bool
		bursts int
	}{
		{"no data", fixturePlane{https: []bool{false}}, false, 0},
		{"clean", fixturePlane{https: []bool{true}, rtt: time.Millisecond}, true, 1},
		{"collapsed", fixturePlane{https: []bool{true, false}, torn: true, loss: 1}, false, 1},
		{"icmp blocked but https survived", fixturePlane{https: []bool{true, true}, torn: true, loss: 1}, true, 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			f := tc.f
			r := measureDesktopSession(context.Background(), &f, probeTarget{}, "fixture", 7, time.Second)
			if r.working() != tc.ok || f.bursts != tc.bursts || f.checks != len(f.https) || r.working() && (!r.measured || r.loss != f.loss) {
				t.Fatalf("bad session result: %+v", r)
			}
		})
	}
}

func TestEarlyStopNeedsQualityNotJustHTTPS(t *testing.T) {
	o := options{desktop: true, tunnelParallel: 1, earlyStop: 2, scanIntensity: "low"}
	var final scanProgressMsg
	_, err := verifyTargets(context.Background(), o, mustParseProto(protoWG), fixtureTargets(8),
		func(m tea.Msg) {
			if p, ok := m.(scanProgressMsg); ok && p.finished {
				final = p
			}
		},
		func() (tunnel, error) { return &fakeScanTunnel{}, nil },
		func(ctx context.Context, tn tunnel, t probeTarget) endpointResult {
			return endpointResult{ok: true, durable: true, measured: true, tunPing: []time.Duration{0, time.Second, 100 * time.Millisecond}[min(2, t.port)]}
		})
	if err != nil || final.completed != 4 || final.succeeded != 4 || final.quality != 2 || final.skipped != 4 {
		t.Fatalf("%+v, %v", final, err)
	}
}

func TestVerifiedMetaRejectsFalseHTTPPositives(t *testing.T) {
	const valid = `{"clientIp":"203.0.113.12"}`
	for _, tc := range []struct {
		status int
		body   string
		ok     bool
	}{
		{200, valid, true}, {503, valid, false}, {200, "<html>please log in</html>", false}, {200, `{"clientIp":"127.0.0.1"}`, false},
		{200, `{"clientIp":"::ffff:192.168.1.2"}`, false},
	} {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(tc.status); io.WriteString(w, tc.body) }))
		body, ok := fetchMeta(context.Background(), srv.Client(), srv.URL)
		srv.Close()
		_, validBody := verifiedMeta(body)
		if (ok && validBody) != tc.ok {
			t.Fatalf("invalid acceptance: %+v", tc)
		}
	}
}
