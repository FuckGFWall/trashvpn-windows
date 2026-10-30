//go:build !windows

package main

import (
	"os/exec"
	"syscall"
)

// hideGuard is a no-op off Windows: there is no console window to suppress.
func hideGuard(cmd *exec.Cmd) {
	cmd.SysProcAttr = nil
}

// processAlive reports whether pid is still running, signal-zero style: asking
// for nothing and reading the error says everything about the process.
func processAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	return syscall.Kill(pid, 0) == nil
}
