//go:build !windows

package core

import (
	"context"
	"os"
	"sync"
)

var resourceLocks sync.Map

func lockResourceFile(ctx context.Context, file *os.File) (func(), error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	value, _ := resourceLocks.LoadOrStore(file.Name(), make(chan struct{}, 1))
	gate := value.(chan struct{})
	select {
	case gate <- struct{}{}:
		return func() { <-gate }, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}
