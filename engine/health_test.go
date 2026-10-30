package main

import "testing"

// The failure counter is the difference between a session that shakes off a
// hiccup and one that throws away a working endpoint: a torn-down endpoint
// shows up here as the same generic error a single DNS timeout does, so the
// counting is what tells them apart. These lock that down.
func TestJudgeHealthTransientFailureDoesNotRotate(t *testing.T) {
	cases := []struct {
		name       string
		fails      int
		ok         bool
		wantFails  int
		wantRepair bool
		wantRecov  bool
	}{
		{"healthy stays healthy", 0, true, 0, false, false},
		{"one failure counted, no rotation", 0, false, 1, false, false},
		{"two failures counted, no rotation", 1, false, 2, false, false},
		{"third failure rotates", 2, false, 0, true, false},
		{"recovered after one failure clears it", 1, true, 0, false, true},
		{"recovered after two failures clears it", 2, true, 0, false, true},
		{"counting restarts after a rotation", 0, false, 1, false, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := judgeHealth(tc.fails, tc.ok)
			if got.fails != tc.wantFails || got.repair != tc.wantRepair || got.recovered != tc.wantRecov {
				t.Fatalf("judgeHealth(%d,%v) = %+v, want fails=%d repair=%v recovered=%v",
					tc.fails, tc.ok, got, tc.wantFails, tc.wantRepair, tc.wantRecov)
			}
		})
	}
}

// A whole bad streak then one good answer: the endpoint must survive it. This
// is the case a heavier hand gets wrong - the failures are real, they just
// stopped being true.
func TestJudgeHealthStreakThenRecovery(t *testing.T) {
	fails := 0
	for i := 0; i < healthFailLimit-1; i++ {
		v := judgeHealth(fails, false)
		if v.repair {
			t.Fatalf("rotated after %d failures", i+1)
		}
		fails = v.fails
	}
	v := judgeHealth(fails, true)
	if v.repair || v.fails != 0 || !v.recovered {
		t.Fatalf("judgeHealth(%d,true) = %+v, want cleared and reported recovered", fails, v)
	}
}

// processAlive must not claim liveness for pids that cannot be ours: ownership
// is decided by this answer, and a wrong "alive" keeps a session unmonitored.
func TestProcessAliveRejectsImpossiblePids(t *testing.T) {
	for _, pid := range []int{0, -1} {
		if processAlive(pid) {
			t.Fatalf("processAlive(%d) = true, want false", pid)
		}
	}
}
