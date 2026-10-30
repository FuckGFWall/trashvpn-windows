package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// A controller ACK is not connectivity. Require a real HTTPS round trip via
// the selected outbound, with hostname resolution inside the mixed proxy.
func verifyDesktopMode(ctx context.Context, port int, family string) (clientState, error) {
	proxy, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", port))
	transport := &http.Transport{Proxy: http.ProxyURL(proxy)}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 6 * time.Second}
	urls := []string{"https://www.cloudflare.com/cdn-cgi/trace", "https://www.gstatic.com/generate_204"}
	if family == famV6 {
		urls = []string{"https://[2606:4700:4700::1111]/cdn-cgi/trace"}
	}
	probe, err := verifyDesktopHTTPS(ctx, client, urls)
	if err == nil && family == famBoth {
		_, err = verifyDesktopHTTPS(ctx, client, []string{"https://[2606:4700:4700::1111]/cdn-cgi/trace"})
	}
	return probe, err
}
func verifyDesktopHTTPS(ctx context.Context, client *http.Client, urls []string) (clientState, error) {
	var last error
	for _, target := range urls {
		if ctx.Err() != nil {
			return clientState{}, ctx.Err()
		}
		fmt.Println("模式热切换：正在检查 HTTPS / DNS 真实数据面…")
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
		if err != nil {
			return clientState{}, err
		}
		resp, err := client.Do(req)
		if err != nil {
			last = err
			continue
		}
		body, readErr := io.ReadAll(io.LimitReader(resp.Body, 65536))
		resp.Body.Close()
		if readErr != nil {
			last = readErr
			continue
		}
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			last = fmt.Errorf("HTTPS HTTP %d", resp.StatusCode)
			continue
		}
		probe := clientState{}
		for _, line := range strings.Split(string(body), "\n") {
			if value, ok := strings.CutPrefix(line, "ip="); ok {
				probe.ExitIP = strings.TrimSpace(value)
			}
		}
		return probe, nil
	}
	return clientState{}, fmt.Errorf("HTTPS 数据面不可用: %w", last)
}
