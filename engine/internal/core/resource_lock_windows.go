//go:build windows

package core

import (
	"context"
	"os"
	"time"

	"golang.org/x/sys/windows"
)

func lockResourceFile(ctx context.Context, file *os.File) (func(), error) {
	var overlap windows.Overlapped
	for {
		err := windows.LockFileEx(windows.Handle(file.Fd()), windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, &overlap)
		if err == nil {
			return func() { _ = windows.UnlockFileEx(windows.Handle(file.Fd()), 0, 1, 0, &overlap) }, nil
		}
		if err != windows.ERROR_LOCK_VIOLATION {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(25 * time.Millisecond):
		}
	}
}
