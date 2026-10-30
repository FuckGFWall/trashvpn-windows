package main

import (
	"os"
	"testing"

	"github.com/xhwarp/warp-client/internal/core"
)

// TestProbeConfigsAccepted feeds every protocol's capability-probe config to a
// real mihomo and requires it to parse. The UI's protocol switch is gated on
// exactly this probe (see protoOK / probeCoreCapabilities), so a probe config
// that drifts from what mihomo accepts shows up as "clicking that protocol does
// nothing" — this test catches the drift instead of the user.
//
// Gated on WARP_TEST_MIHOMO like the other real-binary tests:
//
//	$env:WARP_TEST_MIHOMO='E:\path\to\mihomo.exe'
//	go test -run TestProbeConfigsAccepted -v .
func TestProbeConfigsAccepted(t *testing.T) {
	bin := os.Getenv("WARP_TEST_MIHOMO")
	if bin == "" {
		t.Skip("set WARP_TEST_MIHOMO to a mihomo binary to run this test")
	}
	dir := t.TempDir()
	unsupported := map[string]string{}
	names := make([]string, 0, len(protoRuns)+1)
	for _, r := range protoRuns {
		names = append(names, r.name)
	}
	names = append(names, probeOpenVPNProto)
	for _, name := range names {
		yaml, err := probeYAML(name)
		if err != nil {
			unsupported[name] = "probeYAML: " + err.Error()
			continue
		}
		if err := core.TestConfig(bin, dir, []byte(yaml)); err != nil {
			unsupported[name] = err.Error()
			t.Logf("--- %s probe config ---\n%s", name, yaml)
			continue
		}
		t.Logf("%s: OK", name)
	}
	if len(unsupported) > 0 {
		for name, why := range unsupported {
			t.Errorf("%s is reported UNSUPPORTED by the probe: %s", name, why)
		}
	}
}
