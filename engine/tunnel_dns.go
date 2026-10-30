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
		return &cancelableDNSConn{Conn: c, finish: closeOnCancel(ctx, c)}, nil
	}}
	return resolver.LookupHost(ctx, host)
}
