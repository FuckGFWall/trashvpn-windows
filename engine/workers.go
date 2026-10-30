package main

import (
	"context"
	"fmt"
	"io"
	"os"
	"sync"
)

func closeOnCancel(ctx context.Context, c io.Closer) func() {
	var once sync.Once
	close := func() { once.Do(func() { _ = c.Close() }) }
	stop := context.AfterFunc(ctx, close)
	return func() { stop(); close() }
}

func runPool(workers, n int, fn func(i int)) {
	if workers < 1 {
		workers = 1
	}
	sem := make(chan struct{}, workers)
	var wg sync.WaitGroup
	for i := 0; i < n; i++ {
		sem <- struct{}{}
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			defer func() { <-sem }()
			fn(i)
		}(i)
	}
	wg.Wait()
}

func runTunnelPool(ctx context.Context, workers int, run protoRun, n int, fn func(tn tunnel, i int)) error {
	return runTunnelWorkers(ctx, workers, n, func() (tunnel, error) { return newTunnel(run) }, fn)
}

// Bound both dispatch and active work by the scan context. Every worker is
// joined before its tunnel is closed; no producer or cancellation goroutine is
// left behind. fn must propagate ctx into all blocking network operations.
func runTunnelWorkers(ctx context.Context, workers, n int, create func() (tunnel, error), fn func(tunnel, int)) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if n == 0 {
		return nil
	}
	if workers < 1 {
		workers = 1
	}
	if workers > n {
		workers = n
	}

	var tunnels []tunnel
	for len(tunnels) < workers {
		if ctx.Err() != nil {
			break
		}
		tn, err := create()
		if err != nil {
			fmt.Fprintf(os.Stderr, "tunnel setup failed: %v\n", err)
			break
		}
		tunnels = append(tunnels, tn)
	}
	if len(tunnels) == 0 {
		if err := ctx.Err(); err != nil {
			return err
		}
		return fmt.Errorf("no tunnels could be created")
	}
	defer func() {
		for _, tn := range tunnels {
			tn.Close()
		}
	}()

	jobs := make(chan int)
	var wg sync.WaitGroup
	for _, tn := range tunnels {
		wg.Add(1)
		go func(tn tunnel) {
			defer wg.Done()
			for {
				select {
				case <-ctx.Done():
					return
				case i, ok := <-jobs:
					if !ok || ctx.Err() != nil {
						return
					}
					fn(tn, i)
				}
			}
		}(tn)
	}
dispatch:
	for i := 0; i < n; i++ {
		if ctx.Err() != nil {
			break
		}
		select {
		case <-ctx.Done():
			break dispatch
		case jobs <- i:
		}
	}
	close(jobs)
	wg.Wait()
	return ctx.Err()
}
