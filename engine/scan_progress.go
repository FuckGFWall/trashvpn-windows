package main

import (
	"fmt"
	"math"
	"sync"
)

var desktopScan *scanProgressTracker

// One tracker per desktop operation, not one progress bar per phase. Nested
// scans reserve separate outer/inner ranges so cache hits cannot show 100%
// before the inner scan begins. Retries retain the last visible high-water mark.
type scanProgressTracker struct {
	mu                  sync.Mutex
	chain               bool
	segment             int
	start, end, percent float64
	label, text         string
	last                scanProgressMsg
	send                func(map[string]any)
}

func (p *scanProgressTracker) publish(fraction float64, text string) {
	p.percent = math.Max(p.percent, p.start+(p.end-p.start)*fraction)
	p.text = text
	p.send(map[string]any{"type": "progress", "phase": "scan", "text": text,
		"done": p.percent, "total": 100, "stage": p.last.stage,
		"completed": p.last.completed, "succeeded": p.last.succeeded, "skipped": p.last.skipped,
		"candidates": p.last.total, "target": p.last.target})
}

func (p *scanProgressTracker) begin(proto string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.segment++
	p.start, p.end = p.percent, 99
	p.label = ""
	if p.chain {
		if p.segment == 1 {
			p.end, p.label = 45, "外层 · "
		} else {
			p.label = "内层 · "
		}
	}
	p.label += protoLabel(proto) + " · "
	p.last = scanProgressMsg{stage: "cache-check"}
	p.publish(0, p.label+"检查节点缓存")
}

func (p *scanProgressTracker) update(m scanProgressMsg) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.last = m
	ratio := 0.0
	if m.total > 0 {
		ratio = float64(m.completed) / float64(m.total)
	}
	if m.finished {
		ratio = 1
	}
	var fraction float64
	var text string
	switch m.stage {
	case "ports-primary":
		fraction = ratio * .15
		text = fmt.Sprintf("探测常用端口 · 完成 %d/%d 地址 · 可用 %d 端口", m.completed, m.total, m.succeeded)
	case "ports-alternate":
		fraction = .15 + ratio*.15
		text = fmt.Sprintf("补充探测备用端口 · 完成 %d/%d 地址 · 可用 %d 端口", m.completed, m.total, m.succeeded)
	case "verify":
		fraction = .30 + ratio*.65
		text = fmt.Sprintf("验证隧道 · 完成 %d/%d · 可用 %d", m.completed, m.total, m.succeeded)
		if m.target > 0 {
			text += fmt.Sprintf("/%d", m.target)
		}
		text += fmt.Sprintf(" · 跳过 %d", m.skipped)
		if m.detail != "" {
			text += "\n" + m.detail
		}
	default:
		return
	}
	p.publish(fraction, p.label+text)
}

func (p *scanProgressTracker) note(stage, text string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	fraction := 0.0
	if stage == "rank" {
		fraction = .96
	}
	if stage == "cache-write" {
		fraction = .99
	}
	p.last.stage = stage
	p.publish(fraction, p.label+text)
}

func (p *scanProgressTracker) ready(available int, cached bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	text := fmt.Sprintf("优选就绪 · 可用 %d", available)
	if cached {
		p.last = scanProgressMsg{stage: "cached", succeeded: available}
		text = fmt.Sprintf("缓存就绪 · 可用 %d", available)
	} else {
		text += fmt.Sprintf(" · 已验证 %d/%d · 跳过 %d", p.last.completed, p.last.total, p.last.skipped)
	}
	p.publish(1, p.label+text)
}

func (p *scanProgressTracker) finish() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.start, p.end = 100, 100
	text := p.text
	if text == "" {
		text = "节点已就绪"
	}
	p.publish(1, text)
}

func desktopScanBegin(proto string) {
	if desktopScan != nil {
		desktopScan.begin(proto)
	}
}
func desktopScanNote(stage, text string) {
	if desktopScan != nil {
		desktopScan.note(stage, text)
	}
}
func desktopScanReady(available int, cached bool) {
	if desktopScan != nil {
		desktopScan.ready(available, cached)
	}
}
