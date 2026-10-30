//go:build !windows

package core

import "errors"

type ProxySnapshot struct{}

func SetSystemProxy(port int) (*ProxySnapshot, error) {
	return nil, errors.New("core: system proxy is only supported on Windows")
}

func UnsetSystemProxy(*ProxySnapshot) error { return nil }

func SystemProxyEnabledAt(port int) bool { return false }

func ClearSystemProxy() error { return nil }

func DetectProxyBlocker() string { return "" }

func IsElevated() bool { return false }

func SetSystemProxyElevated(port int) error {
	return errors.New("core: elevated proxy helper is only supported on Windows")
}

func RestoreSystemProxyElevated(*ProxySnapshot) error { return nil }

func RunProxyHelper([]string) int { return 2 }
