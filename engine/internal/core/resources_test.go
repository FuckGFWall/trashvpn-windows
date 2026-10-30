package core

import (
	"archive/zip"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func writeGeoPair(t *testing.T, dir string, value byte, at time.Time) {
	t.Helper()
	if e := os.MkdirAll(dir, 0700); e != nil {
		t.Fatal(e)
	}
	for _, a := range geoAssets {
		path := filepath.Join(dir, a.Name)
		if e := os.WriteFile(path, bytes.Repeat([]byte{value}, 600*1024), 0600); e != nil {
			t.Fatal(e)
		}
		if e := os.Chtimes(path, at, at); e != nil {
			t.Fatal(e)
		}
	}
}
func TestGeoResourceAge(t *testing.T) {
	now := time.Date(2026, 10, 30, 12, 0, 0, 0, time.Local)
	dir := t.TempDir()
	if _, due, _ := GeoResourceStatus(dir, now); !due {
		t.Fatal("missing pair not due")
	}
	writeGeoPair(t, dir, 1, now.Add(-7*24*time.Hour))
	if date, due, _ := GeoResourceStatus(dir, now); due || date != "2026-10-23" {
		t.Fatalf("7 day boundary %s %v", date, due)
	}
	if _, due, _ := GeoResourceStatus(dir, now.Add(time.Second)); !due {
		t.Fatal("over 7 days not due")
	}
	writeGeoPair(t, dir, 1, now.Add(time.Hour))
	if _, due, _ := GeoResourceStatus(dir, now); !due {
		t.Fatal("future timestamps not handled")
	}
}
func TestGeoStagingAndDeferredApply(t *testing.T) {
	now := time.Now().Truncate(time.Second)
	dir := t.TempDir()
	writeGeoPair(t, dir, 1, now.Add(-8*24*time.Hour))
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		w.(http.Flusher).Flush() // Explicit unknown Content-Length.
		_, _ = w.Write(bytes.Repeat([]byte{2}, 600*1024))
	}))
	defer server.Close()
	progress := captureResourceProgress(t, func() {
		if e := StageGeoData(context.Background(), dir, server.Client(), server.URL+"/", now); e != nil {
			t.Fatal(e)
		}
	})
	for _, asset := range geoAssets {
		started, completed := false, false
		for _, sample := range progress {
			if sample.File == asset.Name && sample.Phase == "download" && sample.Total == 0 {
				started = true
			}
			if sample.File == asset.Name && sample.Phase == "downloaded" {
				completed = started
			}
		}
		if !completed {
			t.Fatalf("unknown-length %s did not signal file EOF", asset.Name)
		}
	}
	if len(progress) == 0 || progress[len(progress)-1].Phase != "staged" {
		t.Fatal("download completion must precede the final staged state")
	}
	if e := StageGeoData(context.Background(), dir, server.Client(), server.URL+"/", now); e != nil || requests != 2 {
		t.Fatal("duplicate pending download")
	}
	if _, due, pending := GeoResourceStatus(dir, now); !due || !pending {
		t.Fatal("stage changed live age / pending")
	}
	if e := ApplyPendingGeoData(dir, true); e != nil {
		t.Fatal(e)
	}
	live, _ := os.ReadFile(filepath.Join(dir, geoAssets[0].Name))
	if live[0] != 1 {
		t.Fatal("live database overwritten while connected")
	}
	if e := ApplyPendingGeoData(dir, false); e != nil {
		t.Fatal(e)
	}
	live, _ = os.ReadFile(filepath.Join(dir, geoAssets[0].Name))
	old, _ := os.ReadFile(filepath.Join(dir, ".geofiles-previous", geoAssets[0].Name))
	if live[0] != 2 || old[0] != 1 {
		t.Fatal("apply / rollback pair wrong")
	}
	if _, due, pending := GeoResourceStatus(dir, now); due || pending {
		t.Fatal("applied pair not marked current")
	}
}
func TestGeoPartialOrCorruptUpdateNeverReplacesLive(t *testing.T) {
	now := time.Now()
	dir := t.TempDir()
	writeGeoPair(t, dir, 1, now.Add(-8*24*time.Hour))
	fail := true
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if fail && r.URL.Path == "/geoip.metadb" {
			w.WriteHeader(503)
			return
		}
		_, _ = w.Write(bytes.Repeat([]byte{2}, 600*1024))
	}))
	defer server.Close()
	if e := StageGeoData(context.Background(), dir, server.Client(), server.URL+"/", now); e == nil {
		t.Fatal("partial download accepted")
	}
	if readPending(dir).Ready {
		t.Fatal("partial pair marked ready")
	}
	fail = false
	if e := StageGeoData(context.Background(), dir, server.Client(), server.URL+"/", now); e != nil {
		t.Fatal(e)
	}
	if e := os.WriteFile(filepath.Join(dir, ".geofiles-staging", geoAssets[0].Name), bytes.Repeat([]byte{9}, 600*1024), 0600); e != nil {
		t.Fatal(e)
	}
	if e := ApplyPendingGeoData(dir, false); e == nil {
		t.Fatal("corrupt stage accepted")
	}
	live, _ := os.ReadFile(filepath.Join(dir, geoAssets[0].Name))
	if live[0] != 1 {
		t.Fatal("failed update changed live file")
	}
}
func TestCoreCandidateDownloadAndPermanentOldRemoval(t *testing.T) {
	var archive bytes.Buffer
	z := zip.NewWriter(&archive)
	f, _ := z.Create("mihomo-windows-amd64-compatible.exe")
	_, _ = f.Write([]byte("MZ fixture core"))
	_ = z.Close()
	sum := sha256.Sum256(archive.Bytes())
	bad := false
	var server *httptest.Server
	server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/archive" {
			_, _ = w.Write(archive.Bytes())
			return
		}
		osPart, arch, _ := ostuple()
		digest := hex.EncodeToString(sum[:])
		if bad {
			digest = string(bytes.Repeat([]byte{'0'}, 64))
		}
		fmt.Fprintf(w, `{"tag_name":"v9.0.0","assets":[{"name":"mihomo-%s-%s-compatible-v9.0.0.zip","browser_download_url":"%s/archive","digest":"sha256:%s"}]}`, osPart, arch, server.URL, digest)
	}))
	defer server.Close()
	dir := t.TempDir()
	target := CoreInDir(dir)
	_ = os.WriteFile(target, []byte("old core"), 0600)
	var candidate string
	var e error
	progress := captureResourceProgress(t, func() {
		candidate, e = downloadBinaryCandidate(context.Background(), dir, server.Client(), server.URL+"/release")
		if e != nil {
			t.Fatal(e)
		}
	})
	downloaded := false
	for _, sample := range progress {
		if sample.Resource == "core" && sample.Phase == "downloaded" && sample.Done == int64(archive.Len()) {
			downloaded = true
		}
		if sample.Phase == "validate" && !downloaded {
			t.Fatal("core validation began without signalling completed file transfer")
		}
	}
	if !downloaded {
		t.Fatal("core transfer never signalled EOF")
	}
	if e = InstallValidatedBinary(dir, candidate, true); e == nil {
		t.Fatal("running core replaced")
	}
	old, _ := os.ReadFile(target)
	if string(old) != "old core" {
		t.Fatal("download overwrote live core")
	}
	if e = InstallValidatedBinary(dir, candidate, false); e != nil {
		t.Fatal(e)
	}
	if _, e = os.Stat(target + ".old"); !os.IsNotExist(e) {
		t.Fatal("old core not permanently removed")
	}
	live, _ := os.ReadFile(target)
	if string(live) != "MZ fixture core" {
		t.Fatal("wrong installed binary")
	}
	bad = true
	if _, e = downloadBinaryCandidate(context.Background(), dir, server.Client(), server.URL+"/release"); e == nil {
		t.Fatal("bad checksum accepted")
	}
}

func TestGeoApplyRollbackPreservesOldPairAndDate(t *testing.T) {
	now := time.Now().Truncate(time.Second)
	oldTime := now.Add(-8 * 24 * time.Hour)
	dir := t.TempDir()
	writeGeoPair(t, dir, 1, oldTime)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write(bytes.Repeat([]byte{2}, 600*1024)) }))
	defer server.Close()
	if e := StageGeoData(context.Background(), dir, server.Client(), server.URL+"/", now); e != nil {
		t.Fatal(e)
	}
	// Fail the second file after the first has already been replaced.
	if e := os.Mkdir(filepath.Join(dir, "geoip.metadb.update"), 0700); e != nil {
		t.Fatal(e)
	}
	if e := ApplyPendingGeoData(dir, false); e == nil {
		t.Fatal("partial apply succeeded unexpectedly")
	}
	for _, a := range geoAssets {
		b, e := os.ReadFile(filepath.Join(dir, a.Name))
		if e != nil || b[0] != 1 {
			t.Fatal("old pair not restored")
		}
	}
	date, due, _ := GeoResourceStatus(dir, now)
	if !due || date != oldTime.Local().Format("2006-01-02") {
		t.Fatal("rollback incorrectly marked old files freshly updated")
	}
}
func TestResourceLockCancellation(t *testing.T) {
	dir := t.TempDir()
	unlock, e := ResourceLock(context.Background(), dir)
	if e != nil {
		t.Fatal(e)
	}
	defer unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Millisecond)
	defer cancel()
	if release, e := ResourceLock(ctx, dir); e == nil {
		release()
		t.Fatal("resource lock ignored cancellation")
	}
}

func TestGeoApplyRollbackRestoresMissingFile(t *testing.T) {
	now := time.Now()
	dir := t.TempDir()
	// There was no complete old pair. A partial install must not be mistaken
	// for usable old data when connect decides whether it can continue.
	if e := os.WriteFile(filepath.Join(dir, "geoip.metadb"), bytes.Repeat([]byte{1}, 600*1024), 0600); e != nil {
		t.Fatal(e)
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write(bytes.Repeat([]byte{2}, 600*1024)) }))
	defer server.Close()
	if e := StageGeoData(context.Background(), dir, server.Client(), server.URL+"/", now); e != nil {
		t.Fatal(e)
	}
	if e := os.Mkdir(filepath.Join(dir, "geoip.metadb.update"), 0700); e != nil {
		t.Fatal(e)
	}
	if e := ApplyPendingGeoData(dir, false); e == nil {
		t.Fatal("partial apply unexpectedly succeeded")
	}
	if GeoDataReady(dir) {
		t.Fatal("failed install left a mixed pair that could fake usable old data")
	}
	if _, e := os.Stat(filepath.Join(dir, "geosite.dat")); !os.IsNotExist(e) {
		t.Fatal("originally absent file not restored to absent")
	}
	if _, e := os.Stat(filepath.Join(dir, ".geofiles-previous", "geosite.dat.uncommitted")); e != nil {
		t.Fatal("uncommitted new file was not retained")
	}
}
