package main

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/xhwarp/warp-client/internal/core"
)

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
					if s == "verified" && !verified {
						t.Error("UI connected before HTTPS verification")
					}
					events = append(events, s)
				}, optimistic: optimistic,
			}
			err := applyDesktopModes(context.Background(), old, []byte("old"), []byte("new"), false, ops)
			want := "verifying"
			if optimistic {
				want = "provisional"
			}
			if len(events) == 0 || events[0] != want {
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
