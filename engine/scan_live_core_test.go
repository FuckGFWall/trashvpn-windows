//go:build livewarp

package main

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/core"
)

// Validate the actual desktop renderer and core separately from the selection
// netstack. Never use desktop-connect: it can change the user's proxy.
func testSelectedCore(t *testing.T, bin string, o options, run protoRun, ce cachedEndpoint) {
	t.Helper()
	reserve := func() net.Listener {
		l, err := net.Listen("tcp4", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		return l
	}
	l1 := reserve()
	defer l1.Close()
	l2 := reserve()
	defer l2.Close()
	o.mixedPort = l1.Addr().(*net.TCPAddr).Port
	o.apiPort = l2.Addr().(*net.TCPAddr).Port
	o.tunMode, o.allowLan, o.noProxy, o.proxyMode = false, false, true, modeGlobal
	config, err := renderClientConfig(o, ce, run)
	if err != nil {
		t.Fatal("cannot render test config")
	}
	c, err := core.New(core.Config{BinaryPath: bin, WorkDir: t.TempDir(), MixedPort: o.mixedPort, APIPort: o.apiPort, LogWriter: io.Discard})
	if err != nil {
		t.Fatal(err)
	}
	if err = c.WriteConfig(config); err != nil {
		t.Fatal(err)
	}
	l1.Close()
	l2.Close()
	if err = c.Start(); err != nil {
		t.Fatal(err)
	}
	defer c.Stop()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err = c.WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	tr := &http.Transport{Proxy: http.ProxyURL(mustParseURL(fmt.Sprintf("http://127.0.0.1:%d", o.mixedPort))), DisableKeepAlives: true}
	defer tr.CloseIdleConnections()
	client := &http.Client{Transport: tr, Timeout: 6 * time.Second}
	// Seven independent TLS requests, 10 seconds apart, through the same
	// core. Only count a Cloudflare trace explicitly reporting WARP.
	start := time.Now()
	ok := 0
	for i := range 7 {
		time.Sleep(time.Until(start.Add(time.Duration(i) * 10 * time.Second)))
		resp, err := client.Get("https://1.1.1.1/cdn-cgi/trace")
		if err != nil {
			continue
		}
		raw, err := io.ReadAll(io.LimitReader(resp.Body, 4096))
		resp.Body.Close()
		warp := traceField(string(raw), "warp")
		if err == nil && resp.StatusCode == 200 && (warp == "on" || warp == "plus") {
			ok++
		}
	}
	t.Logf("CORE elapsed=%.1fs trace_WARP=%d/7", time.Since(start).Seconds(), ok)
}
