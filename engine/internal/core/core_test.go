package core

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestNewValidatesConfig(t *testing.T) {
	if _, err := New(Config{}); err == nil {
		t.Fatal("empty config should error")
	}
	if _, err := New(Config{BinaryPath: "x"}); err == nil {
		t.Fatal("missing WorkDir should error")
	}
}

func TestWriteConfigAtomic(t *testing.T) {
	dir := t.TempDir()
	c, err := New(Config{BinaryPath: filepath.Join(dir, "mihomo.exe"), WorkDir: dir})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	body := []byte("mixed-port: 7890\nrules:\n  - MATCH,DIRECT\n")
	if err := c.WriteConfig(body); err != nil {
		t.Fatalf("write: %v", err)
	}
	got, err := os.ReadFile(c.ConfigPath())
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if string(got) != string(body) {
		t.Fatalf("config body mismatch")
	}
}

func TestRunningFalse(t *testing.T) {
	dir := t.TempDir()
	c, _ := New(Config{BinaryPath: filepath.Join(dir, "mihomo.exe"), WorkDir: dir})
	if c.Running() {
		t.Fatal("Running should be false before Start")
	}
}

func TestWaitReadyTimeout(t *testing.T) {
	dir := t.TempDir()
	c, _ := New(Config{BinaryPath: filepath.Join(dir, "mihomo.exe"), WorkDir: dir, APIPort: 1})
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	if err := c.WaitReady(ctx); err == nil {
		t.Fatal("WaitReady on dead controller should timeout")
	}
}
