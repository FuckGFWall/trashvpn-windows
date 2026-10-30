package core

import (
	"archive/zip"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
)

// The latest official stable release, baseline/compatible CPU build. Validate
// GitHub's asset SHA256 before unpacking or executing any candidate.
func DownloadBinaryCandidate(ctx context.Context, dir string, client *http.Client) (string, error) {
	return downloadBinaryCandidate(ctx, dir, client, releasesAPI+"/latest")
}
func downloadBinaryCandidate(ctx context.Context, dir string, client *http.Client, api string) (string, error) {
	req, e := http.NewRequestWithContext(ctx, "GET", api, nil)
	if e != nil {
		return "", e
	}
	req.Header.Set("User-Agent", "XEHN/1.0.0")
	resp, e := client.Do(req)
	if e != nil {
		return "", e
	}
	var r struct {
		Tag    string `json:"tag_name"`
		Assets []struct {
			Name   string `json:"name"`
			URL    string `json:"browser_download_url"`
			Digest string `json:"digest"`
		} `json:"assets"`
	}
	if resp.StatusCode != 200 {
		resp.Body.Close()
		return "", fmt.Errorf("内核发布信息 HTTP %d", resp.StatusCode)
	}
	e = json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(&r)
	resp.Body.Close()
	if e != nil {
		return "", e
	}
	osPart, arch, _ := ostuple()
	prefix := "mihomo-" + osPart + "-" + arch
	assetURL, digest := "", ""
	for _, name := range []string{prefix + "-compatible-" + r.Tag + ".zip", prefix + "-" + r.Tag + ".zip"} {
		for _, a := range r.Assets {
			if a.Name == name {
				assetURL, digest = a.URL, a.Digest
				break
			}
		}
		if assetURL != "" {
			break
		}
	}
	if assetURL == "" || !strings.HasPrefix(digest, "sha256:") || len(digest) != 71 {
		return "", fmt.Errorf("没有适用且含 SHA256 的内核资源")
	}
	req, e = http.NewRequestWithContext(ctx, "GET", assetURL, nil)
	if e != nil {
		return "", e
	}
	resp, e = client.Do(req)
	if e != nil {
		return "", e
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return "", fmt.Errorf("内核下载 HTTP %d", resp.StatusCode)
	}
	var downloaded bytes.Buffer
	_, e = copyResourceProgress(&downloaded, io.LimitReader(resp.Body, 128<<20+1), resp.ContentLength, func(done, total int64) { ReportResourceProgress("core", "download", "mihomo.zip", done, total) })
	data := downloaded.Bytes()
	if e != nil {
		return "", e
	}
	ReportResourceProgress("core", "downloaded", "mihomo.zip", int64(len(data)), int64(len(data)))
	if len(data) > 128<<20 {
		return "", fmt.Errorf("内核压缩包过大")
	}
	sum := sha256.Sum256(data)
	if hex.EncodeToString(sum[:]) != strings.TrimPrefix(digest, "sha256:") {
		return "", fmt.Errorf("内核 SHA256 校验失败")
	}
	ReportResourceProgress("core", "validate", "", 0, 0)
	stage := filepath.Join(dir, ".core-staging")
	if e = os.MkdirAll(stage, 0700); e != nil {
		return "", e
	}
	path := filepath.Join(stage, ExeName())
	if e = unpackBinaryCandidate(data, path); e != nil {
		return "", e
	}
	return path, nil
}
func unpackBinaryCandidate(data []byte, target string) error {
	z, e := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if e != nil {
		return e
	}
	var found *zip.File
	for _, f := range z.File {
		name := filepath.Base(f.Name)
		if !f.FileInfo().IsDir() && strings.HasPrefix(strings.ToLower(name), "mihomo") && strings.HasSuffix(strings.ToLower(name), ".exe") {
			if found != nil {
				return fmt.Errorf("内核包包含多个可执行文件")
			}
			found = f
		}
	}
	if found == nil || found.UncompressedSize64 > 256<<20 {
		return fmt.Errorf("内核包缺少合法 mihomo 可执行文件")
	}
	reader, e := found.Open()
	if e != nil {
		return e
	}
	defer reader.Close()
	out, e := os.OpenFile(target, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0700)
	if e != nil {
		return e
	}
	_, e = io.Copy(out, io.LimitReader(reader, 256<<20+1))
	closeErr := out.Close()
	if e != nil {
		return e
	}
	return closeErr
}

// Caller validated all protocol shapes and holds ResourceLock. The user
// explicitly requested permanent deletion of the replaced OLD core only.
func InstallValidatedBinary(dir, candidate string, running bool) error {
	if running {
		return fmt.Errorf("请先断开 VPN；不会停止或覆盖运行中的内核")
	}
	want := filepath.Join(dir, ".core-staging", ExeName())
	a, _ := filepath.Abs(candidate)
	b, _ := filepath.Abs(want)
	if !strings.EqualFold(a, b) {
		return fmt.Errorf("内核暂存路径无效")
	}
	target := CoreInDir(dir)
	backup := target + ".old"
	hadOld := false
	if _, e := os.Stat(target); e == nil {
		if e = os.Rename(target, backup); e != nil {
			return e
		}
		hadOld = true
	}
	if e := os.Rename(candidate, target); e != nil {
		if hadOld {
			_ = os.Rename(backup, target)
		}
		return e
	}
	if hadOld {
		if e := os.Remove(backup); e != nil {
			return fmt.Errorf("新内核已安装，但旧内核删除失败: %w", e)
		}
	}
	ReportResourceProgress("core", "installed", "", 1, 1)
	return nil
}
