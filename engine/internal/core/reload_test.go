package core

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestReloadConfigLoopback(t *testing.T) {
	for _, tc := range []struct {
		name     string
		status   int
		readback string
		tun      bool
		ok       bool
	}{
		{"enable", 204, `{"tun":{"enable":true}}`, true, true},
		{"disable", 204, `{"tun":{"enable":false}}`, false, true},
		{"rejected", 400, `{}`, false, false},
		{"silently-not-enabled", 204, `{"tun":{"enable":false}}`, true, false},
		{"bad-json", 204, `broken`, false, false},
		{"missing-tun", 204, `{}`, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			puts, gets := 0, 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path != "/configs" {
					t.Error("unexpected endpoint")
					w.WriteHeader(404)
					return
				}
				if r.Method == http.MethodPut {
					puts++
					var body map[string]string
					if json.NewDecoder(r.Body).Decode(&body) != nil || body["payload"] != "opaque-config" || r.URL.Query().Get("force") != "true" {
						t.Error("reload wire contract mismatch")
					}
					w.WriteHeader(tc.status)
				} else {
					gets++
					fmt.Fprint(w, tc.readback)
				}
			}))
			defer server.Close()
			port := server.Listener.Addr().(*net.TCPAddr).Port
			err := ReloadConfig(context.Background(), port, []byte("opaque-config"), tc.tun)
			if (err == nil) != tc.ok || puts != 1 || (tc.status == 204 && gets != 1) {
				t.Fatalf("bad reload result %v put=%d get=%d", err, puts, gets)
			}
		})
	}
}

func TestReloadConfigCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	err := ReloadConfig(ctx, 1, []byte("fixture"), false)
	if err == nil || !strings.Contains(err.Error(), "canceled") {
		t.Fatalf("cancellation not propagated: %v", err)
	}
}
