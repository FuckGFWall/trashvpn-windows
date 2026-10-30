package core

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

func ResourceLock(ctx context.Context, dir string) (func(), error) {
	if err := os.MkdirAll(dir, 0700); err != nil {
		return nil, err
	}
	f, err := os.OpenFile(filepath.Join(dir, ".resources.lock"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	unlock, err := lockResourceFile(ctx, f)
	if err != nil {
		f.Close()
		return nil, err
	}
	return func() { unlock(); f.Close() }, nil
}

type geoPending struct {
	Ready      bool              `json:"ready"`
	Downloaded time.Time         `json:"downloaded"`
	Hashes     map[string]string `json:"hashes"`
}

func pendingPath(dir string) string { return filepath.Join(dir, ".geofiles-staging", "ready.json") }
func readPending(dir string) geoPending {
	var p geoPending
	b, _ := os.ReadFile(pendingPath(dir))
	_ = json.Unmarshal(b, &p)
	return p
}
func writePending(dir string, p geoPending) error {
	b, e := json.Marshal(p)
	if e != nil {
		return e
	}
	path := pendingPath(dir)
	if e = os.WriteFile(path+".tmp", b, 0600); e != nil {
		return e
	}
	return os.Rename(path+".tmp", path)
}
func hashFile(path string) (string, error) {
	f, e := os.Open(path)
	if e != nil {
		return "", e
	}
	defer f.Close()
	h := sha256.New()
	if _, e = io.Copy(h, f); e != nil {
		return "", e
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// The oldest file is the pair's effective update date. Exact 7 days is still
// current; missing/short files or future timestamps require an update.
func GeoResourceStatus(dir string, now time.Time) (date string, due, pending bool) {
	pending = readPending(dir).Ready
	if !GeoDataReady(dir) {
		return "", true, pending
	}
	var oldest time.Time
	for _, a := range geoAssets {
		fi, e := os.Stat(filepath.Join(dir, a.Name))
		if e != nil {
			return "", true, pending
		}
		if oldest.IsZero() || fi.ModTime().Before(oldest) {
			oldest = fi.ModTime()
		}
	}
	return oldest.Local().Format("2006-01-02"), now.Sub(oldest) > 7*24*time.Hour || oldest.After(now.Add(time.Minute)), pending
}

// Download both public databases into staging. Never write a live file. The
// caller supplies an explicit VPN proxy client for automatic updates.
func StageGeoData(ctx context.Context, dir string, client *http.Client, base string, now time.Time) error {
	stage := filepath.Join(dir, ".geofiles-staging")
	unlock, e := ResourceLock(ctx, stage)
	if e != nil {
		return e
	}
	defer unlock()
	if readPending(dir).Ready {
		return nil
	}
	if e = writePending(dir, geoPending{}); e != nil {
		return e
	}
	p := geoPending{Ready: true, Downloaded: now, Hashes: map[string]string{}}
	for _, a := range geoAssets {
		fmt.Println("下载 GeoFiles：" + a.Name)
		if _, e = fetchToFile(ctx, client, base+a.Name, filepath.Join(stage, a.Name), a.Min, func(done, total int64) { ReportResourceProgress("geo", "download", a.Name, done, total) }); e != nil {
			return e
		}
		p.Hashes[a.Name], e = hashFile(filepath.Join(stage, a.Name))
		if e != nil {
			return e
		}
	}
	if _, e = os.Stat(CoreInDir(dir)); e == nil {
		ReportResourceProgress("geo", "validate", "", 0, 0)
		yaml := []byte("mode: rule\ngeo-auto-update: false\nrules:\n  - GEOSITE,cn,DIRECT\n  - GEOIP,CN,DIRECT,no-resolve\n  - MATCH,DIRECT\n")
		ok, _, checkErr := TestConfigDetailContext(ctx, CoreInDir(dir), stage, yaml)
		if !ok {
			return fmt.Errorf("下载的 GeoFiles 解析验证失败: %w", checkErr)
		}
	}
	if e = writePending(dir, p); e != nil {
		return e
	}
	ReportResourceProgress("geo", "staged", "", 1, 1)
	return nil
}
func StageGeoDataOfficial(ctx context.Context, dir string, client *http.Client) error {
	return StageGeoData(ctx, dir, client, geoDataTagURL, time.Now())
}

func copyResource(source, target string) error {
	in, e := os.Open(source)
	if e != nil {
		return e
	}
	defer in.Close()
	out, e := os.OpenFile(target, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	info, _ := in.Stat()
	_, e = io.Copy(out, in)
	closeErr := out.Close()
	if e != nil {
		return e
	}
	if closeErr != nil {
		return closeErr
	}
	if info != nil {
		return os.Chtimes(target, info.ModTime(), info.ModTime())
	}
	return nil
}

// Caller holds ResourceLock and has proved the managed core is stopped. Keep
// the previous pair locally for rollback; do not delete old GeoFiles.
func ApplyPendingGeoData(dir string, running bool) error {
	if running {
		return nil
	}
	p := readPending(dir)
	if !p.Ready {
		return nil
	}
	stage := filepath.Join(dir, ".geofiles-staging")
	backup := filepath.Join(dir, ".geofiles-previous")
	if !GeoDataReady(stage) {
		p.Ready = false
		_ = writePending(dir, p)
		return fmt.Errorf("暂存 GeoFiles 不完整")
	}
	for _, a := range geoAssets {
		h, e := hashFile(filepath.Join(stage, a.Name))
		if e != nil || h != p.Hashes[a.Name] {
			p.Ready = false
			_ = writePending(dir, p)
			return fmt.Errorf("暂存 GeoFiles 校验失败: %s", a.Name)
		}
	}
	if e := os.MkdirAll(backup, 0700); e != nil {
		return e
	}
	existed := map[string]bool{}
	for _, a := range geoAssets {
		path := filepath.Join(dir, a.Name)
		if _, e := os.Stat(path); e == nil {
			if e = copyResource(path, filepath.Join(backup, a.Name)); e != nil {
				return e
			}
			existed[a.Name] = true
		}
	}
	for _, a := range geoAssets {
		target := filepath.Join(dir, a.Name)
		e := copyResource(filepath.Join(stage, a.Name), target+".update")
		if e == nil {
			e = os.Rename(target+".update", target)
		}
		if e != nil {
			var rollbackErrors []error
			for _, old := range geoAssets {
				if existed[old.Name] {
					rollbackErrors = append(rollbackErrors, copyResource(filepath.Join(backup, old.Name), filepath.Join(dir, old.Name)))
				} else {
					// Restore an originally absent path without deleting the
					// newly installed file or leaving a mixed, usable pair.
					path := filepath.Join(dir, old.Name)
					if _, statErr := os.Stat(path); statErr == nil {
						rollbackErrors = append(rollbackErrors, os.Rename(path, filepath.Join(backup, old.Name+".uncommitted")))
					}
				}
			}
			return errors.Join(e, errors.Join(rollbackErrors...))
		}
		_ = os.Chtimes(target, p.Downloaded, p.Downloaded)
	}
	p.Ready = false
	if e := writePending(dir, p); e != nil {
		return e
	}
	ReportResourceProgress("geo", "installed", "", 1, 1)
	return nil
}
