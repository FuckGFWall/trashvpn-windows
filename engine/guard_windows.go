//go:build windows

package main

import (
	"os/exec"
	"syscall"

	"golang.org/x/sys/windows"
)

// hideGuard keeps the detached health guard invisible: a console-subprocess
// child of the windowsgui build would otherwise flash a black window, and the
// guard is spawned during the very click the user is watching.
func hideGuard(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: windows.CREATE_NO_WINDOW,
	}
}

// processAlive reports whether pid is still running. A guard killed outright
// never gets to release its claim, so liveness - not the claim's mere presence,
// not its age - is what decides whether a successor may take over.
func processAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	h, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		return false // already gone, or not ours to see
	}
	defer windows.CloseHandle(h)
	// A signalled handle means the process has exited, whatever OpenProcess
	// thought. TerminateProcess-refusing-but-finished processes land here too.
	event, err := windows.WaitForSingleObject(h, 0)
	return err == nil && event != windows.WAIT_OBJECT_0
}
