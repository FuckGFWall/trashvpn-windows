package main

import (
	"context"
	"crypto/tls"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/quic-go/quic-go"
	"github.com/quic-go/quic-go/http3"
)

type fakeScanTunnel struct {
	handshakeFn func(context.Context, string) bool
	closeFn     func()
}

func (f *fakeScanTunnel) handshake(ctx context.Context, ep string, _ time.Duration) bool {
	return f.handshakeFn(ctx, ep)
}
func (*fakeScanTunnel) stack() *ipStack { return nil }
func (*fakeScanTunnel) ports() []int    { return []int{2408} }
func (*fakeScanTunnel) attempts() int   { return 1 }
func (f *fakeScanTunnel) Close() {
	if f.closeFn != nil {
		f.closeFn()
	}
}

func fixtureTargets(n int) []probeTarget {
	out := make([]probeTarget, n)
	for i := range out {
		out[i] = probeTarget{ip: netip.AddrFrom4([4]byte{192, 0, 2, byte(i + 1)}), port: i}
	}
	return out
}

func TestScanStrengthControlsBothPhases(t *testing.T) {
	for i, level := range []string{"low", "medium", "high"} {
		o := options{}
		applyScanIntensity(&o, level)
		if scanPortSamples(o) != []int{4, 8, 12}[i] || o.earlyStop != []int{2, 4, 8}[i] {
			t.Fatalf("wrong %s budgets: %+v", level, o)
		}
		if o.scanBudget != []time.Duration{25 * time.Second, 45 * time.Second, 75 * time.Second}[i] || o.candidateBudget <= 0 {
			t.Fatalf("unbounded %s scan: %+v", level, o)
		}
	}
	if scanPortSamples(options{}) != portProbeSample {
		t.Fatal("CLI default changed")
	}
}

func TestCandidateBudgetMovesOnInsteadOfSweepingPortsForever(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	var final scanProgressMsg
	results, err := verifyTargets(ctx, options{tunnelParallel: 1, earlyStop: 1, candidateBudget: 20 * time.Millisecond}, mustParseProto(protoAWG), fixtureTargets(5),
		func(m tea.Msg) {
			if s, ok := m.(scanProgressMsg); ok && s.finished {
				final = s
			}
		},
		func() (tunnel, error) { return &fakeScanTunnel{}, nil },
		func(ctx context.Context, _ tunnel, target probeTarget) endpointResult {
			if target.port == 0 {
				<-ctx.Done()
				return endpointResult{ok: true, durable: true}
			}
			return endpointResult{ok: true, durable: true, endpoint: "fixture"}
		})
	if err != nil || ctx.Err() != nil || len(workingSorted(results)) != 1 || final.completed != 2 || final.succeeded != 1 || final.skipped != 3 {
		t.Fatalf("candidate timeout accepted a stale result or failed to continue: %+v %v", final, err)
	}
}

func TestScanTimeBudgetRetainsVerifiedResults(t *testing.T) {
	for _, partial := range []bool{false, true} {
		t.Run(fmt.Sprint(partial), func(t *testing.T) {
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			defer cancel()
			var final scanProgressMsg
			start := time.Now()
			results, err := verifyTargets(ctx, options{tunnelParallel: 4, earlyStop: 8, scanBudget: 30 * time.Millisecond}, mustParseProto(protoWG), fixtureTargets(40),
				func(m tea.Msg) {
					if s, ok := m.(scanProgressMsg); ok && s.finished {
						final = s
					}
				},
				func() (tunnel, error) { return &fakeScanTunnel{}, nil },
				func(ctx context.Context, _ tunnel, target probeTarget) endpointResult {
					if partial && target.port == 0 {
						return endpointResult{ok: true, durable: true, endpoint: "fixture"}
					}
					<-ctx.Done()
					return endpointResult{}
				})
			if ctx.Err() != nil || time.Since(start) > 500*time.Millisecond || !final.budgetReached {
				t.Fatalf("budget did not stop independently: %+v %v", final, err)
			}
			if partial {
				if err != nil || len(workingSorted(results)) != 1 {
					t.Fatalf("partial results discarded: %v", err)
				}
			} else if err == nil || !strings.Contains(err.Error(), "时间预算") {
				t.Fatalf("missing useful timeout error: %v", err)
			}
		})
	}
}

func TestScanTargetCancelsActiveWorkersWithoutCancelingConnect(t *testing.T) {
	for _, level := range []string{"low", "medium", "high"} {
		t.Run(level, func(t *testing.T) {
			o := options{}
			applyScanIntensity(&o, level)
			o.tunnelParallel = o.earlyStop + 2
			parent, cancelParent := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancelParent()
			var active, started, closed atomic.Int32
			allStarted := make(chan struct{})
			var final scanProgressMsg
			emit := func(m tea.Msg) {
				if m, ok := m.(scanProgressMsg); ok && m.finished {
					final = m
				}
			}
			create := func() (tunnel, error) { return &fakeScanTunnel{closeFn: func() { closed.Add(1) }}, nil }
			probe := func(ctx context.Context, _ tunnel, target probeTarget) endpointResult {
				active.Add(1)
				defer active.Add(-1)
				if started.Add(1) == int32(o.tunnelParallel) {
					close(allStarted)
				}
				select {
				case <-allStarted:
				case <-ctx.Done():
					return endpointResult{}
				}
				probeStep(ctx, "握手")
				if target.port < o.earlyStop {
					return endpointResult{ok: true, durable: true, endpoint: target.ip.String()}
				}
				<-ctx.Done() // simulate a black-holed network request, no timeout shortcut
				return endpointResult{}
			}
			start := time.Now()
			results, err := verifyTargets(parent, o, mustParseProto(protoWG), fixtureTargets(40), emit, create, probe)
			if err != nil || parent.Err() != nil || time.Since(start) > time.Second {
				t.Fatalf("scan did not promptly succeed independently: %v parent=%v", err, parent.Err())
			}
			if active.Load() != 0 || closed.Load() != int32(o.tunnelParallel) {
				t.Fatal("workers or tunnels leaked")
			}
			if len(workingSorted(results)) != o.earlyStop || final.completed != o.earlyStop || final.succeeded != o.earlyStop || final.skipped != 40-o.earlyStop {
				t.Fatalf("wrong final outcomes: %+v", final)
			}
			// The same parent must still permit the next connection step.
			if err := runTunnelWorkers(parent, 1, 1, create, func(tunnel, int) {}); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestScanNoTargetCompletesAllAndCountsFailures(t *testing.T) {
	o := options{tunnelParallel: 3}
	var final scanProgressMsg
	_, err := verifyTargets(context.Background(), o, mustParseProto(protoWG), fixtureTargets(12),
		func(m tea.Msg) {
			if s, ok := m.(scanProgressMsg); ok && s.finished {
				final = s
			}
		},
		func() (tunnel, error) { return &fakeScanTunnel{}, nil },
		func(_ context.Context, _ tunnel, target probeTarget) endpointResult {
			return endpointResult{ok: target.port%2 == 0, durable: true}
		})
	if err != nil || final.completed != 12 || final.succeeded != 6 || final.skipped != 0 {
		t.Fatalf("%+v %v", final, err)
	}
}

func TestScanCountsOnlyDurableMatchingEndpoints(t *testing.T) {
	o := options{tunnelParallel: 1, earlyStop: 2, countries: []string{"CN"}}
	var final scanProgressMsg
	_, err := verifyTargets(context.Background(), o, mustParseProto(protoWG), fixtureTargets(8),
		func(m tea.Msg) {
			if s, ok := m.(scanProgressMsg); ok && s.finished {
				final = s
			}
		},
		func() (tunnel, error) { return &fakeScanTunnel{}, nil },
		func(_ context.Context, _ tunnel, target probeTarget) endpointResult {
			r := endpointResult{ok: true, durable: target.port > 0, exit: metaResult{coloISO: "CN"}}
			if target.port == 1 {
				r.exit.coloISO = "US"
			}
			return r
		})
	if err != nil || final.completed != 4 || final.succeeded != 2 || final.skipped != 4 {
		t.Fatalf("%+v %v", final, err)
	}
}

func TestScanExternalCancelPropagatesAndJoins(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	var active atomic.Int32
	_, err := verifyTargets(ctx, options{tunnelParallel: 4}, mustParseProto(protoMASQUE), fixtureTargets(40), func(tea.Msg) {},
		func() (tunnel, error) { return &fakeScanTunnel{}, nil },
		func(ctx context.Context, _ tunnel, _ probeTarget) endpointResult {
			active.Add(1)
			defer active.Add(-1)
			cancel()
			<-ctx.Done()
			return endpointResult{}
		})
	if err != context.Canceled || active.Load() != 0 {
		t.Fatalf("cancel not propagated: %v", err)
	}
}

func TestPortDiscoveryCancellationIsLocal(t *testing.T) {
	parent, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	var active, closed atomic.Int32
	create := func() (tunnel, error) {
		return &fakeScanTunnel{
			closeFn: func() { closed.Add(1) },
			handshakeFn: func(ctx context.Context, ep string) bool {
				active.Add(1)
				defer active.Add(-1)
				if strings.HasPrefix(ep, "192.0.2.1:") {
					return true
				}
				<-ctx.Done()
				return false
			}}, nil
	}
	ips := []netip.Addr{netip.MustParseAddr("192.0.2.1"), netip.MustParseAddr("192.0.2.2"), netip.MustParseAddr("192.0.2.3")}
	ports, err := probePortsWithFactory(parent, ips, []int{2408, 500}, time.Minute, 3, func(tea.Msg) {}, "primary", create)
	if err != nil || len(ports) != 2 || parent.Err() != nil || active.Load() != 0 || closed.Load() != 3 {
		t.Fatalf("port discovery cancellation failed: %v %v", ports, err)
	}
}

func TestScanProgressIsMonotonicAcrossPhasesAndNestedCaches(t *testing.T) {
	for _, chain := range []bool{false, true} {
		var events []map[string]any
		p := &scanProgressTracker{chain: chain, send: func(e map[string]any) { events = append(events, e) }}
		p.begin(protoWG)
		p.update(scanProgressMsg{stage: "ports-primary", completed: 4, total: 4, finished: true})
		p.update(scanProgressMsg{stage: "ports-alternate", total: 4})
		p.update(scanProgressMsg{stage: "ports-alternate", completed: 4, total: 4, finished: true})
		p.update(scanProgressMsg{stage: "verify", completed: 2, succeeded: 2, skipped: 12, total: 14, target: 2, finished: true})
		p.note("rank", "排序")
		p.note("cache-write", "保存缓存")
		p.ready(2, false)
		if chain {
			p.begin(protoWG)
			p.ready(4, true)
		}
		if p.percent >= 100 {
			t.Fatal("progress finished before preparation")
		}
		p.finish()
		last := -1.0
		for _, e := range events {
			v := e["done"].(float64)
			if v < last || v > 100 {
				t.Fatalf("progress regressed: %v -> %v", last, v)
			}
			last = v
		}
		if last != 100 {
			t.Fatal("not finished")
		}
		if !chain && (events[len(events)-1]["skipped"] != 12 || !strings.Contains(p.text, "跳过 12")) {
			t.Fatal("final outcomes lost")
		}
	}
}

func TestTunnelPingAndDNSWaitsObserveCancel(t *testing.T) {
	dev, stack, err := newIPStack([]netip.Addr{netip.MustParseAddr("192.0.2.2")}, 1280)
	if err != nil {
		t.Fatal(err)
	}
	// A real tunnel always consumes outbound packets. Drain them into a local
	// black hole here instead of leaving netstack's WriteNotify blocked.
	drained := make(chan struct{})
	go func() {
		defer close(drained)
		bufs, sizes := [][]byte{make([]byte, 65535)}, []int{0}
		for {
			if _, err := dev.Read(bufs, sizes, 0); err != nil {
				return
			}
		}
	}()
	defer func() { _ = dev.Close(); <-drained }()
	savedTarget, savedPing := pingTarget, pingAddr.Load()
	defer func() { pingTarget = savedTarget; pingAddr.Store(savedPing) }()
	for _, step := range []string{"ping", "dns"} {
		t.Run(step, func(t *testing.T) {
			pingAddr.Store(nil)
			pingTarget = "1.1.1.1"
			if step == "dns" {
				pingTarget = "fixture.invalid"
			}
			ctx, cancel := context.WithCancel(context.Background())
			time.AfterFunc(30*time.Millisecond, cancel)
			defer cancel()
			done := make(chan struct{})
			go func() { defer close(done); stack.tunnelPing(ctx, 10, time.Minute) }()
			select {
			case <-done:
			case <-time.After(time.Second):
				t.Fatal("uncancelable netstack wait")
			}
		})
	}
}

func TestMasqueH2AndH3HeaderWaitsObserveCancel(t *testing.T) {
	_, certPEM, keyPEM, err := probePEMCredentials()
	if err != nil {
		t.Fatal(err)
	}
	cert, err := tls.X509KeyPair([]byte(certPEM), []byte(keyPEM))
	if err != nil {
		t.Fatal(err)
	}
	for _, h2 := range []bool{true, false} {
		t.Run(fmt.Sprintf("h2=%v", h2), func(t *testing.T) {
			entered := make(chan struct{}, 1)
			handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				select {
				case entered <- struct{}{}:
				default:
				}
				<-r.Context().Done() // deliberately never return CONNECT response headers
			})
			var remote net.Addr
			if h2 {
				s := httptest.NewUnstartedServer(handler)
				s.EnableHTTP2 = true
				s.StartTLS()
				defer s.Close()
				remote, err = net.ResolveTCPAddr("tcp", s.Listener.Addr().String())
			} else {
				l, e := quic.ListenAddr("127.0.0.1:0", &tls.Config{Certificates: []tls.Certificate{cert}, NextProtos: []string{http3.NextProtoH3}}, &quic.Config{EnableDatagrams: true})
				if e != nil {
					t.Fatal(e)
				}
				s := &http3.Server{Handler: handler, EnableDatagrams: true}
				serverDone := make(chan struct{})
				go func() { defer close(serverDone); _ = s.ServeListener(l) }()
				defer func() { _ = s.Close(); _ = l.Close(); <-serverDone }()
				remote = l.Addr()
			}
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			done := make(chan error, 1)
			go func() {
				_, closeAll, e := dialMasqueTransport(ctx, &tls.Config{InsecureSkipVerify: true, NextProtos: []string{http3.NextProtoH3}}, &quic.Config{EnableDatagrams: true}, remote, h2, nil)
				closeAll()
				done <- e
			}()
			select {
			case <-entered:
			case <-ctx.Done():
				t.Fatal("fixture CONNECT not received")
			}
			start := time.Now()
			cancel()
			select {
			case e := <-done:
				if e == nil || time.Since(start) > time.Second {
					t.Fatalf("unexpected result: %v", e)
				}
			case <-time.After(time.Second):
				t.Fatal("CONNECT header wait ignored cancellation")
			}
		})
	}
}

func TestMasqueH3DefaultProbeAllowsSlowConnectResponse(t *testing.T) {
	_, certPEM, keyPEM, err := probePEMCredentials()
	if err != nil {
		t.Fatal(err)
	}
	cert, err := tls.X509KeyPair([]byte(certPEM), []byte(keyPEM))
	if err != nil {
		t.Fatal(err)
	}
	l, err := quic.ListenAddr("127.0.0.1:0", &tls.Config{Certificates: []tls.Certificate{cert}, NextProtos: []string{http3.NextProtoH3}}, &quic.Config{EnableDatagrams: true})
	if err != nil {
		t.Fatal(err)
	}
	s := &http3.Server{EnableDatagrams: true, Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		timer := time.NewTimer(2100 * time.Millisecond)
		defer timer.Stop()
		select {
		case <-timer.C:
		case <-r.Context().Done():
			return
		}
		w.WriteHeader(http.StatusOK)
		w.(http.Flusher).Flush()
		<-r.Context().Done()
	})}
	done := make(chan struct{})
	go func() { defer close(done); _ = s.ServeListener(l) }()
	defer func() { _ = s.Close(); _ = l.Close(); <-done }()
	dev, stack, err := newIPStack([]netip.Addr{netip.MustParseAddr("192.0.2.2")}, 1280)
	if err != nil {
		t.Fatal(err)
	}
	tn := &masqueTunnel{dev: dev, ipStack: stack, tlsCfg: &tls.Config{InsecureSkipVerify: true, NextProtos: []string{http3.NextProtoH3}}}
	defer tn.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if !tn.handshake(ctx, l.Addr().String(), 2*time.Second) {
		t.Fatal("healthy H3 CONNECT was cut off by WG's 2s timeout")
	}
	if !tn.samePeer(l.Addr().String()) {
		t.Fatal("successful tunnel lost immediately after handshake")
	}
}
