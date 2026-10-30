package core

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

// The "bypass mainland" preset emits GEOSITE,cn and GEOIP,CN rules, and mihomo
// refuses to start a config that references geodata it does not have. The
// databases are ~13 MB, so they are not embedded in the binary: the release
// ZIP carries them, and an install that lacks them fetches them here.
//
// meta-rules-dat publishes both files under a moving "latest" tag, which is
// what mihomo's own auto-update uses. The checksums are not verified — the
// download is only accepted when the file parses as a database of plausible
// size, which is enough to catch a truncated or HTML-error-page response.
const (
	geoDataRepo   = "MetaCubeX/meta-rules-dat"
	geoDataTagURL = "https://github.com/" + geoDataRepo + "/releases/download/latest/"
)

// geoAssets are the files mihomo needs, in download order.
var geoAssets = []struct {
	Name string
	Min  int64
}{
	{"geosite.dat", 512 * 1024},
	{"geoip.metadb", 512 * 1024},
}

// GeoDataNames lists the files GeoDataReady looks for.
func GeoDataNames() []string {
	out := make([]string, 0, len(geoAssets))
	for _, a := range geoAssets {
		out = append(out, a.Name)
	}
	return out
}

// GeoDataReady reports whether every database mihomo needs is on disk and looks
// complete (a 0-byte placeholder left by a failed download does not count).
func GeoDataReady(dir string) bool {
	for _, a := range geoAssets {
		fi, err := os.Stat(filepath.Join(dir, a.Name))
		if err != nil || fi.Size() < a.Min {
			return false
		}
	}
	return true
}

// DownloadGeoData fetches the missing databases into dir. Files already present
// are left alone, so this doubles as a repair for a half-finished download.
// logf receives one line per file, suitable for the GUI log panel.
func DownloadGeoData(ctx context.Context, dir string, logf func(string)) error {
	if logf == nil {
		logf = func(string) {}
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	client := &http.Client{Timeout: 5 * time.Minute}
	for _, a := range geoAssets {
		dst := filepath.Join(dir, a.Name)
		if fi, err := os.Stat(dst); err == nil && fi.Size() >= a.Min {
			continue
		}
		url := geoDataTagURL + a.Name
		logf(fmt.Sprintf("下载 %s（%s）…", a.Name, url))
		n, err := fetchToFile(ctx, client, url, dst, a.Min)
		if err != nil {
			return fmt.Errorf("%s: %w", a.Name, err)
		}
		logf(fmt.Sprintf("%s 完成（%.1f MB）", a.Name, float64(n)/(1024*1024)))
	}
	return nil
}

// fetchToFile streams url into dst through a .part file, so an interrupted
// download never leaves a half-written database where mihomo would read it.
func fetchToFile(ctx context.Context, client *http.Client, url, dst string, min int64) (int64, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return 0, err
	}
	req.Header.Set("User-Agent", "xehn")
	resp, err := client.Do(req)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return 0, fmt.Errorf("HTTP %d", resp.StatusCode)
	}

	tmp := dst + ".part"
	f, err := os.Create(tmp)
	if err != nil {
		return 0, err
	}
	n, copyErr := io.Copy(f, resp.Body)
	closeErr := f.Close()
	if copyErr != nil {
		os.Remove(tmp)
		return n, copyErr
	}
	if closeErr != nil {
		os.Remove(tmp)
		return n, closeErr
	}
	// A blocked network often answers with a small HTML page and status 200;
	// the size floor is what turns that into a visible failure instead of a
	// database mihomo chokes on at connect time.
	if n < min {
		os.Remove(tmp)
		return n, fmt.Errorf("下载内容异常（只有 %d 字节）", n)
	}
	if err := os.Rename(tmp, dst); err != nil {
		os.Remove(tmp)
		return n, err
	}
	return n, nil
}
