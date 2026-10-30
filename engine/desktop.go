package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"

	"github.com/xhwarp/warp-client/internal/core"
)

// The CLI engine has no UI language state. Keep the two node-class labels Chinese.
func tr(s string) string { return s }

func runGUICmd(context.Context, options) error {
	return fmt.Errorf("请运行 Xehn.App.exe；此文件仅为本地连接引擎")
}

func runDesktopConnect(parent context.Context, opts options) error {
	return runDesktopOperation(parent, false)
}
func runDesktopPrepare(parent context.Context, opts options) error {
	return runDesktopOperation(parent, true)
}
func runDesktopOperation(parent context.Context, prepare bool) error {
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	go func() {
		scanner := bufio.NewScanner(os.Stdin)
		for scanner.Scan() {
			if scanner.Text() == "cancel" {
				cancel()
				return
			}
		}
		cancel()
	}()
	s := loadSettings()
	if raw := os.Getenv("WARP_DESKTOP_SETTINGS"); raw != "" {
		if err := json.Unmarshal([]byte(raw), &s); err != nil {
			return fmt.Errorf("desktop settings: %w", err)
		}
	}
	o := desktopConnectOptions(s)
	o.prepareOnly = prepare
	if prepare {
		o.tunMode = false
	}
	// Preparation already fulfilled a requested fresh scan; connect consumes it.
	if !prepare && os.Getenv("WARP_PREPARED") == "1" {
		o.scanNow = false
	}
	previousStage := connectStage
	connectStage = func(s string) { fmt.Println(s) }
	desktopEvents = true
	defer func() { connectStage = previousStage; desktopEvents = false }()
	if err := ctx.Err(); err != nil {
		return err
	}
	if !prepare && s.ProxyMode == modeBypass {
		dir, err := coreDir()
		if err != nil {
			return err
		}
		if !core.GeoDataReady(dir) {
			if err = core.DownloadGeoData(ctx, dir, func(s string) { fmt.Println(s) }); err != nil {
				return err
			}
		}
	}
	err := runConnectCmd(ctx, o)
	if err != nil {
		desktopConnection("disconnected")
		if prepare {
			desktopProgress("error", "准备失败，请查看提示", 0, 1)
		}
		if !prepare {
			_ = runDisconnectCmd(context.Background(), o)
		}
	} else if !prepare {
		desktopConnection("verified")
	}
	return err
}

// Keep desktop preference mapping testable without registering or connecting.
func desktopConnectOptions(s clientSettings) options {
	o := defaultConnectOptions(s.Proto, s.CaptureMode == captureTun, s.Rescan)
	s.apply(&o)
	o.desktop = true
	o.optimistic = !s.VerifyBeforeConnect
	applyScanIntensity(&o, s.ScanIntensity)
	o.pick = s.Pin
	o.outerProto, o.innerProto = s.OuterProto, s.InnerProto
	o.psiphonMode, o.openvpnMode = s.Psiphon, s.OpenVPN
	return o
}

func runGeoCheck(context.Context, options) error {
	dir, err := coreDir()
	if err != nil {
		return err
	}
	if !core.GeoDataReady(dir) {
		return fmt.Errorf("GeoIP / GeoSite 缺失或文件过小，请下载缺失数据库")
	}
	fmt.Println("GeoIP / GeoSite 本地文件完整性初检通过（大小检查，非版本或内容校验）")
	return nil
}
func runGeoRepair(ctx context.Context, _ options) error {
	dir, err := coreDir()
	if err != nil {
		return err
	}
	return core.DownloadGeoData(ctx, dir, func(s string) { fmt.Println(s) })
}
