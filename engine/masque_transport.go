package main

import (
	"context"
	"crypto/tls"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"sync"

	connectip "github.com/Diniboy1123/connect-ip-go"
	"github.com/quic-go/quic-go"
	"github.com/quic-go/quic-go/http3"
	"github.com/yosida95/uritemplate/v3"
	"golang.org/x/net/http2"
)

// Endpoint probes must not inherit HTTP_PROXY or the user's system proxy.
// A nested probe must use the outer netstack, never a host socket.
func dialMasqueTransport(ctx context.Context, cfg *tls.Config, qc *quic.Config, remote net.Addr, h2 bool, via *nest) (*connectip.Conn, func(), error) {
	if h2 {
		tc := cfg.Clone()
		tc.NextProtos = []string{"h2"}
		tr := &http2.Transport{DialTLSContext: func(ctx context.Context, network, _ string, _ *tls.Config) (net.Conn, error) {
			dial := (&net.Dialer{}).DialContext
			if via != nil {
				dial = via.net.DialContext
			}
			c, err := dial(ctx, network, remote.String())
			if err != nil {
				return nil, err
			}
			secure := tls.Client(c, tc)
			if err := secure.HandshakeContext(ctx); err != nil {
				c.Close()
				return nil, err
			}
			return secure, nil
		}}
		headers := http.Header{"User-Agent": {""}, "Cf-Connect-Proto": {"cf-connect-ip"}, "Pq-Enabled": {"false"}}
		c, _, err := connectip.DialH2(ctx, &http.Client{Transport: tr}, uritemplate.MustNew(masqueConnectURI), headers)
		closeAll := func() {
			if c != nil {
				c.Close()
			}
			tr.CloseIdleConnections()
		}
		return c, closeAll, err
	}
	for attempt := 0; ; attempt++ {
		c, closeAll, err := dialMasqueH3(ctx, cfg, qc, remote, via)
		// Preserve the upstream's one retry for transient protocol violations.
		if err == nil || ctx.Err() != nil || via != nil || attempt > 0 || !strings.Contains(err.Error(), "PROTOCOL_VIOLATION") {
			return c, closeAll, err
		}
		closeAll()
	}
}

func dialMasqueH3(ctx context.Context, cfg *tls.Config, qc *quic.Config, remote net.Addr, via *nest) (*connectip.Conn, func(), error) {
	var packet net.PacketConn
	var err error
	if via != nil {
		packet, err = via.net.ListenUDPAddrPort(netip.AddrPortFrom(via.local, 0))
	} else {
		local := net.IPv4zero
		if addr, ok := remote.(*net.UDPAddr); ok && addr.IP.To4() == nil {
			local = net.IPv6zero
		}
		packet, err = net.ListenUDP("udp", &net.UDPAddr{IP: local})
	}
	if err != nil {
		return nil, func() {}, err
	}
	qt := &quic.Transport{Conn: packet, ConnectionIDLength: 20}
	q, err := qt.Dial(ctx, remote, cfg, qc)
	if err != nil {
		qt.Close()
		packet.Close()
		return nil, func() {}, err
	}
	tr := &http3.Transport{EnableDatagrams: true, AdditionalSettings: map[uint64]uint64{0x276: 1}, DisableCompression: true}
	// connect-ip-go's ReadResponse does not observe ctx after the stream was
	// opened. Closing QUIC on cancellation also interrupts that network wait.
	stop := context.AfterFunc(ctx, func() { _ = q.CloseWithError(0, "scan canceled") })
	c, _, err := connectip.Dial(ctx, tr.NewClientConn(q), uritemplate.MustNew(masqueConnectURI), "cf-connect-ip", http.Header{"User-Agent": {""}}, true)
	var once sync.Once
	closeAll := func() {
		once.Do(func() {
			stop()
			_ = q.CloseWithError(0, "closed")
			if c != nil {
				_ = c.Close()
			}
			_ = tr.Close()
			_ = qt.Close()
			_ = packet.Close()
		})
	}
	if err != nil {
		closeAll()
	}
	return c, closeAll, err
}
