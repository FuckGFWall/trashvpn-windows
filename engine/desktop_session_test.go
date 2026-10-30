package main

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
)

// All host writes, persistence and network operations here are isolated fakes.
func TestDesktopSessionCaptureTiming(t *testing.T) {
	for _, optimistic := range []bool{false, true} {
		for _, tun := range []bool{false, true} {
			t.Run(map[bool]string{true: "fast", false: "strict"}[optimistic]+map[bool]string{true: "-tun", false: "-proxy"}[tun], func(t *testing.T) {
				var order []string
				var stored clientState
				captured, restored := false, false
				baseline := []byte(`{"original":"PAC and proxy baseline"}`)
				initial := clientState{Connected: true, Verification: "checking", CorePID: 123, TUN: tun, MixedPort: 17891, APIPort: 19090, StartedAt: "session"}
				probe := clientState{ExitIP: "192.0.2.2", Country: "US"}
				ops := desktopSessionOps{
					verify: func(ctx context.Context, usable func(clientState) error) (clientState, error) {
						order = append(order, "https")
						if err := usable(probe); err != nil {
							return clientState{}, err
						}
						// A repeated readiness notification must not re-snapshot our own proxy.
						if err := usable(probe); err != nil {
							return clientState{}, err
						}
						if captured != optimistic {
							t.Error("incorrect capture timing")
						}
						if optimistic && (stored.Verification != "checking" || stored.CorePID != 123 || !reflect.DeepEqual(stored.ProxySnap, map[bool][]byte{true: nil, false: baseline}[tun])) {
							t.Error("background check lacks a recoverable live session")
						}
						order = append(order, "payload")
						probe.MeasuredBps = 8192
						return probe, nil
					},
					capture: func(s *clientState) error {
						if captured {
							t.Error("captured twice, baseline lost")
						}
						captured = true
						order = append(order, "capture")
						if !s.TUN {
							s.ProxySnap = baseline
						}
						return nil
					},
					persist: func(s clientState) error { stored = s; order = append(order, s.Verification); return nil },
					restore: func(clientState) error { restored = true; return nil },
					connection: func(event string) {
						if !captured || stored.Verification != "checking" || stored.ExitIP != probe.ExitIP {
							t.Error("connected before real HTTPS, host capture or recovery commit")
						}
						order = append(order, event)
					},
				}
				got, err := verifyDesktopSession(context.Background(), initial, optimistic, ops)
				want := []string{"https", "payload", "capture", "verified"}
				if optimistic {
					want = []string{"https", "capture", "checking", "provisional", "payload", "verified"}
				}
				if err != nil || restored || !reflect.DeepEqual(order, want) || got.Verification != "verified" || got.MeasuredBps != 8192 || got.CorePID != initial.CorePID || got.StartedAt != initial.StartedAt {
					t.Fatalf("session not safely committed: %v %+v order=%v", err, got, order)
				}
			})
		}
	}
}

func TestDesktopSessionFailuresRestoreBaseline(t *testing.T) {
	for _, failure := range []string{"https", "payload", "cancel", "capture", "checking", "verified", "rollback"} {
		t.Run(failure, func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			var stored clientState
			var events []string
			captures, restores := 0, 0
			baseline := []byte(`{"original":true}`)
			fixtureError := errors.New("fixture " + failure)
			ops := desktopSessionOps{
				verify: func(ctx context.Context, usable func(clientState) error) (clientState, error) {
					if failure == "https" {
						return clientState{}, fixtureError
					}
					if err := usable(clientState{ExitIP: "192.0.2.2"}); err != nil {
						return clientState{}, err
					}
					if failure == "cancel" {
						cancel()
						return clientState{}, ctx.Err()
					}
					if failure == "payload" || failure == "rollback" {
						return clientState{}, fixtureError
					}
					return clientState{ExitIP: "192.0.2.2"}, nil
				},
				capture: func(s *clientState) error {
					captures++
					s.ProxySnap = baseline
					if failure == "capture" {
						return fixtureError
					}
					return nil
				},
				persist: func(s clientState) error {
					stored = s
					if s.Verification == failure {
						return fixtureError
					}
					return nil
				},
				restore: func(s clientState) error {
					restores++
					if !reflect.DeepEqual(s.ProxySnap, baseline) || s.CorePID != 123 {
						t.Error("wrong restoration baseline / PID")
					}
					if failure == "rollback" {
						return errors.New("fixture restore failed")
					}
					return nil
				},
				connection: func(event string) { events = append(events, event) },
			}
			_, err := verifyDesktopSession(ctx, clientState{Connected: true, CorePID: 123}, true, ops)
			if err == nil {
				t.Fatal("verification / persistence failure falsely succeeded")
			}
			if failure == "https" {
				if captures != 0 || restores != 0 || len(events) != 0 {
					t.Fatal("failed HTTPS changed host state or UI")
				}
			} else if captures != 1 || restores != 1 || stored.Verification != "failed" || stored.CorePID != 123 || !reflect.DeepEqual(stored.ProxySnap, baseline) {
				t.Fatalf("failed session not restored / recoverable: capture=%d restore=%d state=%+v", captures, restores, stored)
			}
			if (failure == "capture" || failure == "checking") && len(events) != 0 {
				t.Fatal("UI connected before capture / recovery record succeeded")
			}
			if failure == "cancel" && !errors.Is(err, context.Canceled) {
				t.Fatal("cancellation lost")
			}
			if failure == "rollback" && !strings.Contains(err.Error(), "fixture restore failed") {
				t.Fatal("cleanup failure hidden")
			}
		})
	}
}

func TestDesktopSessionStrictFailureDoesNotCapture(t *testing.T) {
	called := false
	_, err := verifyDesktopSession(context.Background(), clientState{}, false, desktopSessionOps{
		verify: func(ctx context.Context, usable func(clientState) error) (clientState, error) {
			if err := usable(clientState{}); err != nil {
				t.Fatal(err)
			}
			return clientState{}, errors.New("incomplete payload")
		},
		capture:    func(*clientState) error { called = true; return nil },
		connection: func(string) { t.Error("strict mode published provisional connection") },
	})
	if err == nil || called {
		t.Fatal("strict mode captured before full payload verification")
	}
}

func TestDesktopSessionBlockedProxyDoesNotAdvertiseUsable(t *testing.T) {
	provisional := false
	got, err := verifyDesktopSession(context.Background(), clientState{}, true, desktopSessionOps{
		verify: func(ctx context.Context, usable func(clientState) error) (clientState, error) {
			return clientState{}, usable(clientState{})
		},
		capture: func(s *clientState) error { s.ProxyWarn = "fixture write blocked"; return nil },
		persist: func(clientState) error { return nil }, restore: func(clientState) error { return nil },
		connection: func(string) { provisional = true },
	})
	if err != nil || provisional || got.ProxyWarn == "" || got.Verification != "verified" {
		t.Fatal("blocked host capture advertised as usable or lost its warning")
	}
}

func TestDesktopSessionCancelledBeforeStarting(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err := verifyDesktopSession(ctx, clientState{}, true, desktopSessionOps{})
	if !errors.Is(err, context.Canceled) {
		t.Fatal("pre-cancelled session touched operations")
	}
}
