package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/xhwarp/warp-client/internal/core"
)

type resourceStatus struct {
	CoreVersion       string `json:"core_version"`
	CoreNeedsDownload bool   `json:"core_needs_download"`
	CoreCheck         string `json:"core_check"`
	GeoUpdated        string `json:"geo_updated"`
	GeoNeedsDownload  bool   `json:"geo_needs_download"`
	GeoPending        bool   `json:"geo_pending"`
}

func inspectResources(ctx context.Context, dir string, probe bool) resourceStatus {
	s := resourceStatus{}
	s.GeoUpdated, s.GeoNeedsDownload, s.GeoPending = core.GeoResourceStatus(dir, time.Now())
	bin := core.CoreInDir(dir)
	if _, e := os.Stat(bin); e != nil {
		s.CoreNeedsDownload = true
		s.CoreCheck = "未找到内核"
		return s
	}
	version, e := core.BinaryVersionContext(ctx, bin)
	s.CoreVersion = version
	if e != nil {
		s.CoreNeedsDownload = true
		s.CoreCheck = "内核版本读取失败"
		return s
	}
	if probe {
		if e = strictCoreCheck(ctx, bin, filepath.Join(dir, ".protocol-check")); e != nil {
			s.CoreNeedsDownload = true
			s.CoreCheck = e.Error()
		} else {
			s.CoreCheck = "全部协议检查通过"
		}
	}
	return s
}
func strictCoreCheck(ctx context.Context, bin, dir string) error {
	names := []string{protoAWG, protoWG, protoMASQUE, protoMASQUEH2, protoWWarp, protoMASQUEMASQUE, probeOpenVPNProto}
	var failed []string
	for _, name := range names {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		y, e := probeYAML(name)
		if e == nil {
			var ok, ran bool
			ok, ran, e = core.TestConfigDetailContext(ctx, bin, dir, []byte(y))
			if !ok || !ran {
				failed = append(failed, name)
			}
		} else {
			failed = append(failed, name)
		}
	}
	if len(failed) > 0 {
		return fmt.Errorf("不支持或无法验证：%s", strings.Join(failed, "、"))
	}
	return nil
}
func emitResourceStatus(s resourceStatus) {
	b, _ := json.Marshal(s)
	fmt.Println("@@RESOURCES " + string(b))
}
func requireDesktopGeoData(dir string) error {
	if !core.GeoDataReady(dir) {
		return fmt.Errorf("GeoFiles 缺失或不完整，请在关于页下载；连接中只暂存，断开后替换再重试")
	}
	return nil
}
func resourceProxyPort(st clientState, alive, requireVPN bool) (int, error) {
	if !alive {
		if requireVPN {
			return 0, fmt.Errorf("没有已验证的 VPN 连接，未直连下载")
		}
		return 0, nil // A user-requested, disconnected download only.
	}
	if st.Verification != "verified" || st.MixedPort < 1 || st.MixedPort > 65535 {
		return 0, fmt.Errorf("VPN 尚未验证或代理端口无效，未直连下载")
	}
	return st.MixedPort, nil
}
func resourceHTTP(proxyPort int) *http.Client {
	t := &http.Transport{Proxy: nil}
	if proxyPort > 0 {
		u, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", proxyPort))
		t.Proxy = http.ProxyURL(u)
	}
	return &http.Client{Transport: t, Timeout: 5 * time.Minute}
}
func runResourceCommand(parent context.Context, command string) error {
	ctx, cancel := desktopContext(parent)
	defer cancel()
	ctx, timeout := context.WithTimeout(ctx, 6*time.Minute)
	defer timeout()
	dir, e := coreDir()
	if e != nil {
		return e
	}
	switch command {
	case "resources-info", "resources-check-core":
		emitResourceStatus(inspectResources(ctx, dir, command == "resources-check-core"))
		return nil
	case "resources-download-core":
		if core.BinaryRunning(core.CoreInDir(dir)) {
			return fmt.Errorf("请先断开 VPN 再更新内核")
		}
		unlockStage, e := core.ResourceLock(ctx, filepath.Join(dir, ".core-staging"))
		if e != nil {
			return e
		}
		defer unlockStage() // Keep the validated candidate immutable until installed.
		client := resourceHTTP(0)
		defer client.CloseIdleConnections()
		candidate, e := core.DownloadBinaryCandidate(ctx, dir, client)
		if e != nil {
			return e
		}
		if _, e = core.BinaryVersionContext(ctx, candidate); e != nil {
			return e
		}
		if e = strictCoreCheck(ctx, candidate, filepath.Join(dir, ".core-staging", "check")); e != nil {
			return e
		}
		unlock, e := core.ResourceLock(ctx, dir)
		if e != nil {
			return e
		}
		defer unlock()
		if e = core.InstallValidatedBinary(dir, candidate, core.BinaryRunning(core.CoreInDir(dir))); e != nil {
			return e
		}
		emitResourceStatus(inspectResources(ctx, dir, true))
		return nil
	case "resources-download-geo", "resources-download-geo-proxy":
		st, e := loadState()
		if e != nil {
			return e
		}
		alive := st.Connected && core.ProcessMatches(st.CorePID, core.CoreInDir(dir))
		port, e := resourceProxyPort(st, alive, command == "resources-download-geo-proxy")
		if e != nil {
			return e
		}
		client := resourceHTTP(port)
		defer client.CloseIdleConnections()
		if e = core.StageGeoDataOfficial(ctx, dir, client); e != nil {
			return e
		}
		// Stage only. Apply uses the connection workflow's resource lock, after
		// disconnect or before the next connect; never touch an in-use MMDB.
		emitResourceStatus(inspectResources(ctx, dir, false))
		return nil
	case "resources-apply":
		unlock, e := core.ResourceLock(ctx, dir)
		if e != nil {
			return e
		}
		defer unlock()
		if e = core.ApplyPendingGeoData(dir, core.BinaryRunning(core.CoreInDir(dir))); e != nil {
			return e
		}
		emitResourceStatus(inspectResources(ctx, dir, false))
		return nil
	}
	return fmt.Errorf("未知的资源操作")
}
