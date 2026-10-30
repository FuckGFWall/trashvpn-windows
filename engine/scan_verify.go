package main

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"
)

// Counters describe settled outcomes, not merely dequeued jobs. In-flight and
// undispatched jobs canceled after the target is met are skipped, not failures.
type scanProgressMsg struct {
	stage, detail                                string
	completed, succeeded, skipped, total, target int
	finished                                     bool
}

type probeStepKey struct{}

func probeStep(ctx context.Context, step string) {
	if ctx.Err() != nil {
		return
	}
	if report, ok := ctx.Value(probeStepKey{}).(func(string)); ok {
		report(step)
	}
}

type candidateProbe func(context.Context, tunnel, probeTarget) endpointResult

func scanCandidate(ctx context.Context, tn tunnel, target probeTarget, opts options, timeout time.Duration) endpointResult {
	r := endpointResult{ip: target.ip}
	t, endpoint, ok, rtt, loss, torn := tunnelProbe(ctx, tn, target, timeout, opts.tunPingCount, opts.wantMeta)
	if !ok || ctx.Err() != nil {
		return r
	}
	r.exit, r.endpoint, r.ok, r.durable = t, endpoint, true, true
	if opts.tunPingCount > 0 {
		r.tunPing, r.loss, r.measured, r.durable = rtt, loss, true, !torn
	}
	probeStep(ctx, "延迟测量")
	if outer == nil {
		if hrtt, ok := pingHost(ctx, target.ip, timeout); ok {
			r.epPing = hrtt
		}
	} else if hrtt, ok := outer.pingTo(ctx, target.ip, timeout); ok {
		r.epPing = hrtt
	}
	return r
}

// This context belongs only to Phase 2. Meeting the target cancels every active
// network wait and queued job, but leaves the caller's connect context alive.
func verifyTargets(parent context.Context, opts options, run protoRun, targets []probeTarget, emit emitter,
	create func() (tunnel, error), probe candidateProbe) ([]endpointResult, error) {
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	results := make([]endpointResult, len(targets))
	var mu sync.Mutex
	completed, succeeded := 0, 0
	active := make(map[int]string)
	report := func(finished bool) {
		counts := make(map[string]int)
		for _, step := range active {
			counts[step]++
		}
		var steps []string
		for _, step := range []string{"握手", "隧道 DNS", "出口 HTTPS", "稳定性", "稳定性复测", "延迟测量"} {
			if n := counts[step]; n > 0 {
				steps = append(steps, fmt.Sprintf("%s %d", step, n))
			}
		}
		skipped := 0
		if finished {
			skipped = len(targets) - completed
		}
		emit(scanProgressMsg{stage: "verify", detail: strings.Join(steps, " / "), completed: completed,
			succeeded: succeeded, skipped: skipped, total: len(targets), target: opts.earlyStop, finished: finished})
	}
	mu.Lock()
	report(false)
	mu.Unlock()
	work := func(tn tunnel, i int) {
		workerCtx := context.WithValue(ctx, probeStepKey{}, func(step string) {
			mu.Lock()
			defer mu.Unlock()
			if ctx.Err() == nil {
				active[i] = step
				report(false)
			}
		})
		r := probe(workerCtx, tn, targets[i])
		mu.Lock()
		defer mu.Unlock()
		delete(active, i)
		if ctx.Err() != nil {
			return
		}
		completed++
		results[i] = r
		usable := r.working()
		if usable && filtered(opts) {
			usable = len(applyFilters(phaseResult{run: run, results: []endpointResult{r}}, opts).results) > 0
		}
		if usable {
			succeeded++
		}
		if r.ok {
			found := foundMsg{endpoint: r.endpoint, epPing: r.epPing, tunPing: r.tunPing, loss: r.loss, measured: r.measured, torn: !r.durable}
			if opts.wantMeta {
				found.exit, found.colo = exitRegion(r.exit), exitColo(r.exit)
			}
			emit(found)
		}
		// The lock makes acceptance and cancellation atomic: racing successes
		// cannot exceed the requested target or be mistaken for skipped jobs.
		if opts.earlyStop > 0 && succeeded >= opts.earlyStop {
			cancel()
		}
		emit(probedMsg{})
		report(false)
	}
	err := runTunnelWorkers(ctx, opts.tunnelParallel, len(targets), create, work)
	mu.Lock()
	report(true)
	mu.Unlock()
	emit(barEndMsg{label: fmt.Sprintf("Phase 2: verifying tunnels (proto=%s)", run.name),
		summary: fmt.Sprintf("completed %d/%d, usable %d, skipped %d", completed, len(targets), succeeded, len(targets)-completed)})
	if parent.Err() != nil {
		return nil, parent.Err()
	}
	if err != nil && !(ctx.Err() != nil && opts.earlyStop > 0 && succeeded >= opts.earlyStop) {
		return nil, err
	}
	return results, nil
}
