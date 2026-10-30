package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/cache"
)

func TestPreparationSettingsAndIntensity(t *testing.T) {
	s := defaultSettings()
	if s.VerifyBeforeConnect || s.ScanIntensity != "medium" {
		t.Fatal("wrong defaults")
	}
	lastSamples, lastStop := 0, 0
	for _, level := range []string{"low", "medium", "high"} {
		s.ScanIntensity = level
		o := desktopConnectOptions(s)
		if !o.optimistic || !o.desktop || o.perSubnet <= lastSamples || o.earlyStop <= lastStop {
			t.Fatalf("bad level %s: %+v", level, o)
		}
		lastSamples, lastStop = o.perSubnet, o.earlyStop
	}
	s.VerifyBeforeConnect = true
	if desktopConnectOptions(s).optimistic {
		t.Fatal("strict setting ignored")
	}
}

func TestProtocolSpecificRegistration(t *testing.T) {
	a := account{PrivateKey: "wg", PeerPublicKey: "peer"}
	if !accountReadyFor(a, protoWG) || !accountReadyFor(a, protoAWG) || accountReadyFor(a, protoMASQUE) || accountReadyFor(a, protoWWarp) {
		t.Fatal("base readiness")
	}
	a.Masque = &masqueAccount{PrivateKey: "m", PeerPublicKey: "mp", IPv4: "172.16.0.2"}
	for _, p := range []string{protoMASQUE, protoMASQUEH2, protoMASQUEMASQUE} {
		if !accountReadyFor(a, p) {
			t.Fatal(p)
		}
	}
	a.Outer = &account{PrivateKey: "outer", PeerPublicKey: "op"}
	if !accountReadyFor(a, protoWWarp) {
		t.Fatal("outer readiness")
	}
}

func TestWarmPreparationDoesNotStartCore(t *testing.T) {
	home := t.TempDir()
	t.Setenv("WARP_CLIENT_HOME", home)
	savedPriv, savedPub, savedOuter, savedMasque, savedChain := warpPrivateKey, warpPublicKey, outer, masqueAcct, chainUserProto
	defer func() {
		warpPrivateKey, warpPublicKey, outer, masqueAcct, chainUserProto = savedPriv, savedPub, savedOuter, savedMasque, savedChain
	}()
	path, _ := accountPath()
	if err := saveAccount(path, account{PrivateKey: savedPriv, PeerPublicKey: savedPub}); err != nil {
		t.Fatal(err)
	}
	s := defaultSettings()
	s.Proto = protoWG
	o := desktopConnectOptions(s)
	o.prepareOnly = true
	store, err := cache.Open(cache.Options{})
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := json.Marshal(cachedScan{Proto: protoWG, Intensity: "medium", At: time.Now(), Results: []cachedEndpoint{{Endpoint: "192.0.2.1:2408", Working: true}}})
	if err = store.Put(scanCacheKey(protoWG, false), raw); err != nil {
		t.Fatal(err)
	}
	// No actual core exists here. A network scan would also fail on this reserved endpoint.
	if err = runConnectCmd(context.Background(), o); err != nil {
		t.Fatal(err)
	}
}

type rejectRegistrationTransport struct{ t *testing.T }

func (r rejectRegistrationTransport) RoundTrip(*http.Request) (*http.Response, error) {
	r.t.Fatal("unnecessary account registration")
	return nil, nil
}
func TestExistingRegistrationIsNotRotated(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	a := account{PrivateKey: "existing", PeerPublicKey: "peer", ID: "id", Token: "token"}
	result, err := mintRequiredAccount(context.Background(), &http.Client{Transport: rejectRegistrationTransport{t}}, a, protoWG)
	if err != nil || result.PrivateKey != a.PrivateKey {
		t.Fatalf("existing account changed: %v", err)
	}
}

type registrationFixture func(*http.Request) (*http.Response, error)

func (f registrationFixture) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestRegistrationCheckpointsSurviveOptionalFailure(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	posts, patches := 0, 0
	failOuter := true
	client := &http.Client{Transport: registrationFixture(func(r *http.Request) (*http.Response, error) {
		status, body := http.StatusOK, `{}`
		switch r.Method {
		case http.MethodPost:
			posts++
			if posts == 2 && failOuter {
				status = http.StatusServiceUnavailable
			} else {
				body = `{"id":"fixture","token":"fixture","config":{"peers":[{"public_key":"peer"}],"interface":{"addresses":{"v4":"172.16.0.2"}}}}`
			}
		case http.MethodPatch:
			patches++
		default:
			t.Fatalf("unexpected request: %s", r.Method)
		}
		return &http.Response{StatusCode: status, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(body)), Request: r}, nil
	})}
	if _, err := mintRequiredAccount(context.Background(), client, account{}, protoWWarp); err == nil {
		t.Fatal("expected outer registration failure")
	}
	path, _ := accountPath()
	saved, err := loadAccount(path)
	if err != nil || saved.PrivateKey == "" || saved.Outer != nil || posts != 2 || patches != 1 {
		t.Fatalf("base checkpoint missing: posts=%d patches=%d err=%v", posts, patches, err)
	}
	failOuter = false
	a, err := mintRequiredAccount(context.Background(), client, account{}, protoWWarp)
	if err != nil || !accountReadyFor(a, protoWWarp) || a.PrivateKey != saved.PrivateKey || posts != 3 || patches != 2 {
		t.Fatalf("retry did not reuse checkpoint: posts=%d patches=%d err=%v", posts, patches, err)
	}
}

func TestCancelledPreparationPreservesAccount(t *testing.T) {
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	path, _ := accountPath()
	a := account{PrivateKey: "existing", PeerPublicKey: "peer"}
	if err := saveAccount(path, a); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := mintRequiredAccount(ctx, &http.Client{Transport: rejectRegistrationTransport{t}}, a, protoWWarp); err != context.Canceled {
		t.Fatalf("expected cancellation, got %v", err)
	}
	saved, err := loadAccount(path)
	if err != nil || saved.PrivateKey != a.PrivateKey || saved.Outer != nil {
		t.Fatal("cancellation modified credentials")
	}
}
