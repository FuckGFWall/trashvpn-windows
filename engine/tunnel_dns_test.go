package main

import (
	"context"
	"encoding/binary"
	"net"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"

	"golang.org/x/net/dns/dnsmessage"
)

func dnsFixtureReply(query []byte) []byte {
	var req dnsmessage.Message
	if req.Unpack(query) != nil || len(req.Questions) != 1 {
		return nil
	}
	q := req.Questions[0]
	resp := dnsmessage.Message{Header: dnsmessage.Header{ID: req.ID, Response: true, RecursionAvailable: true}, Questions: req.Questions}
	if q.Type == dnsmessage.TypeA {
		resp.Answers = []dnsmessage.Resource{{Header: dnsmessage.ResourceHeader{Name: q.Name, Class: dnsmessage.ClassINET, Type: dnsmessage.TypeA, TTL: 60}, Body: &dnsmessage.AResource{A: [4]byte{192, 0, 2, 10}}}}
	}
	wire, _ := resp.Pack()
	return wire
}

func TestCancelableDNSPreservesUDPAndResolves(t *testing.T) {
	server, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan struct{})
	var invalid atomic.Int32
	go func() {
		defer close(done)
		buf := make([]byte, 4096)
		for {
			n, addr, err := server.ReadFromUDP(buf)
			if err != nil {
				return
			}
			wire := dnsFixtureReply(buf[:n])
			if wire == nil {
				invalid.Add(1)
				continue
			}
			_, _ = server.WriteToUDP(wire, addr)
		}
	}()
	defer func() { _ = server.Close(); <-done }()
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	resolver := &net.Resolver{PreferGo: true, Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
		c, err := (&net.Dialer{}).DialContext(ctx, network, server.LocalAddr().String())
		if err != nil {
			return nil, err
		}
		return wrapDNSConn(ctx, c), nil
	}}
	ips, err := resolver.LookupHost(ctx, "wire-format.test.")
	if err != nil || len(ips) != 1 || ips[0] != "192.0.2.10" || invalid.Load() != 0 {
		t.Fatalf("UDP DNS broken: ips=%v err=%v malformed=%d", ips, err, invalid.Load())
	}
}

func TestTunnelDNSResolvesThroughRealNetstack(t *testing.T) {
	dev, stack, err := newIPStack([]netip.Addr{netip.MustParseAddr("192.0.2.2")}, 1280)
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		bufs, sizes := [][]byte{make([]byte, 65535)}, []int{0}
		for {
			if _, err := dev.Read(bufs, sizes, 0); err != nil {
				return
			}
			packet := bufs[0][:sizes[0]]
			if len(packet) < 28 || packet[0]>>4 != 4 || packet[9] != 17 {
				continue
			}
			h := int(packet[0]&15) * 4
			if len(packet) < h+8 || binary.BigEndian.Uint16(packet[h+2:h+4]) != 53 {
				continue
			}
			wire := dnsFixtureReply(packet[h+8:])
			if wire == nil {
				continue
			}
			reply := make([]byte, 28+len(wire))
			reply[0], reply[8], reply[9] = 0x45, 64, 17
			binary.BigEndian.PutUint16(reply[2:4], uint16(len(reply)))
			copy(reply[12:16], packet[16:20])
			copy(reply[16:20], packet[12:16])
			var sum uint32
			for i := 0; i < 20; i += 2 {
				sum += uint32(binary.BigEndian.Uint16(reply[i : i+2]))
			}
			for sum>>16 != 0 {
				sum = sum&0xffff + sum>>16
			}
			binary.BigEndian.PutUint16(reply[10:12], ^uint16(sum))
			copy(reply[20:22], packet[h+2:h+4])
			copy(reply[22:24], packet[h:h+2])
			binary.BigEndian.PutUint16(reply[24:26], uint16(len(wire)+8))
			copy(reply[28:], wire) // IPv4 permits an omitted UDP checksum.
			_, _ = dev.Write([][]byte{reply}, 0)
		}
	}()
	defer func() { _ = dev.Close(); <-done }()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	ips, err := lookupTunnelHost(ctx, stack.tnet, "netstack-wire.test.")
	if err != nil || len(ips) != 1 || ips[0] != "192.0.2.10" {
		t.Fatalf("netstack DNS failed: %v %v", ips, err)
	}
}

func TestCancelableDNSPreservesTransportInterfaces(t *testing.T) {
	u, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	udp := wrapDNSConn(context.Background(), u)
	defer udp.Close()
	if _, ok := udp.(net.PacketConn); !ok {
		t.Error("UDP packet semantics hidden; Go resolver would add a TCP length prefix")
	}
	a, b := net.Pipe()
	defer b.Close()
	tcp := wrapDNSConn(context.Background(), a)
	defer tcp.Close()
	if _, ok := tcp.(net.PacketConn); ok {
		t.Error("TCP must not advertise datagram semantics")
	}
}
