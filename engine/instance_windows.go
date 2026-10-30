//go:build windows

package main

import (
	"github.com/lxn/win"
	"golang.org/x/sys/windows"
)

// One client per desktop. The window hides into the tray instead of exiting, so
// launching the exe again must raise that window rather than start a second
// instance — two clients share one mixed port and one health-owner claim, and
// the second one always loses in ways that look like a broken program.
const instanceMutexName = "xehn-single-instance"

var instanceMutex windows.Handle

// claimSingleInstance reports whether this process may run the GUI. A false
// return means another instance owns the mutex and has been asked to show
// itself.
func claimSingleInstance() bool {
	name, err := windows.UTF16PtrFromString(instanceMutexName)
	if err != nil {
		return true
	}
	h, err := windows.CreateMutex(nil, false, name)
	if err != nil && err != windows.ERROR_ALREADY_EXISTS {
		// Without a usable mutex the safe choice is to start: refusing would
		// make the program unlaunchable for a reason the user cannot see.
		return true
	}
	if err == windows.ERROR_ALREADY_EXISTS {
		// The handle is still valid and must be closed on exit, but this
		// process is not the owner.
		if h != 0 {
			windows.CloseHandle(h)
		}
		focusExistingWindow()
		return false
	}
	instanceMutex = h
	return true
}

// focusExistingWindow brings the running client to the front. It finds the
// window by title; if that fails (a different build, a renamed window) it says
// so instead of exiting silently, which would look like the program refusing to
// start.
func focusExistingWindow() {
	title, _ := windows.UTF16PtrFromString("XEHN VPN Client")
	if hwnd := win.FindWindow(nil, title); hwnd != 0 {
		win.ShowWindow(hwnd, win.SW_RESTORE)
		win.SetForegroundWindow(hwnd)
		return
	}
	text, _ := windows.UTF16PtrFromString("XEHN 已在运行（可能在系统托盘中）。\r\n请在托盘图标上点左键打开窗口。")
	caption, _ := windows.UTF16PtrFromString(productName)
	win.MessageBox(0, text, caption, win.MB_OK|win.MB_ICONINFORMATION)
}
