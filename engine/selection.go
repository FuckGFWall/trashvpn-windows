package main

import (
	"context"
	"fmt"
	"math/rand"
	"net/netip"
	"slices"
	"sort"
	"time"
)

// Desktop selection is latency bounded, unlike the explicit exhaustive CLI
// scan modes. Echo loss/RTT thresholds govern early stopping, not whether a
// demonstrably usable but slower endpoint can be retained as a fallback.
type selectionProfile struct {
	samples, limit, workers, goal, echoes int
	budget, candidate, goodRTT            time.Duration
	goodLoss                              float32
}

func selectionFor(level string) selectionProfile {
	switch level {
	case "low":
		return selectionProfile{samples: 1, limit: 48, workers: 6, goal: 2, echoes: 5, budget: 22 * time.Second, candidate: 11 * time.Second, goodRTT: 600 * time.Millisecond, goodLoss: .20}
	case "high":
		return selectionProfile{samples: 8, limit: 224, workers: 10, goal: 8, echoes: 10, budget: 58 * time.Second, candidate: 18 * time.Second, goodRTT: 350 * time.Millisecond, goodLoss: .10}
	default:
		return selectionProfile{samples: 3, limit: 112, workers: 8, goal: 4, echoes: 7, budget: 38 * time.Second, candidate: 15 * time.Second, goodRTT: 450 * time.Millisecond, goodLoss: .15}
	}
}

func desktopTargets(o options, run protoRun, sampled []netip.Addr, history []cachedEndpoint) []probeTarget {
	profile := selectionFor(o.scanIntensity)
	allowed := poolsFor(run, o.ipv6)
	if run.isMASQUE() && !run.isH2() && !o.ipv6 {
		// Keep the known H3 gateways first, but give the consumer /24s an
		// opportunity when those two IPs are unreachable on this network.
		allowed = masqueH2PoolsV4
	}
	if len(o.targets) > 0 {
		allowed = o.targets
	}
	allowedIP := func(ip netip.Addr) bool {
		for _, p := range allowed {
			if p.Contains(ip) {
				return true
			}
		}
		return false
	}
	ports := allWarpPorts()
	first := []int{2408, 854, 880, 500, 1701, 859, 4500, 864}
	if run.isMASQUE() {
		ports = masqueEndpointPorts
		first = []int{443, 4443, 8443, 8095, 500, 4500, 1701}
	}
	if o.port != 0 {
		ports = []int{o.port}
		first = ports
	}
	out := make([]probeTarget, 0, profile.limit)
	seen := make(map[probeTarget]bool)
	add := func(ip netip.Addr, p int) {
		t := probeTarget{ip: ip, port: p}
		if len(out) < profile.limit && !seen[t] && allowedIP(ip) && slices.Contains(ports, p) {
			out = append(out, t)
			seen[t] = true
		}
	}

	history = slices.Clone(history)
	sort.SliceStable(history, func(i, j int) bool { return betterEndpoint(history[i], history[j]) })
	var learnedPort int
	for _, h := range history {
		ap, err := netip.ParseAddrPort(h.Endpoint)
		if !h.Working || err != nil {
			continue
		}
		before := len(out)
		p := int(ap.Port())
		add(ap.Addr(), p)
		if before != len(out) && learnedPort == 0 {
			learnedPort = p
		}
		if len(out) >= profile.goal {
			break
		}
	}
	// History hints never count as verified. Favour known port numbers on
	// other IPs as well, without letting one obsolete port monopolize a wave.
	if learnedPort != 0 {
		first = append([]int{learnedPort}, first...)
	}
	if len(o.targets) == 0 && !o.ipv6 {
		seeds := []string{"162.159.192.1", "162.159.195.1"}
		if run.isMASQUE() {
			seeds = []string{"162.159.198.2", "162.159.198.1", "162.159.199.2"}
		}
		for i, s := range seeds {
			ip := netip.MustParseAddr(s)
			add(ip, first[i%len(first)])
			add(ip, first[(i+1)%len(first)])
		}
	}
	// setupScan samples each subnet in a contiguous block. Round-robin
	// across subnets before taking a second address from the first one.
	if run.isMASQUE() && !run.isH2() && !o.ipv6 && len(o.targets) == 0 {
		sampled = slices.Clone(sampled)
		for _, p := range allowed {
			sampled = append(sampled, expandV4(p, profile.samples)...)
		}
	}
	groups := make([][]netip.Addr, len(allowed))
	for _, ip := range sampled {
		for j, p := range allowed {
			if p.Contains(ip) {
				groups[j] = append(groups[j], ip)
				break
			}
		}
	}
	var ips []netip.Addr
	for round := 0; ; round++ {
		n := len(ips)
		for _, g := range groups {
			if round < len(g) {
				ips = append(ips, g[round])
			}
		}
		if n == len(ips) {
			break
		}
	}
	// Front-load port and subnet diversity. Follow with diagonal waves over
	// the entire protocol-specific port pool; no preliminary sweep or barrier.
	for i, ip := range ips[:min(len(ips), profile.workers*2)] {
		add(ip, first[i%len(first)])
	}
	waves := func(order []int, limit int) {
		for wave := range order {
			for i, ip := range ips {
				if len(out) >= limit {
					return
				}
				add(ip, order[(i+wave)%len(order)])
			}
		}
	}
	if !run.isMASQUE() && o.port == 0 {
		// A bounded low-tier scan must not always truncate the same suffix
		// of the 50 alternate ports. Give the tail a chance on every rerun.
		waves(ports, profile.limit/2)
		rand.Shuffle(len(ports), func(i, j int) { ports[i], ports[j] = ports[j], ports[i] })
	}
	waves(ports, profile.limit)
	return out
}

func verificationLabel(o options, run protoRun) string {
	if o.desktop {
		return fmt.Sprintf("Verifying endpoint pairs (proto=%s)", run.name)
	}
	return fmt.Sprintf("Phase 2: verifying tunnels (proto=%s)", run.name)
}

func runDesktopSelection(ctx context.Context, o options, run protoRun, ips []netip.Addr, timeout time.Duration, emit emitter) (phaseResult, error) {
	// Expired scans can supply hints only: same protocol, family and (for a
	// chain) exact outer hop. Normal cache reuse still enforces user TTL.
	policy := o
	policy.cacheNeverExpires = true
	history, _ := cachedScanFor(scanKeyFor(o), o.ipv6, policy)
	targets := desktopTargets(o, run, ips, history.Results)
	if o.through != "" {
		o.tunnelParallel = nestedTunnelJobs
	}
	emit(barBeginMsg{label: verificationLabel(o, run), total: len(targets)})
	results, err := verifyTargets(ctx, o, run, targets, emit, func() (tunnel, error) { return newTunnel(run) }, func(ctx context.Context, tn tunnel, t probeTarget) endpointResult {
		return verifyDesktopPair(ctx, tn, t, o, timeout)
	})
	if err != nil {
		return phaseResult{}, err
	}
	emit(doneMsg{})
	return phaseResult{run, results}, nil
}

// One worker exclusively owns its netstack throughout both stages. Different
// workers can qualify, fetch HTTPS, and sample loss at the same time.
func verifyDesktopPair(ctx context.Context, tn tunnel, t probeTarget, o options, timeout time.Duration) endpointResult {
	ep := netip.AddrPortFrom(t.ip, uint16(t.port)).String()
	r := endpointResult{ip: t.ip}
	// A transient MASQUE handshake loss warrants one more attempt. Do not
	// reconnect a successful handshake between DNS, HTTPS and echo sampling.
	ready := false
	for i := 0; i < min(2, max(1, tn.attempts())) && ctx.Err() == nil; i++ {
		probeStep(ctx, "握手")
		if tn.handshake(ctx, ep, timeout) {
			ready = true
			break
		}
	}
	if !ready || ctx.Err() != nil {
		return r
	}
	return measureDesktopSession(ctx, tn.stack(), t, ep, o.tunPingCount, timeout)
}

type selectionPlane interface {
	selectionMeta(context.Context, time.Duration) (metaResult, bool)
	tunnelPing(context.Context, int, time.Duration) (time.Duration, float32, bool)
}

func measureDesktopSession(ctx context.Context, stack selectionPlane, t probeTarget, ep string, echoes int, timeout time.Duration) endpointResult {
	r := endpointResult{ip: t.ip}
	stageTimeout := max(timeout, 3*time.Second)
	meta, ok := stack.selectionMeta(ctx, stageTimeout)
	if !ok {
		return r
	}
	probeStep(ctx, "稳定性")
	rtt, loss, torn := stack.tunnelPing(ctx, echoes, timeout)
	if ctx.Err() != nil {
		return r
	}
	if torn || rtt == 0 {
		// Some paths suppress ICMP but still carry real traffic. Only retain
		// one as a fallback after a second fresh TLS request on this tunnel.
		probeStep(ctx, "稳定性复测")
		if _, ok := stack.selectionMeta(ctx, stageTimeout); !ok {
			return r
		}
	}
	r.endpoint, r.exit, r.ok, r.durable, r.measured, r.tunPing, r.loss = ep, meta, true, true, true, rtt, loss
	return r
}
