//go:build windows

package main

import (
	"log"
	"os"

	"github.com/charmbracelet/bubbles/spinner"
	"golang.org/x/sys/windows"
)

// Consolas and the raster console fonts carry no glyph for the check mark, the
// ballot X or the braille spinner - the legacy console draws a box instead.
const (
	glyphOK   = "+"
	glyphFail = "x"
)

var scanSpinner = spinner.Line

var procAttachConsole = windows.NewLazySystemDLL("kernel32.dll").NewProc("AttachConsole")

// attachParentConsole gives the single windowsgui binary usable CLI output.
// cmd.exe and PowerShell start a GUI-subsystem child with no console, so every
// subcommand's prints would vanish; attach to the caller's console and reopen
// the standard streams. A double-click has no parent console and falls through
// to the GUI's log-file redirection instead.
func attachParentConsole() {
	if len(os.Args) < 2 {
		return
	}
	switch os.Args[1] {
	case "guard", "watchdog", "proxy-helper":
		// Internal children stay detached: attaching them to the caller's
		// terminal would let a Ctrl+C there kill a long-lived guard.
		return
	}
	if stdStreamOpen(os.Stdout) && stdStreamOpen(os.Stderr) {
		return
	}
	const attachParentProcess = ^uint32(0)
	if r, _, _ := procAttachConsole.Call(uintptr(attachParentProcess)); r == 0 {
		return
	}
	if !stdStreamOpen(os.Stdout) {
		if f, err := os.OpenFile("CONOUT$", os.O_WRONLY, 0); err == nil {
			os.Stdout = f
		}
	}
	if !stdStreamOpen(os.Stderr) {
		if f, err := os.OpenFile("CONOUT$", os.O_WRONLY, 0); err == nil {
			os.Stderr = f
			log.SetOutput(f)
		}
	}
	if !stdStreamOpen(os.Stdin) {
		if f, err := os.OpenFile("CONIN$", os.O_RDONLY, 0); err == nil {
			os.Stdin = f
		}
	}
}

func stdStreamOpen(f *os.File) bool {
	_, err := f.Stat()
	return err == nil
}

func enableVirtualTerminal() {
	for _, f := range []*os.File{os.Stdout, os.Stderr} {
		h := windows.Handle(f.Fd())
		var mode uint32
		if windows.GetConsoleMode(h, &mode) != nil {
			continue
		}
		_ = windows.SetConsoleMode(h, mode|windows.ENABLE_VIRTUAL_TERMINAL_PROCESSING)
	}
}
