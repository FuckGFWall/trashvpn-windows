package main

import (
	"context"
	"net"

	"github.com/amnezia-vpn/amneziawg-go/tun/netstack"
)

type cancelableDNSConn struct {
	net.Conn
	finish func()
}

func (c *cancelableDNSConn) Close() error { c.finish(); return nil }

// net.Resolver distinguishes DNS datagrams from length-prefixed DNS streams
// using net.PacketConn, not the network argument passed to Dial. Preserve the
// underlying UDP interface as well as cancellation; TCP must remain a stream.
type cancelablePacketDNSConn struct {
	*cancelableDNSConn
	packet net.PacketConn
}

func (c *cancelablePacketDNSConn) ReadFrom(p []byte) (int, net.Addr, error) {
	return c.packet.ReadFrom(p)
}
func (c *cancelablePacketDNSConn) WriteTo(p []byte, a net.Addr) (int, error) {
	return c.packet.WriteTo(p, a)
}

func wrapDNSConn(ctx context.Context, c net.Conn) net.Conn {
	wrapped := &cancelableDNSConn{Conn: c, finish: closeOnCancel(ctx, c)}
	if packet, ok := c.(net.PacketConn); ok {
		return &cancelablePacketDNSConn{cancelableDNSConn: wrapped, packet: packet}
	}
	return wrapped
}

// The dependency's LookupContextHost sets a deadline on UDP, but does not close
// the socket when its context is canceled. Keep DNS inside the tunnel while
// making cancellation interrupt the actual read, not just the outer caller.
func lookupTunnelHost(ctx context.Context, tnet *netstack.Net, host string) ([]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	resolver := &net.Resolver{PreferGo: true, Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		c, err := tnet.DialContext(ctx, network, net.JoinHostPort(tunnelDNS, "53"))
		if err != nil {
			return nil, err
		}
		return wrapDNSConn(ctx, c), nil
	}}
	return resolver.LookupHost(ctx, host)
}
