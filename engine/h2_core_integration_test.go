package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/pem"
	"fmt"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/xhwarp/warp-client/internal/core"
)

// Unlike -t, this checks which transport the installed core actually dials.
// All outbound packets terminate on loopback; no system proxy or TUN is used.
func TestMihomoH2UsesTCP(t *testing.T) {
	bin := os.Getenv("WARP_TEST_MIHOMO")
	if bin == "" {
		t.Skip("set WARP_TEST_MIHOMO")
	}
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	old, oldOuter := masqueAcct, outer
	defer func() { masqueAcct, outer = old, oldOuter }()
	outer = nil
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	priv, _ := x509.MarshalECPrivateKey(key)
	pub, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	masqueAcct = &masqueAccount{PrivateKey: base64.StdEncoding.EncodeToString(priv), PeerPublicKey: string(pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: pub})), IPv4: "172.16.0.2"}
	tcp, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer tcp.Close()
	udp, err := net.ListenPacket("udp4", tcp.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer udp.Close()
	observed := make(chan string, 2)
	go func() {
		c, err := tcp.Accept()
		if err == nil {
			observed <- "TCP"
			c.Close()
		}
	}()
	go func() {
		b := make([]byte, 2048)
		_, _, err := udp.ReadFrom(b)
		if err == nil {
			observed <- "UDP"
		}
	}()
	o := defaultConnectOptions(protoMASQUEH2, false, false)
	o.mixedPort, o.apiPort = freePort(t), freePort(t)
	o.proxyMode = modeGlobal
	cfg, err := renderClientConfig(o, cachedEndpoint{Endpoint: tcp.Addr().String()}, mustParseProto(protoMASQUEH2))
	if err != nil {
		t.Fatal(err)
	}
	c, err := core.New(core.Config{BinaryPath: bin, WorkDir: t.TempDir(), MixedPort: o.mixedPort, APIPort: o.apiPort})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.WriteConfig(cfg); err != nil {
		t.Fatal(err)
	}
	if err := c.Start(); err != nil {
		t.Fatal(err)
	}
	defer c.Stop()
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	if err := c.WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	proxy, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", o.mixedPort))
	tr := &http.Transport{Proxy: http.ProxyURL(proxy)}
	defer tr.CloseIdleConnections()
	done := make(chan struct{})
	go func() {
		defer close(done)
		req, _ := http.NewRequestWithContext(ctx, "GET", "http://1.1.1.1/", nil)
		resp, _ := (&http.Client{Transport: tr}).Do(req)
		if resp != nil {
			resp.Body.Close()
		}
	}()
	select {
	case protocol := <-observed:
		if protocol != "TCP" {
			t.Errorf("network:h2 was accepted but core dialled %s (H3), not TCP", protocol)
		} else {
			t.Log("H2 selected TCP")
		}
	case <-ctx.Done():
		t.Error("no loopback transport observed")
	}
	cancel()
	<-done
}

// A synthetic CONNECT-IP endpoint catches request-lifetime bugs without Internet access.
func TestMihomoH2StreamSurvivesHandshake(t *testing.T) {
	bin := os.Getenv("WARP_TEST_MIHOMO")
	if bin == "" {
		t.Skip("set WARP_TEST_MIHOMO")
	}
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	old, oldOuter := masqueAcct, outer
	defer func() { masqueAcct, outer = old, oldOuter }()
	outer = nil
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	priv, _ := x509.MarshalECPrivateKey(key)
	pub, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	masqueAcct = &masqueAccount{PrivateKey: base64.StdEncoding.EncodeToString(priv), PeerPublicKey: string(pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: pub})), IPv4: "172.16.0.2"}
	certTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour)}
	cert, _ := x509.CreateCertificate(rand.Reader, certTemplate, certTemplate, &key.PublicKey, key)
	observed := make(chan string, 4)
	closed := make(chan struct{}, 4)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		observed <- r.Method + " " + r.Proto
		w.WriteHeader(200)
		w.(http.Flusher).Flush()
		io.Copy(io.Discard, r.Body)
		closed <- struct{}{}
	}))
	server.EnableHTTP2 = true
	server.TLS = &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{cert}, PrivateKey: key}}}
	server.StartTLS()
	defer server.Close()
	o := defaultConnectOptions(protoMASQUEH2, false, false)
	o.mixedPort, o.apiPort = freePort(t), freePort(t)
	o.proxyMode = modeGlobal
	cfg, err := renderClientConfig(o, cachedEndpoint{Endpoint: server.Listener.Addr().String()}, mustParseProto(protoMASQUEH2))
	if err != nil {
		t.Fatal(err)
	}
	c, err := core.New(core.Config{BinaryPath: bin, WorkDir: t.TempDir(), MixedPort: o.mixedPort, APIPort: o.apiPort})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.WriteConfig(cfg); err != nil {
		t.Fatal(err)
	}
	if err := c.Start(); err != nil {
		t.Fatal(err)
	}
	defer c.Stop()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := c.WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	proxy, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", o.mixedPort))
	tr := &http.Transport{Proxy: http.ProxyURL(proxy)}
	defer tr.CloseIdleConnections()
	go func() {
		req, _ := http.NewRequestWithContext(ctx, "GET", "http://1.1.1.1/", nil)
		resp, _ := (&http.Client{Transport: tr}).Do(req)
		if resp != nil {
			resp.Body.Close()
		}
	}()
	select {
	case protocol := <-observed:
		t.Log(protocol)
	case <-ctx.Done():
		t.Fatal("core never sent CONNECT-IP to local HTTP/2 server")
	}
	select {
	case <-closed:
		t.Fatal("H2 stream closed immediately or at handshake timeout")
	case <-time.After(6 * time.Second):
		t.Log("H2 stream survived handshake deadline")
	}
}

// Opt-in A/B probe: same account, endpoint and literal-IP HTTPS target.
// Independent random localhost ports, no system proxy/TUN/account registration.
func TestH2PairedDiagnostic(t *testing.T) {
	path, bin, endpoint := os.Getenv("WARP_TEST_ACCOUNT"), os.Getenv("WARP_TEST_MIHOMO"), os.Getenv("WARP_TEST_ENDPOINT")
	if path == "" || bin == "" || endpoint == "" {
		t.Skip("set WARP_TEST_ACCOUNT, WARP_TEST_MIHOMO, WARP_TEST_ENDPOINT")
	}
	a, err := loadAccount(path)
	if err != nil {
		t.Fatal(err)
	}
	if a.Masque == nil {
		t.Fatal("no MASQUE identity")
	}
	old, oldOuter := masqueAcct, outer
	defer func() { masqueAcct, outer = old, oldOuter }()
	masqueAcct = a.Masque
	outer = nil
	t.Setenv("WARP_CLIENT_HOME", t.TempDir())
	target := "https://1.1.1.1/cdn-cgi/trace"
	request := func(client *http.Client) {
		resp, err := client.Get(target)
		if err != nil {
			t.Logf("HTTPS failed: %v", err)
			return
		}
		defer resp.Body.Close()
		b, err := io.ReadAll(io.LimitReader(resp.Body, 4096))
		t.Logf("HTTPS status=%d trace=%t readError=%v", resp.StatusCode, strings.Contains(string(b), "warp="), err)
	}
	tn, err := newMasqueTunnel(true)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	if tn.handshake(ctx, endpoint, 5*time.Second) {
		t.Log("native H2 CONNECT accepted")
		tr := &http.Transport{DialContext: tn.stack().tnet.DialContext}
		request(&http.Client{Transport: tr, Timeout: 10 * time.Second})
		tr.CloseIdleConnections()
	} else {
		t.Log("native H2 handshake failed")
	}
	tn.Close()
	cancel()
	o := defaultConnectOptions(protoMASQUEH2, false, false)
	o.mixedPort, o.apiPort = freePort(t), freePort(t)
	o.proxyMode = modeGlobal
	cfg, err := renderClientConfig(o, cachedEndpoint{Endpoint: endpoint}, mustParseProto(protoMASQUEH2))
	if err != nil {
		t.Fatal(err)
	}
	cfg = []byte(strings.Replace(string(cfg), "log-level: info", "log-level: debug", 1))
	dir := t.TempDir()
	c, err := core.New(core.Config{BinaryPath: bin, WorkDir: dir, MixedPort: o.mixedPort, APIPort: o.apiPort})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.WriteConfig(cfg); err != nil {
		t.Fatal(err)
	}
	if err := c.Start(); err != nil {
		t.Fatal(err)
	}
	defer c.Stop()
	ctx, cancel = context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	if err := c.WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	proxy, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", o.mixedPort))
	tr := &http.Transport{Proxy: http.ProxyURL(proxy)}
	t.Log("mihomo H2 request")
	request(&http.Client{Transport: tr, Timeout: 12 * time.Second})
	tr.CloseIdleConnections()
	c.Stop()
	log, _ := os.ReadFile(filepath.Join(dir, "core.log"))
	for _, line := range strings.Split(string(log), "\n") {
		if strings.Contains(line, "level=debug") || strings.Contains(line, "level=warning") {
			t.Log(line)
		}
	}
}
