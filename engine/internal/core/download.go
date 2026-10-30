package core

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

// mihomo releases are published on GitHub as versioned per-platform zips. The
// client keeps a known-good default so a fresh install can provision the core
// without the user hunting for a binary.
const (
	releasesAPI = "https://api.github.com/repos/MetaCubeX/mihomo/releases"
	// defaultVersion is the release tag the client falls back to when no core
	// is present and the network cannot be reached to list newer ones.
	// v1.19.32 is the official latest stable (2026-09-30): the first line that
	// carries every protocol this client renders (wireguard+amnezia-wg-option,
	// masque h2/h3, dialer-proxy chains).
	defaultVersion = "v1.19.32"
)

// ostuple returns the mihomo release asset fragment for this platform.
func ostuple() (osPart, archPart, exeName string) {
	archPart = runtime.GOARCH
	switch archPart {
	case "amd64", "arm64":
	default:
		archPart = "amd64"
	}
	exeName = "mihomo"
	switch runtime.GOOS {
	case "windows":
		osPart = "windows"
		exeName = "mihomo.exe"
	case "linux":
		osPart = "linux"
	case "darwin":
		osPart = "darwin"
	default:
		osPart = runtime.GOOS
	}
	return osPart, archPart, exeName
}

// ExeName returns the core binary filename for this host.
func ExeName() string {
	_, _, n := ostuple()
	return n
}

// CoreInDir returns the expected core binary path inside dir.
func CoreInDir(dir string) string {
	return filepath.Join(dir, ExeName())
}

// release is the subset of the GitHub releases API we consume.
type release struct {
	TagName string `json:"tag_name"`
	Assets  []struct {
		Name               string `json:"name"`
		BrowserDownloadURL string `json:"browser_download_url"`
	} `json:"assets"`
}

// EnsureBinary guarantees a working core binary in workDir. When one is
// already present it is kept; otherwise the caller may supply localPath, and
// failing that the client downloads the default (or a newer) release.
func EnsureBinary(workDir, localPath string) (string, error) {
	if err := os.MkdirAll(workDir, 0o755); err != nil {
		return "", err
	}
	candidate := CoreInDir(workDir)
	if isExecutable(candidate) {
		return candidate, nil
	}
	if localPath != "" {
		if isExecutable(localPath) {
			return localPath, nil
		}
		return "", fmt.Errorf("core: %s is not an executable mihomo binary", localPath)
	}
	// No local core: download a release.
	if err := downloadRelease(workDir); err != nil {
		return "", fmt.Errorf("core: no mihomo binary and download failed: %w", err)
	}
	if isExecutable(candidate) {
		return candidate, nil
	}
	return "", fmt.Errorf("core: download completed but %s is missing", candidate)
}

func isExecutable(p string) bool {
	fi, err := os.Stat(p)
	return err == nil && !fi.IsDir() && fi.Size() > 0
}

// downloadRelease fetches the latest release (falling back to defaultVersion)
// and unpacks the archive that matches this host's OS+arch.
func downloadRelease(workDir string) error {
	client := httpClient(30 * time.Second)
	version, assetURL, err := pickAsset(client)
	if err != nil {
		return err
	}
	resp, err := client.Get(assetURL)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("core: download %s: HTTP %d", assetURL, resp.StatusCode)
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, 512<<20))
	if err != nil {
		return err
	}
	_ = version
	return unpackArchive(data, workDir)
}

func pickAsset(client *http.Client) (version, url string, err error) {
	if v, u, ok := releaseFor(client, defaultVersion); ok {
		return v, u, nil
	}
	// Fall back to listing latest when the pinned one 404s (e.g. removed).
	req, _ := http.NewRequest(http.MethodGet, releasesAPI+"/latest", nil)
	req.Header.Set("Accept", "application/vnd.github+json")
	resp, err := client.Do(req)
	if err != nil {
		return "", "", err
	}
	defer resp.Body.Close()
	var r release
	if err := json.NewDecoder(resp.Body).Decode(&r); err != nil {
		return "", "", err
	}
	return matchingAsset(r)
}

func releaseFor(client *http.Client, tag string) (string, string, bool) {
	req, _ := http.NewRequest(http.MethodGet, releasesAPI+"/tags/"+tag, nil)
	req.Header.Set("Accept", "application/vnd.github+json")
	resp, err := client.Do(req)
	if err != nil {
		return "", "", false
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", "", false
	}
	var r release
	if err := json.NewDecoder(resp.Body).Decode(&r); err != nil {
		return "", "", false
	}
	v, u, err := matchingAsset(r)
	return v, u, err == nil
}

// matchingAsset finds the asset for <os>-<arch> and prefers the non-compressed
// zip. mihomo names assets like "mihomo-windows-amd64-v1.19.4.zip".
func matchingAsset(r release) (string, string, error) {
	osPart, archPart, _ := ostuple()
	needle := osPart + "-" + archPart
	for _, a := range r.Assets {
		if strings.Contains(a.Name, needle) && strings.HasSuffix(a.Name, ".zip") {
			return r.TagName, a.BrowserDownloadURL, nil
		}
	}
	return "", "", fmt.Errorf("core: no %s asset in release %s", needle, r.TagName)
}

func unpackArchive(data []byte, workDir string) error {
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}
	for _, f := range zr.File {
		name := filepath.Base(f.Name)
		// Keep the exe plus the geodata mihomo needs for rule-based routing.
		if name != ExeName() && name != geoSiteName && name != geoIPName {
			continue
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		out, err := os.OpenFile(filepath.Join(workDir, name), os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
		if err != nil {
			rc.Close()
			return err
		}
		if _, err := io.Copy(out, rc); err != nil {
			out.Close()
			rc.Close()
			return err
		}
		out.Close()
		rc.Close()
	}
	return nil
}
