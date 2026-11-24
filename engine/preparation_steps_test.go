package main

import (
	"strings"
	"testing"
)

func TestPreparationStepsMatchProtocolWorkflows(t *testing.T) {
	for _, tc := range []struct {
		proto string
		total int
	}{{protoWG, 6}, {protoAWG, 6}, {protoMASQUE, 6}, {protoMASQUEH2, 6}, {protoWWarp, 12}, {protoMASQUEMASQUE, 12}} {
		t.Run(tc.proto, func(t *testing.T) {
			s := defaultSettings()
			s.Proto = tc.proto
			var events []map[string]any
			p := newScanProgressTracker(s, func(e map[string]any) { events = append(events, e) })
			p.registration("注册基础账号", 0, 0)
			p.registration("基础账号就绪，注册外层账号", 1, 2)
			if len(p.plan) != tc.total || events[0]["step"] != 1 || events[0]["steps"] != tc.total || !strings.Contains(events[1]["detail"].(string), "1/2 账号") {
				t.Fatal("registration plan or real account counters incorrect")
			}
			for segment := 1; segment <= 1+boolInt(p.chain); segment++ {
				proto := s.Proto
				if p.chain {
					proto = protoWG
					if tc.proto == protoMASQUEMASQUE {
						proto = protoMASQUE
					}
				}
				p.begin(proto)
				before := len(events)
				p.registration("账号已就绪", 1, 1)
				if len(events) != before {
					t.Fatal("cached account recheck moved progress back to registration")
				}
				p.note("start", "准备候选")
				if segment > 1 {
					p.note("outer-dial", "建立外层")
				}
				p.update(scanProgressMsg{stage: "verify", completed: 4, total: 14, succeeded: 2, target: 4, quality: 1, skipped: 10, finished: true})
				verify := events[len(events)-1]
				if !strings.Contains(verify["detail"].(string), "4/14 节点 · 可用 2 · 优质 1/4 · 跳过 10") {
					t.Fatal("actual verification counters lost")
				}
				p.note("rank", "排序")
				p.note("cache-write", "保存")
				last := events[len(events)-1]
				if segment == 1 && p.chain {
					if last["step"].(int) >= tc.total {
						t.Fatal("outer claimed final step")
					}
				} else if last["step"] != tc.total {
					t.Fatal("final cache-write not the final major step")
				}
			}
			p.finish()
			if events[len(events)-1]["done"] != float64(100) {
				t.Fatal("not complete")
			}
		})
	}
}

func boolInt(value bool) int {
	if value {
		return 1
	}
	return 0
}

func TestPreparationCachedChainKeepsSeparateRealSteps(t *testing.T) {
	s := defaultSettings()
	s.Proto = protoMASQUEMASQUE
	var events []map[string]any
	p := newScanProgressTracker(s, func(e map[string]any) { events = append(events, e) })
	p.begin(protoMASQUE)
	p.ready(4, true)
	first := events[len(events)-1]
	p.begin(protoMASQUE)
	p.ready(2, true)
	second := events[len(events)-1]
	if first["step"] != 2 || second["step"] != 7 || first["steps"] != 12 || second["steps"] != 12 || !strings.Contains(second["detail"].(string), "2 个可用节点") {
		t.Fatal("cached stages did not preserve actual layer and counts")
	}
	if p.percent >= 100 {
		t.Fatal("cached layer was prematurely treated as full preparation")
	}
	previous := p.percent
	p.begin(protoMASQUE, 1)
	p.ready(3, true)
	if events[len(events)-1]["step"] != 2 || p.percent < previous {
		t.Fatal("outer retry lost its real step or reset progress")
	}
	p.begin(protoMASQUE, 2)
	p.update(scanProgressMsg{stage: "verify", completed: 1, total: 8})
	if events[len(events)-1]["step"] != 10 || p.percent < previous {
		t.Fatal("inner retry lost its real step or reset progress")
	}
}
