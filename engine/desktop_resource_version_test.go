package main

import (
	"context"
	"errors"
	"testing"
	"time"
)

func TestResourceVersionColdReadRetriesOnce(t *testing.T) {
	for _, fail := range []bool{false, true} {
		calls := 0
		version, err := readResourceVersion(context.Background(), "fixture.exe", func(context.Context, string) (string, error) {
			calls++
			if calls == 1 || fail {
				return "", errors.New("fixture cold read")
			}
			return "1.19.33", nil
		})
		if calls != 2 || (err != nil) != fail || !fail && version != "1.19.33" {
			t.Fatalf("bad retry: %s %v calls=%d", version, err, calls)
		}
	}
}
func TestResourceVersionSuccessfulReadDoesNotRetry(t *testing.T) {
	calls := 0
	version, err := readResourceVersion(context.Background(), "fixture.exe", func(context.Context, string) (string, error) { calls++; return "1.19.33", nil })
	if err != nil || version != "1.19.33" || calls != 1 {
		t.Fatal("successful read repeated")
	}
}
func TestResourceVersionCancellationSkipsRetry(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	calls := 0
	_, err := readResourceVersion(ctx, "fixture.exe", func(context.Context, string) (string, error) { calls++; cancel(); return "", context.Canceled })
	if calls != 1 || !errors.Is(err, context.Canceled) {
		t.Fatal("cancellation retried")
	}
	_, err = readResourceVersion(ctx, "fixture.exe", func(context.Context, string) (string, error) { t.Error("pre-cancelled read executed"); return "", nil })
	if !errors.Is(err, context.Canceled) {
		t.Fatal("pre-cancelled read lost error")
	}
}

func TestResourceVersionRetryWaitIsCancellable(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	calls := 0
	start := time.Now()
	_, err := readResourceVersion(ctx, "fixture.exe", func(context.Context, string) (string, error) {
		calls++
		time.AfterFunc(10*time.Millisecond, cancel)
		return "", errors.New("fixture first launch refused")
	})
	if calls != 1 || !errors.Is(err, context.Canceled) || time.Since(start) > time.Second {
		t.Fatalf("retry wait ignored cancellation: %v calls=%d", err, calls)
	}
}
