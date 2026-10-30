package core

import (
	"bytes"
	"io"
	"testing"
)

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
