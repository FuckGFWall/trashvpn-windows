package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"sync"
	"time"
)

var desktopEvents bool
var desktopEventMu sync.Mutex
var registrationProtocol string

func desktopProgress(phase, text string, done, total int) {
	desktopEvent(map[string]any{"type": "progress", "phase": phase, "text": text, "done": done, "total": total})
}
func desktopConnection(state string) {
	desktopEvent(map[string]any{"type": "connection", "state": state})
}
func desktopEvent(e any) {
	if !desktopEvents {
		return
	}
	raw, _ := json.Marshal(e)
	desktopEventMu.Lock()
	defer desktopEventMu.Unlock()
	fmt.Println("@@XEHN " + string(raw))
}

// Same ranking, different finite sampling budgets. Stop once enough durable
// candidates exist instead of sweeping the entire pool for a single connection.
func applyScanIntensity(o *options, level string) {
	switch level {
	case "low":
		o.perSubnet, o.earlyStop = 1, 2
		o.portSamples = 4
		o.scanBudget, o.candidateBudget = 25*time.Second, 10*time.Second
	case "high":
		o.perSubnet, o.earlyStop = 8, 8
		o.portSamples = 12
		o.scanBudget, o.candidateBudget = 75*time.Second, 15*time.Second
	default:
		level = "medium"
		o.perSubnet, o.earlyStop = 3, 4
		o.portSamples = 8
		o.scanBudget, o.candidateBudget = 45*time.Second, 12*time.Second
	}
	o.scanIntensity = level
}

func accountReadyFor(a account, proto string) bool {
	if a.PrivateKey == "" || a.PeerPublicKey == "" {
		return false
	}
	switch proto {
	case protoMASQUE, protoMASQUEH2, protoMASQUEMASQUE:
		return a.Masque != nil && a.Masque.PrivateKey != "" && a.Masque.PeerPublicKey != "" && a.Masque.IPv4 != ""
	case protoWWarp:
		return a.Outer != nil && a.Outer.PrivateKey != "" && a.Outer.PeerPublicKey != ""
	}
	return true
}

// Keep completed credentials even when a later enrolment fails. Do not rotate
// a working account merely because a different protocol needs another identity.
func mintRequiredAccount(ctx context.Context, client *http.Client, a account, proto string) (account, error) {
	if path, err := accountPath(); err == nil && a.PrivateKey == "" {
		if saved, err := loadAccount(path); err == nil {
			a = saved
		}
	}
	checkpoint := func() error {
		if err := ctx.Err(); err != nil {
			return err
		}
		path, err := accountPath()
		if err != nil {
			return err
		}
		return saveAccount(path, a)
	}
	if a.PrivateKey == "" || a.PeerPublicKey == "" {
		desktopProgress("register", "注册基础账号", 0, 0)
		var err error
		a, err = registerWARP(ctx, client)
		if err != nil {
			return a, err
		}
		if err = checkpoint(); err != nil {
			return a, err
		}
	}
	if ctx.Err() != nil {
		return a, ctx.Err()
	}
	switch proto {
	case protoMASQUE, protoMASQUEH2, protoMASQUEMASQUE:
		if !accountReadyFor(a, proto) {
			desktopProgress("register", "基础账号就绪，注册 MASQUE", 1, 2)
			m, err := registerMasque(ctx, client)
			if err != nil {
				return a, err
			}
			a.Masque = m
		}
	case protoWWarp:
		if !accountReadyFor(a, proto) {
			desktopProgress("register", "基础账号就绪，注册外层账号", 1, 2)
			o, err := registerWARP(ctx, client)
			if err != nil {
				return a, err
			}
			a.Outer = &o
		}
	}
	if err := checkpoint(); err != nil {
		return a, err
	}
	desktopProgress("register", "注册完成", 1, 1)
	return a, nil
}

type postConnectVerificationError struct{ err error }

func (e *postConnectVerificationError) Error() string {
	return "后台可用性检测失败，已断开: " + e.err.Error()
}
func (e *postConnectVerificationError) Unwrap() error { return e.err }
