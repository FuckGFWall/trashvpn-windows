package main

import (
	"os"
	"testing"
)

// TestRenderPsiphonConfig renders the V1 Psiphon-mode mihomo config to a file
// so it can be validated with `mihomo -t` against the real binary. It is a
// verification helper, not a behavioral test. It exercises the production
// two-axis shape: a real base-protocol endpoint plus the Psiphon socks5 exit,
// with the loop-guard rules that keep Psiphon's own dials off its own exit.
func TestRenderPsiphonConfig(t *testing.T) {
	opts := options{
		psiphonMode:      true,
		psiphonSocksPort: 43721,
		mixedPort:        7890,
		apiPort:          9090,
		ipv6:             false,
	}
	b, err := renderClientConfig(opts, cachedEndpoint{Endpoint: "162.159.192.1:2408"}, protoRun{kind: kindWG, name: protoWG})
	if err != nil {
		t.Fatalf("renderClientConfig: %v", err)
	}
	const path = "psiphon-test-config.yaml"
	if err := os.WriteFile(path, b, 0o644); err != nil {
		t.Fatal(err)
	}
	t.Logf("wrote %s:\n%s", path, string(b))
}
