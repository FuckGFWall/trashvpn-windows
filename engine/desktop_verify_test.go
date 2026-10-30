package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/core"
)

type desktopRoundTrip func(*http.Request) (*http.Response, error)

func (f desktopRoundTrip) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func desktopResponse(r *http.Request, code int, body string) *http.Response {
	return &http.Response{StatusCode: code, Body: io.NopCloser(strings.NewReader(body)), Header: make(http.Header), Request: r}
}

func TestDesktopHTTPSCancellationWinsOverRacingSuccess(t *testing.T) {
	for range 20 {
		ctx, cancel := context.WithCancel(context.Background())
		client := &http.Client{Transport: desktopRoundTrip(func(r *http.Request) (*http.Response, error) {
			cancel()
			return desktopResponse(r, 200, "192.0.2.2"), nil
		})}
		_, err := verifyDesktopHTTPS(ctx, client, []string{"https://fixture.invalid"})
		cancel()
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("racing success masked cancellation: %v", err)
		}
	}
}

func TestDesktopHTTPSPreservesFallbackSourcesAndMetadata(t *testing.T) {
	for _, source := range []string{"meta", "fallback"} {
		client := &http.Client{Transport: desktopRoundTrip(func(r *http.Request) (*http.Response, error) {
			if r.URL.String() == "https://speed.cloudflare.com/meta" {
				if r.Header.Get("Referer") != metaReferer {
					t.Error("metadata Referer missing")
				}
				if source == "meta" {
					return desktopResponse(r, 200, `{"clientIp":"192.0.2.8","country":"US","colo":{"iata":"LAX","city":"Los Angeles"}}`), nil
				}
				return desktopResponse(r, 403, "refused"), nil
			}
			if r.URL.String() == "https://api.ipify.org" || source == "meta" {
				return desktopResponse(r, 503, "unavailable"), nil
			}
			return desktopResponse(r, 200, "ip=192.0.2.8\nloc=US\ncolo=LAX\n"), nil
		})}
		probe, err := verifyDesktopFamily(context.Background(), client, famV4, dataPlaneSources, nil)
		if err != nil || probe.ExitIP != "192.0.2.8" || probe.Country != "US" || probe.Colo != "LAX" || source == "meta" && probe.NodeCity != "Los Angeles" {
			t.Fatalf("existing %s metadata / fallback lost: %v %+v", source, err, probe)
		}
	}
}

func TestDesktopHTTPSFastProviderCancelsAndJoinsSlowProvider(t *testing.T) {
	started := make(chan struct{})
	var exited atomic.Bool
	client := &http.Client{Transport: desktopRoundTrip(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "slow.invalid" {
			close(started)
			<-r.Context().Done()
			exited.Store(true)
			return nil, r.Context().Err()
		}
		select {
		case <-started:
		case <-r.Context().Done():
			return nil, r.Context().Err()
		}
		return desktopResponse(r, 200, "192.0.2.9"), nil
	})}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	start := time.Now()
	probe, err := verifyDesktopHTTPS(ctx, client, []string{"https://slow.invalid", "https://fast.invalid"})
	if err != nil || probe.ExitIP != "192.0.2.9" || !exited.Load() || time.Since(start) > time.Second {
		t.Fatalf("fast provider blocked / slow request leaked: %v %+v joined=%v", err, probe, exited.Load())
	}
}

func TestDesktopHTTPSConcurrencyIsBoundedAndAllFailuresFail(t *testing.T) {
	var active, maximum, requests atomic.Int32
	barrier := make(chan struct{})
	client := &http.Client{Transport: desktopRoundTrip(func(r *http.Request) (*http.Response, error) {
		n := active.Add(1)
		defer active.Add(-1)
		for before := maximum.Load(); n > before; before = maximum.Load() {
			if maximum.CompareAndSwap(before, n) {
				break
			}
		}
		if requests.Add(1) == 2 {
			close(barrier)
		}
		select {
		case <-barrier:
		case <-r.Context().Done():
			return nil, r.Context().Err()
		}
		return desktopResponse(r, 503, "unavailable"), nil
	})}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	urls := []string{"https://a.invalid", "https://b.invalid", "https://c.invalid", "https://d.invalid", "https://e.invalid"}
	_, err := verifyDesktopHTTPS(ctx, client, urls)
	if err == nil || maximum.Load() != 2 || active.Load() != 0 || requests.Load() != int32(len(urls)) {
		t.Fatalf("bad bounded verification: %v max=%d active=%d requests=%d", err, maximum.Load(), active.Load(), requests.Load())
	}
	if _, err := verifyDesktopHTTPS(ctx, client, nil); err == nil {
		t.Fatal("empty providers counted connected")
	}
}

func TestDesktopHTTPSRequiresCompleteSecureResponse(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/truncated":
			w.Header().Set("Content-Length", "1024")
			fmt.Fprint(w, "ip=192.0.2.2\n")
		case "/oversized":
			fmt.Fprint(w, strings.Repeat("x", 65537))
		case "/redirect":
			http.Redirect(w, r, "http://plaintext.invalid", http.StatusFound)
		}
	}))
	defer server.Close()
	for _, path := range []string{"/truncated", "/oversized"} {
		if _, err := verifyDesktopHTTPS(context.Background(), server.Client(), []string{server.URL + path}); err == nil {
			t.Errorf("%s counted as completed proof", path)
		}
	}
	plain := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { t.Error("plaintext target was requested") }))
	defer plain.Close()
	if _, err := verifyDesktopHTTPS(context.Background(), plain.Client(), []string{plain.URL}); err == nil {
		t.Fatal("plaintext counted as trusted HTTPS")
	}
	// No network: a redirecting transport models the final downgraded response.
	client := &http.Client{Transport: desktopRoundTrip(func(r *http.Request) (*http.Response, error) {
		response := desktopResponse(r, 200, "192.0.2.2")
		copy := r.Clone(r.Context())
		u := *r.URL
		u.Scheme = "http"
		copy.URL = &u
		response.Request = copy
		return response, nil
	})}
	if _, err := verifyDesktopHTTPS(context.Background(), client, []string{"https://fixture.invalid"}); err == nil {
		t.Fatal("downgraded response counted as HTTPS")
	}
}

func TestDesktopDualStackChecksRunTogetherAndRequireBoth(t *testing.T) {
	for _, failV6 := range []bool{false, true} {
		var requests atomic.Int32
		barrier := make(chan struct{})
		client := &http.Client{Transport: desktopRoundTrip(func(r *http.Request) (*http.Response, error) {
			if requests.Add(1) == 2 {
				close(barrier)
			}
			select {
			case <-barrier:
			case <-r.Context().Done():
				return nil, r.Context().Err()
			}
			if r.URL.Host == "v6.invalid" {
				if failV6 {
					return desktopResponse(r, 200, "ip=not-an-ip\n"), nil
				}
				return desktopResponse(r, 200, "ip=2001:db8::1\n"), nil
			}
			return desktopResponse(r, 200, "ip=192.0.2.2\nloc=US\n"), nil
		})}
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		probe, err := verifyDesktopFamily(ctx, client, famBoth, []string{"https://v4.invalid"}, []string{"https://v6.invalid"})
		cancel()
		if (err != nil) != failV6 || requests.Load() != 2 || probe.ExitIP != "192.0.2.2" {
			t.Fatalf("dual-stack skipped, serialized or falsely verified: %v %+v requests=%d", err, probe, requests.Load())
		}
	}
}

func TestDesktopVerificationStagesKeepPayloadGate(t *testing.T) {
	for _, scenario := range []string{"success", "retry", "https-failure", "payload-failure", "capture-failure", "v6", "v6-invalid"} {
		t.Run(scenario, func(t *testing.T) {
			var order []string
			attempts := 0
			family := famV4
			if strings.HasPrefix(scenario, "v6") {
				family = famV6
			}
			probe, err := verifyDesktopSteps(context.Background(), family, func(string) {}, func(p clientState) error {
				order = append(order, "usable")
				if scenario == "capture-failure" {
					return errors.New("host capture failed")
				}
				return nil
			}, desktopVerifyOps{
				basic: func(context.Context) (clientState, error) {
					attempts++
					order = append(order, "https")
					if scenario == "https-failure" || scenario == "retry" && attempts == 1 {
						return clientState{}, errors.New("no HTTPS")
					}
					ip := "192.0.2.2"
					if scenario == "v6" {
						ip = "2001:db8::1"
					}
					return clientState{ExitIP: ip}, nil
				},
				payload: func(context.Context) (int64, error) {
					order = append(order, "payload")
					if scenario == "payload-failure" {
						return 0, errors.New("short payload")
					}
					return 8192, nil
				},
			})
			want := []string{"https", "usable", "payload"}
			failure := false
			switch scenario {
			case "retry":
				want = []string{"https", "https", "usable", "payload"}
			case "https-failure":
				want = []string{"https", "https"}
				failure = true
			case "payload-failure":
				failure = true
			case "capture-failure":
				want = []string{"https", "usable"}
				failure = true
			case "v6":
				want = []string{"https", "usable"}
			case "v6-invalid":
				want = []string{"https"}
				failure = true
			}
			if (err != nil) != failure || !reflect.DeepEqual(order, want) {
				t.Fatalf("verification order / accuracy changed: %v order=%v", err, order)
			}
			if !failure && family != famV6 && probe.MeasuredBps != 8192 {
				t.Fatal("full payload measurement lost")
			}
		})
	}
}

func TestDesktopVerificationRetryAndPayloadAreCancellable(t *testing.T) {
	for _, stage := range []string{"retry", "payload", "before-capture"} {
		ctx, cancel := context.WithCancel(context.Background())
		start := time.Now()
		_, err := verifyDesktopSteps(ctx, famV4, func(string) {}, func(clientState) error {
			if stage == "before-capture" {
				cancel()
				return ctx.Err()
			}
			return nil
		}, desktopVerifyOps{
			basic: func(context.Context) (clientState, error) {
				if stage == "retry" {
					time.AfterFunc(20*time.Millisecond, cancel)
					return clientState{}, errors.New("retry me")
				}
				return clientState{}, nil
			},
			payload:    func(ctx context.Context) (int64, error) { cancel(); return 0, ctx.Err() },
			retryDelay: time.Hour,
		})
		cancel()
		if !errors.Is(err, context.Canceled) || time.Since(start) > time.Second {
			t.Fatalf("%s cancellation blocked: %v", stage, err)
		}
	}
}

func TestDesktopHTTPSVerification(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.TLS == nil {
			t.Error("not TLS")
		}
		if r.URL.Path == "/bad" {
			w.WriteHeader(503)
			return
		}
		fmt.Fprintln(w, "ip=192.0.2.2")
	}))
	defer server.Close()
	probe, e := verifyDesktopHTTPS(context.Background(), server.Client(), []string{server.URL + "/bad", server.URL + "/ok"})
	if e != nil || probe.ExitIP != "192.0.2.2" {
		t.Fatalf("HTTPS verification: %v %+v", e, probe)
	}
	if _, e = verifyDesktopHTTPS(context.Background(), server.Client(), []string{server.URL + "/bad"}); e == nil {
		t.Fatal("HTTP failure counted connected")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, e = verifyDesktopHTTPS(ctx, server.Client(), []string{server.URL}); e == nil {
		t.Fatal("cancel ignored")
	}
}
func TestDesktopModeVerificationPolicy(t *testing.T) {
	for _, optimistic := range []bool{false, true} {
		for _, fail := range []bool{false, true} {
			old := clientState{Connected: true, TUN: false, Verification: "verified", CorePID: 123}
			stored := old
			verified, hostChanged := false, false
			var events []string
			ops := modeSwitchOps{
				reload: func(context.Context, []byte, bool) error { return nil }, config: func([]byte) error { return nil },
				state:    func(s clientState) error { stored = s; return nil },
				setProxy: func() (*core.ProxySnapshot, error) { return &core.ProxySnapshot{}, nil }, restoreProxy: func(*core.ProxySnapshot) error { hostChanged = true; return nil },
				verify: func(ctx context.Context) (clientState, error) {
					if hostChanged {
						t.Error("capture changed before verification")
					}
					if stored.Verification != "checking" || stored.TUN != old.TUN {
						t.Error("unsafe provisional state")
					}
					if fail {
						return clientState{}, fmt.Errorf("fixture unavailable")
					}
					verified = true
					return clientState{ExitIP: "192.0.2.3"}, nil
				},
				connection: func(s string) {
					if (s == "verified" || s == "provisional") && !verified {
						t.Error("UI connected before HTTPS verification")
					}
					events = append(events, s)
				}, optimistic: optimistic,
			}
			err := applyDesktopModes(context.Background(), old, []byte("old"), []byte("new"), false, ops)
			if len(events) == 0 || events[0] != "verifying" {
				t.Fatalf("incorrect UI phase: %v", events)
			}
			if fail {
				if err == nil || stored.Verification != "failed" || stored.ExitIP != "" {
					t.Fatalf("failed mode not rolled back: %v %+v", err, stored)
				}
			} else {
				if err != nil || stored.Verification != "verified" || stored.ExitIP != "192.0.2.3" || events[len(events)-1] != "verified" {
					t.Fatalf("verification not committed: %v %+v", err, stored)
				}
			}
		}
	}
}

func TestDesktopHotSwitchAdvertisesConnectionOnlyAfterCapture(t *testing.T) {
	for _, oldTun := range []bool{false, true} {
		for _, failCommit := range []bool{false, true} {
			baseline := &core.ProxySnapshot{}
			old := clientState{Connected: true, TUN: oldTun, NoProxy: oldTun, Verification: "verified", CorePID: 123}
			old.ProxySnap = []byte(`{}`)
			stored, liveTun, hostProxy := old, oldTun, !oldTun
			verified, provisional := false, false
			checks := 0
			ops := modeSwitchOps{
				reload:       func(ctx context.Context, b []byte, tun bool) error { liveTun = tun; return nil },
				setProxy:     func() (*core.ProxySnapshot, error) { hostProxy = true; return baseline, nil },
				restoreProxy: func(*core.ProxySnapshot) error { hostProxy = false; return nil },
				config: func(b []byte) error {
					if failCommit && string(b) == "after" {
						return errors.New("fixture commit failed")
					}
					return nil
				},
				state: func(s clientState) error { stored = s; return nil },
				verify: func(context.Context) (clientState, error) {
					checks++
					verified = true
					return clientState{ExitIP: "192.0.2.2"}, nil
				},
				optimistic: true,
				connection: func(event string) {
					if event == "provisional" {
						provisional = true
						if !verified || liveTun != !oldTun || hostProxy != oldTun {
							t.Error("hot-switch connected before actual capture / HTTPS")
						}
					}
				},
			}
			err := applyDesktopModes(context.Background(), old, []byte("before"), []byte("after"), !oldTun, ops)
			if !provisional || (err != nil) != failCommit {
				t.Fatalf("wrong mode-switch result: %v", err)
			}
			if failCommit && (stored.TUN != oldTun || hostProxy != !oldTun || liveTun != oldTun || checks != 2 || stored.Verification != "verified") {
				t.Fatal("provisional switch failed to restore and verify original route")
			}
		}
	}
}

func TestDesktopFailedSwitchVerifiesRestoredConnection(t *testing.T) {
	old := clientState{Connected: true, Verification: "verified"}
	stored := old
	checks := 0
	ops := modeSwitchOps{
		reload: func(context.Context, []byte, bool) error { return nil }, config: func([]byte) error { return nil },
		state: func(s clientState) error { stored = s; return nil },
		verify: func(context.Context) (clientState, error) {
			checks++
			if checks == 1 {
				return clientState{}, fmt.Errorf("new route unavailable")
			}
			return clientState{}, nil
		},
	}
	if e := applyDesktopModes(context.Background(), old, []byte("old"), []byte("new"), false, ops); e == nil {
		t.Fatal("failed switch not reported")
	}
	if checks != 2 || stored.Verification != "verified" {
		t.Fatal("restored connection was not verified before being marked connected")
	}
}
