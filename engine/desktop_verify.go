package main

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// A controller ACK is not connectivity. Require a real HTTPS round trip via
// the selected outbound, with hostname resolution inside the mixed proxy.
func verifyDesktopMode(ctx context.Context, port int, family string) (clientState, error) {
	proxy, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", port))
	transport := &http.Transport{Proxy: http.ProxyURL(proxy)}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 6 * time.Second}
	urls := []string{"https://www.cloudflare.com/cdn-cgi/trace", "https://api.ipify.org"}
	v6URLs := []string{"https://[2606:4700:4700::1111]/cdn-cgi/trace"}
	return verifyDesktopFamily(ctx, client, family, urls, v6URLs)
}

func verifyDesktopFamily(ctx context.Context, client *http.Client, family string, urls, v6URLs []string) (clientState, error) {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	if family == famV6 {
		return verifyDesktopIPv6(ctx, client, v6URLs)
	}
	// Dual-stack checks are independent: do not pay their timeouts in series.
	type outcome struct {
		probe clientState
		err   error
	}
	v6 := make(chan outcome, 1)
	var workers sync.WaitGroup
	if family == famBoth {
		workers.Add(1)
		go func() { defer workers.Done(); p, e := verifyDesktopIPv6(ctx, client, v6URLs); v6 <- outcome{p, e} }()
	}
	defer workers.Wait()
	defer cancel()
	probe, err := verifyDesktopHTTPS(ctx, client, urls)
	if err == nil && family == famBoth {
		err = (<-v6).err
	}
	return probe, err
}

func verifyDesktopIPv6(ctx context.Context, client *http.Client, urls []string) (clientState, error) {
	probe, err := verifyDesktopHTTPS(ctx, client, urls)
	if err == nil && (net.ParseIP(probe.ExitIP) == nil || !strings.Contains(probe.ExitIP, ":")) {
		err = fmt.Errorf("IPv6 验证未返回有效出口地址")
	}
	return probe, err
}
func verifyDesktopHTTPS(ctx context.Context, client *http.Client, urls []string) (clientState, error) {
	if len(urls) == 0 {
		return clientState{}, fmt.Errorf("没有 HTTPS 验证地址")
	}
	parent := ctx
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	type outcome struct {
		probe clientState
		err   error
	}
	results := make(chan outcome, len(urls))
	jobs := make(chan string, len(urls))
	for _, target := range urls {
		jobs <- target
	}
	close(jobs)
	var workers sync.WaitGroup
	for i := 0; i < min(2, len(urls)); i++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			for target := range jobs {
				if ctx.Err() != nil {
					return
				}
				p, e := verifyDesktopURL(ctx, client, target)
				results <- outcome{p, e}
			}
		}()
	}
	// A slow metadata provider must not hold a working tunnel hostage. Still
	// require a completed, trusted HTTPS request; cancel and join all losers.
	defer workers.Wait()
	defer cancel()
	var last error
	for range urls {
		select {
		case result := <-results:
			if parent.Err() != nil {
				return clientState{}, parent.Err()
			}
			if result.err == nil {
				return result.probe, nil
			}
			last = result.err
		case <-parent.Done():
			return clientState{}, parent.Err()
		}
	}
	return clientState{}, fmt.Errorf("HTTPS 数据面不可用: %w", last)
}
func verifyDesktopURL(ctx context.Context, client *http.Client, target string) (clientState, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return clientState{}, err
	}
	if req.URL.Scheme != "https" {
		return clientState{}, fmt.Errorf("数据面验证必须使用 HTTPS")
	}
	resp, err := client.Do(req)
	if err != nil {
		return clientState{}, err
	}
	body, readErr := io.ReadAll(io.LimitReader(resp.Body, 65537))
	resp.Body.Close()
	if readErr != nil {
		return clientState{}, readErr
	}
	if len(body) > 65536 || resp.Request != nil && resp.Request.URL.Scheme != "https" {
		return clientState{}, fmt.Errorf("HTTPS 验证响应过大或降级为明文")
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return clientState{}, fmt.Errorf("HTTPS HTTP %d", resp.StatusCode)
	}
	probe := clientState{}
	for _, line := range strings.Split(string(body), "\n") {
		if value, ok := strings.CutPrefix(line, "ip="); ok {
			probe.ExitIP = strings.TrimSpace(value)
		}
	}
	if net.ParseIP(strings.TrimSpace(string(body))) != nil {
		probe.ExitIP = strings.TrimSpace(string(body))
	}
	probe.Country = traceField(string(body), "loc")
	probe.Colo = traceField(string(body), "colo")
	return probe, nil
}

type desktopVerifyOps struct {
	basic      func(context.Context) (clientState, error)
	payload    func(context.Context) (int64, error)
	retryDelay time.Duration
}

func verifyDesktopConnection(ctx context.Context, opts options, logf func(string), usable func(clientState) error) (clientState, error) {
	bytes, timeout := verifyPayloadBytes, verifyPayloadTimeout
	if opts.openvpnMode {
		bytes, timeout = openvpnSpeedProbeBytes, openvpnSpeedProbeTimeout
	}
	return verifyDesktopSteps(ctx, opts.ipFamily(), logf, usable, desktopVerifyOps{
		basic: func(ctx context.Context) (clientState, error) {
			return verifyDesktopMode(ctx, opts.mixedPort, opts.ipFamily())
		},
		payload: func(ctx context.Context) (int64, error) {
			return probeThroughputContext(ctx, opts.mixedPort, bytes, timeout)
		},
		retryDelay: verifyRetryDelay,
	})
}

func verifyDesktopSteps(ctx context.Context, family string, logf func(string), usable func(clientState) error, ops desktopVerifyOps) (clientState, error) {
	logf("数据面验证：经隧道检查真实 HTTPS…")
	probe, err := ops.basic(ctx)
	if err != nil && ctx.Err() == nil {
		timer := time.NewTimer(ops.retryDelay)
		defer timer.Stop()
		select {
		case <-ctx.Done():
			return clientState{}, ctx.Err()
		case <-timer.C:
		}
		logf("首次探测未通过，重试…")
		probe, err = ops.basic(ctx)
	}
	if ctx.Err() != nil {
		return clientState{}, ctx.Err()
	}
	if err != nil {
		return clientState{}, fmt.Errorf("HTTPS 数据面验证失败: %w", err)
	}
	if family == famV6 && (net.ParseIP(probe.ExitIP) == nil || !strings.Contains(probe.ExitIP, ":")) {
		return clientState{}, fmt.Errorf("IPv6 验证未返回有效出口地址")
	}
	logf("HTTPS 数据面验证成功")
	if family != famV6 {
		logf("DNS 数据面已由 HTTPS 域名请求确认，无需串行等待额外 DoH 服务")
	} else {
		// Preserve the existing IPv6-only policy without pretending a literal
		// address request proves hostname DNS resolution.
		logf("IPv6 字面量 HTTPS 已验证；此项不单独证明域名 DNS")
	}
	if usable != nil {
		if err = usable(probe); err != nil {
			return clientState{}, err
		}
	}
	if family != famV6 {
		logf("数据面验证：吞吐（下载载荷）…")
		probe.MeasuredBps, err = ops.payload(ctx)
		if err != nil {
			return clientState{}, fmt.Errorf("隧道无法完成载荷传输: %w", err)
		}
		logf("吞吐验证成功")
	}
	if ctx.Err() != nil {
		return clientState{}, ctx.Err()
	}
	return probe, nil
}
