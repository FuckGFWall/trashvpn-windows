package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/xhwarp/warp-client/internal/core"
)

type desktopSessionOps struct {
	verify     func(context.Context, func(clientState) error) (clientState, error)
	capture    func(*clientState) error
	persist    func(clientState) error
	restore    func(clientState) error
	connection func(string)
}

func mergeDesktopProbe(state *clientState, probe clientState) {
	state.ExitIP = probe.ExitIP
	state.MeasuredBps = probe.MeasuredBps
	if probe.Country != "" {
		state.Country = probe.Country
	}
	if probe.Colo != "" {
		state.Colo = probe.Colo
	}
	if probe.NodeCity != "" {
		state.NodeCity = probe.NodeCity
	}
}

// Optimistic mode is optimistic only about the remaining payload checks, not
// about an open controller port: capture and its recovery snapshot are committed
// after real HTTPS succeeds, before telling the UI that traffic is connected.
func verifyDesktopSession(ctx context.Context, state clientState, optimistic bool, ops desktopSessionOps) (result clientState, err error) {
	if err = ctx.Err(); err != nil {
		return clientState{}, err
	}
	captured := false
	defer func() {
		if err == nil || !captured {
			return
		}
		state.Verification = "failed"
		if restoreErr := ops.restore(state); restoreErr != nil {
			err = errors.Join(err, fmt.Errorf("恢复系统代理失败，需重试断开: %w", restoreErr))
		}
		// Retain PID + original snapshot until the caller actually stops the
		// core. A failed cleanup must remain recoverable on disconnect / exit.
		if persistErr := ops.persist(state); persistErr != nil {
			err = errors.Join(err, persistErr)
		}
	}()
	usable := func(probe clientState) error {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if !optimistic || captured {
			return nil
		}
		mergeDesktopProbe(&state, probe)
		captured = true
		if e := ops.capture(&state); e != nil {
			return e
		}
		state.Verification = "checking"
		if e := ops.persist(state); e != nil {
			return e
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if state.ProxyWarn == "" && ops.connection != nil {
			ops.connection("provisional")
		}
		return nil
	}
	probe, err := ops.verify(ctx, usable)
	if err != nil {
		return clientState{}, err
	}
	if ctx.Err() != nil {
		return clientState{}, ctx.Err()
	}
	mergeDesktopProbe(&state, probe)
	if !captured {
		captured = true
		if err = ops.capture(&state); err != nil {
			return clientState{}, err
		}
	}
	state.Verification = "verified"
	if err = ops.persist(state); err != nil {
		return clientState{}, err
	}
	return state, nil
}

func desktopSessionState(opts options, ce cachedEndpoint, run protoRun, pid int) clientState {
	proto := run.name
	if chainUserProto != "" {
		proto = chainUserProto
	} else if opts.through != "" {
		proto = protoWWarp
	}
	return clientState{Proto: proto, Endpoint: ce.Endpoint, Country: ce.Country, Colo: ce.Colo, NodeCity: ce.NodeCity,
		LatencyMS: ce.EpPingMS, TunLatMS: ce.TunPingMS, Connected: true, Verification: "checking", MixedPort: opts.mixedPort,
		APIPort: opts.apiPort, CorePID: pid, TUN: opts.tunMode, AllowLan: opts.allowLan, Family: opts.ipFamily(), NoProxy: opts.noProxy,
		StartedAt: time.Now().Format(time.RFC3339)}
}

func captureDesktopProxy(opts options, label string, state *clientState) error {
	if opts.tunMode || opts.noProxy {
		return nil
	}
	stageMsg(fmt.Sprintf("[%s] HTTPS 已连通，设置系统代理…", label))
	snap, err := core.SetSystemProxy(opts.mixedPort)
	// Preserve the snapshot even if a write partially failed.
	if snap != nil {
		var encodeErr error
		state.ProxySnap, encodeErr = json.Marshal(snap)
		if encodeErr != nil {
			return encodeErr
		}
	}
	if err != nil && proxyHelperAllowed() {
		if e := core.SetSystemProxyElevated(opts.mixedPort); e == nil {
			err = nil
		}
	}
	if err != nil {
		state.ProxyWarn = err.Error()
		stageMsg(fmt.Sprintf("[%s] 系统代理设置失败，请手动指向 127.0.0.1:%d：%v", label, opts.mixedPort, err))
	}
	return nil
}

func restoreDesktopProxy(state clientState) error {
	if state.TUN || len(state.ProxySnap) == 0 {
		return nil
	}
	var snap core.ProxySnapshot
	if err := json.Unmarshal(state.ProxySnap, &snap); err != nil {
		return err
	}
	err := core.UnsetSystemProxy(&snap)
	if err != nil && proxyHelperAllowed() {
		if e := core.RestoreSystemProxyElevated(&snap); e == nil {
			err = nil
		}
	}
	return err
}
