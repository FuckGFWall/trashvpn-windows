package main

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"net"
	"strings"
	"testing"
	"time"
)

func TestDesktopPayloadCancellationClosesProxyConnection(t *testing.T) {
	// An isolated CONNECT sink: no forwarding, internet or system proxy writes.
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	ready, stopped := make(chan struct{}), make(chan struct{})
	go func() {
		defer close(stopped)
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		defer conn.Close()
		_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
		reader := bufio.NewReader(conn)
		for {
			line, err := reader.ReadString('\n')
			if err != nil {
				return
			}
			if strings.TrimSpace(line) == "" {
				break
			}
		}
		fmt.Fprint(conn, "HTTP/1.1 200 Connection Established\r\n\r\n")
		close(ready)
		// TLS has no peer here, so only cancellation / timeout can finish it.
		_, _ = io.Copy(io.Discard, reader)
	}()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() {
		_, err := probeThroughputContext(ctx, listener.Addr().(*net.TCPAddr).Port, verifyPayloadBytes, verifyPayloadTimeout)
		done <- err
	}()
	select {
	case <-ready:
	case <-time.After(2 * time.Second):
		t.Fatal("CONNECT sink not reached")
	}
	start := time.Now()
	cancel()
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("incomplete transfer counted as success")
		}
	case <-time.After(time.Second):
		t.Fatal("payload cancellation waited for full request timeout")
	}
	select {
	case <-stopped:
	case <-time.After(time.Second):
		t.Fatal("cancelled request leaked its proxy connection")
	}
	if time.Since(start) > time.Second {
		t.Fatal("slow cancellation")
	}
}
