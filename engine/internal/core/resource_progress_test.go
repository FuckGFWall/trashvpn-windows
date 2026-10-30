package core

import (
	"bytes"
	"encoding/json"
	"io"
	"os"
	"strings"
	"testing"
)

// These package tests do not run in parallel. Capture the actual JSON wire
// events, not a simulated progress source, and restore stdout even on Fatal.
func captureResourceProgress(t *testing.T, action func()) []ResourceProgress {
	t.Helper()
	file, e := os.CreateTemp(t.TempDir(), "resource-progress-")
	if e != nil {
		t.Fatal(e)
	}
	previous := os.Stdout
	func() {
		os.Stdout = file
		defer func() { os.Stdout = previous; _ = file.Close() }()
		action()
	}()
	data, e := os.ReadFile(file.Name())
	if e != nil {
		t.Fatal(e)
	}
	var result []ResourceProgress
	for _, line := range strings.Split(string(data), "\n") {
		if !strings.HasPrefix(line, "@@RESOURCE_PROGRESS ") {
			continue
		}
		var progress ResourceProgress
		if e = json.Unmarshal([]byte(strings.TrimPrefix(line, "@@RESOURCE_PROGRESS ")), &progress); e != nil {
			t.Fatal(e)
		}
		result = append(result, progress)
	}
	return result
}

func TestResourceByteProgress(t *testing.T) {
	for _, total := range []int64{700000, -1} {
		data := bytes.Repeat([]byte{1}, 700000)
		last := int64(-1)
		samples := 0
		n, e := copyResourceProgress(io.Discard, bytes.NewReader(data), total, func(done, reportedTotal int64) {
			samples++
			if done < last {
				t.Error("byte progress moved backward")
			}
			last = done
			if total < 0 && reportedTotal != 0 {
				t.Error("unknown length must remain indeterminate")
			}
			if total > 0 && reportedTotal != total {
				t.Error("content length changed")
			}
		})
		if e != nil || n != int64(len(data)) || last != n || samples < 2 {
			t.Fatalf("incorrect byte progress: %d %d %d %v", n, last, samples, e)
		}
	}
}
