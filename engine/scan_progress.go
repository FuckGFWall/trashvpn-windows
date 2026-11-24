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
	plan                []preparationStep
}

type preparationStep struct {
	segment     int
	stage, name string
}

// A chain has a separate verification pipeline and cache for each layer.
func newScanProgressTracker(s clientSettings, send func(map[string]any)) *scanProgressTracker {
	p := &scanProgressTracker{chain: protoChained(s.Proto), send: send,
		plan: []preparationStep{{0, "register", "注册账号"}}}
	layers := 1
	if p.chain {
		layers = 2
	}
	for i := range layers {
		prefix := ""
		if p.chain {
			if i == 0 {
				prefix = "外层"
			} else {
				prefix = "内层"
			}
		}
		add := func(stage, name string) { p.plan = append(p.plan, preparationStep{i + 1, stage, prefix + name}) }
		add("cache-check", "检查缓存")
		add("start", "准备候选")
		if i > 0 {
			add("outer-dial", "建立外层隧道")
		}
		add("verify", "验证隧道")
		add("rank", "整理节点")
		add("cache-write", "保存缓存")
	}
	return p
}

func (p *scanProgressTracker) describe(e map[string]any, stage, detail string) {
	if stage == "cached" {
		stage = "cache-check"
	}
	for i, step := range p.plan {
		if step.stage == stage && (stage == "register" || step.segment == p.segment) {
			e["step"], e["steps"], e["operation"], e["detail"] = i+1, len(p.plan), step.name, detail
			return
		}
	}
}

func (p *scanProgressTracker) registration(text string, done, total int) {
	p.mu.Lock()
	defer p.mu.Unlock()
	// doScan re-checks already loaded credentials. It is not another enrolment
	// and must not send the dashboard back to major step 1 mid-scan.
	if p.segment > 0 && total > 0 && done >= total {
		return
	}
	e := map[string]any{"type": "progress", "phase": "register", "text": text, "done": done, "total": total}
	detail := text
	if total > 0 && done < total {
		detail = fmt.Sprintf("%d/%d 账号 · %s", done, total, text)
	}
	p.describe(e, "register", detail)
	p.send(e)
}

func (p *scanProgressTracker) publish(fraction float64, text string) {
	p.percent = math.Max(p.percent, p.start+(p.end-p.start)*fraction)
	p.text = text
	e := map[string]any{"type": "progress", "phase": "scan", "text": text,
		"done": p.percent, "total": 100, "stage": p.last.stage,
		"completed": p.last.completed, "succeeded": p.last.succeeded, "quality": p.last.quality, "skipped": p.last.skipped,
		"candidates": p.last.total, "target": p.last.target}
	detail := text
	switch p.last.stage {
	case "cache-check":
		detail = "读取当前协议缓存"
	case "start":
		detail = "整理候选地址与端口"
	case "outer-dial":
		detail = "等待外层握手与隧道就绪"
	case "rank":
		detail = "按丢包与延迟排序"
	case "cache-write":
		detail = "写入本地优选缓存"
	case "cached":
		detail = fmt.Sprintf("命中 %d 个可用节点", p.last.succeeded)
	case "verify":
		detail = fmt.Sprintf("%d/%d 节点 · 可用 %d", p.last.completed, p.last.total, p.last.succeeded)
		if p.last.target > 0 {
			detail += fmt.Sprintf(" · 优质 %d/%d", p.last.quality, p.last.target)
		}
		if !p.last.finished && p.last.detail != "" {
			detail += " · " + p.last.detail
		}
		if p.last.skipped > 0 {
			detail += fmt.Sprintf(" · 跳过 %d", p.last.skipped)
		}
		if p.last.budgetReached {
			detail += " · 已达时间预算"
		}
	}
	p.describe(e, p.last.stage, detail)
	p.send(e)
}

func (p *scanProgressTracker) begin(proto string, layer ...int) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.segment++
	if len(layer) > 0 {
		p.segment = layer[0]
	}
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
	if m.target > 0 {
		ratio = math.Max(ratio, float64(m.quality)/float64(m.target))
	}
	if m.finished {
		ratio = 1
	}
	var fraction float64
	var text string
	switch m.stage {
	case "verify":
		fraction = ratio * .95
		text = fmt.Sprintf("验证隧道 · 完成 %d/%d · 可用 %d", m.completed, m.total, m.succeeded)
		if m.target > 0 {
			text += fmt.Sprintf(" · 优质 %d/%d", m.quality, m.target)
		}
		text += fmt.Sprintf(" · 跳过 %d", m.skipped)
		if m.budgetReached {
			text += "（已到时间预算）"
		}
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
		if p.last.budgetReached {
			text += "（预算内结果）"
		}
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

func desktopScanBegin(opts options) {
	if desktopScan != nil {
		layer, proto := 1, opts.proto
		if desktopScan.chain && opts.through != "" {
			layer, proto = 2, opts.innerProto
		}
		desktopScan.begin(proto, layer)
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
