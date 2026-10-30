package core

import (
	"encoding/json"
	"fmt"
	"io"
	"time"
)

type ResourceProgress struct {
	Resource string `json:"resource"`
	Phase    string `json:"phase"`
	File     string `json:"file"`
	Done     int64  `json:"done"`
	Total    int64  `json:"total"`
}

func ReportResourceProgress(resource, phase, file string, done, total int64) {
	b, _ := json.Marshal(ResourceProgress{resource, phase, file, done, total})
	fmt.Println("@@RESOURCE_PROGRESS " + string(b))
}

type resourceProgressWriter struct {
	target            io.Writer
	done, total, last int64
	sent              time.Time
	report            func(int64, int64)
}

func (p *resourceProgressWriter) Write(b []byte) (int, error) {
	n, e := p.target.Write(b)
	p.done += int64(n)
	if p.done-p.last >= 256*1024 || time.Since(p.sent) > 150*time.Millisecond {
		p.report(p.done, p.total)
		p.last = p.done
		p.sent = time.Now()
	}
	return n, e
}
func copyResourceProgress(target io.Writer, source io.Reader, total int64, report func(int64, int64)) (int64, error) {
	if total < 0 {
		total = 0
	}
	report(0, total)
	p := &resourceProgressWriter{target: target, total: total, report: report, sent: time.Now()}
	_, err := io.Copy(p, source)
	report(p.done, total)
	return p.done, err
}
