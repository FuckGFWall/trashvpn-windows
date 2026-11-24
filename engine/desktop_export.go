package main

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/netip"
	"os"
	"sort"
)

// Read-only export: never register, scan, start a core, or touch settings/state.
// The structured result carries credentials and MUST NOT be forwarded to logs.
func runDesktopExport(ctx context.Context, _ options) error {
	req := struct {
		clientSettings
		Format string `json:"export_format"`
	}{clientSettings: defaultSettings()}
	var config []byte
	err := json.Unmarshal([]byte(os.Getenv("WARP_DESKTOP_SETTINGS")), &req)
	if err == nil {
		config, err = exportCachedNode(req.clientSettings, req.Format)
	}
	if ctx.Err() != nil {
		err = ctx.Err()
	}
	result := map[string]string{}
	if err != nil {
		result["error"] = err.Error()
	} else {
		result["config"] = string(config)
	}
	raw, err := json.Marshal(result)
	if err != nil {
		return fmt.Errorf("无法编码导出结果")
	}
	fmt.Println("@@NODE_EXPORT " + string(raw))
	return nil
}

func exportCachedNode(s clientSettings, format string) ([]byte, error) {
	if format != "clash" && format != "xray" && format != "singbox" {
		return nil, fmt.Errorf("未知的导出格式")
	}
	if format != "clash" && s.Proto != protoWG {
		return nil, fmt.Errorf("此格式目前仅提供 WireGuard 导出；所选协议请使用 Clash（Mihomo）")
	}
	if s.Psiphon || s.OpenVPN {
		return nil, fmt.Errorf("附加出口模式暂不支持节点导出")
	}
	path, err := accountPath()
	if err != nil {
		return nil, fmt.Errorf("无法定位本地账号")
	}
	a, err := loadAccount(path)
	if err != nil || !accountReadyFor(a, s.Proto) {
		return nil, fmt.Errorf("所选协议尚未注册完成，请先等待首页准备完成")
	}
	// Existing renderers read account globals. This command has its own process;
	// restore them too, so offline in-process callers remain isolated.
	previous := account{PrivateKey: warpPrivateKey, PeerPublicKey: warpPublicKey, IPv4: warpAddress, IPv6: warpAddressV6, Masque: masqueAcct, Outer: outerAcct}
	previousOuter := outer
	applyAccount(a)
	outer = nil
	defer func() { applyAccount(previous); outer = previousOuter }()
	o := desktopConnectOptions(s)
	if protoChained(s.Proto) && o.ipFamily() != famV4 {
		return nil, fmt.Errorf("嵌套协议仅支持 IPv4 导出")
	}
	run, err := parseProto(s.Proto)
	if err != nil {
		return nil, fmt.Errorf("未知的隧道协议")
	}
	if s.Proto == protoWWarp || s.Proto == protoMASQUEMASQUE {
		carrier, inner := s.OuterProto, s.InnerProto
		if s.Proto == protoMASQUEMASQUE {
			carrier, inner = protoMASQUE, protoMASQUE
		}
		outerRun, err := parseProto(carrier)
		if err != nil || (s.Proto == protoWWarp && carrier != protoWG && carrier != protoAWG) {
			return nil, fmt.Errorf("外层协议无效")
		}
		if s.Proto == protoWWarp && inner != protoWG && inner != protoAWG {
			return nil, fmt.Errorf("内层协议无效")
		}
		outerEndpoints, err := cachedExportEndpoints(carrier, o.ipv6, o)
		if err != nil {
			return nil, fmt.Errorf("外层节点缓存不可用，请先等待该协议优选完成")
		}
		outerEndpoint := outerEndpoints[0]
		o.proto, o.innerProto, o.through = carrier, inner, outerEndpoint
		outer = &nest{run: outerRun, endpoint: outerEndpoint, label: outerEndpoint}
		run, _ = parseProto(inner)
	}
	endpoints, err := cachedExportEndpoints(scanKeyFor(o), o.ipv6, o)
	if err != nil {
		return nil, err
	}
	v4, v6 := a.IPv4, a.IPv6
	if run.isMASQUE() {
		v4, v6 = a.Masque.IPv4, a.Masque.IPv6
	}
	if _, err = exportAddresses(v4, v6, o.ipFamily()); err != nil {
		return nil, err
	}
	if format == "clash" {
		if outer != nil && !outer.run.isMASQUE() {
			if _, err := exportAddresses(a.Outer.IPv4, a.Outer.IPv6, o.ipFamily()); err != nil {
				return nil, err
			}
		}
		config, err := renderCachedMihomoExport(o, endpoints, run)
		if err != nil {
			return nil, fmt.Errorf("本地账号或节点数据不完整，无法导出 Clash 配置")
		}
		return config, nil
	}
	return renderWireGuardExport(o, endpoints, a, format)
}

// Export every distinct working endpoint in a fresh cache, not just the best
// or pinned connection choice. Invalid entries must not hide other good nodes.
func cachedExportEndpoints(proto string, ipv6 bool, policy ...options) ([]string, error) {
	cs, ok := cachedScanFor(proto, ipv6, policy...)
	if !ok {
		return nil, fmt.Errorf("暂无有效节点缓存，请先等待所选协议优选完成")
	}
	// Rank a copy: cachedScanFor has decoded private data, but keeping the sort
	// local also makes this helper safe if cache storage becomes shared later.
	results := append([]cachedEndpoint(nil), cs.Results...)
	sort.SliceStable(results, func(i, j int) bool { return betterEndpoint(results[i], results[j]) })
	seen := map[string]bool{}
	var endpoints []string
	for _, result := range results {
		if !result.Working {
			continue
		}
		ap, err := netip.ParseAddrPort(result.Endpoint)
		if err != nil || ap.Port() == 0 || seen[ap.String()] {
			continue
		}
		seen[ap.String()] = true
		endpoints = append(endpoints, ap.String())
	}
	if len(endpoints) == 0 {
		return nil, fmt.Errorf("缓存中没有可导出的节点，请重新优选")
	}
	return endpoints, nil
}

func renderCachedMihomoExport(o options, endpoints []string, run protoRun) ([]byte, error) {
	var proxies [][]kv
	seen := map[string]bool{}
	for _, endpoint := range endpoints {
		nodes, err := mihomoProxies(o, endpoint, run)
		if err != nil {
			return nil, err
		}
		// A chain shares one outer carrier; rename both definitions and their
		// references before merging so every inner node keeps the correct hop.
		names := map[quoted]quoted{}
		for i, node := range nodes {
			address := endpoint
			if outer != nil && i == 0 {
				address = outer.endpoint
			}
			for _, field := range node {
				if field.k == "name" {
					name := field.v.(quoted)
					names[name] = quoted(string(name) + " | " + address)
				}
			}
		}
		for _, node := range nodes {
			name := ""
			for i := range node {
				if node[i].k == "name" || node[i].k == "dialer-proxy" {
					node[i].v = names[node[i].v.(quoted)]
				}
				if node[i].k == "name" {
					name = string(node[i].v.(quoted))
				}
			}
			if !seen[name] {
				seen[name] = true
				proxies = append(proxies, node)
			}
		}
	}
	return []byte("proxies:\n" + renderProxiesYAML(proxies)), nil
}

func exportAddresses(v4, v6, family string) ([]string, error) {
	var result []string
	for _, f := range []struct {
		ip       string
		bits     int
		required bool
	}{
		{v4, 32, family != famV6}, {v6, 128, family != famV4},
	} {
		if !f.required {
			continue
		}
		a, err := netip.ParseAddr(f.ip)
		if err != nil || (f.bits == 32) != a.Is4() {
			return nil, fmt.Errorf("账号缺少所选地址族的隧道地址，无法导出")
		}
		result = append(result, netip.PrefixFrom(a, f.bits).String())
	}
	return result, nil
}

// Export mergeable node sections, not host ports, routing rules or API secrets.
// Xray: https://xtls.github.io/config/outbounds/wireguard.html
// sing-box 1.11+: https://sing-box.sagernet.org/configuration/endpoint/wireguard/
func renderWireGuardExport(o options, endpoints []string, a account, format string) ([]byte, error) {
	for _, key := range []string{a.PrivateKey, a.PeerPublicKey} {
		decoded, err := base64.StdEncoding.DecodeString(key)
		if err != nil || len(decoded) != 32 {
			return nil, fmt.Errorf("WireGuard 密钥格式不正确，无法导出")
		}
	}
	addresses, err := exportAddresses(a.IPv4, a.IPv6, o.ipFamily())
	if err != nil {
		return nil, err
	}
	mtu := o.mtu
	if mtu <= 0 {
		mtu = tunnelMTU
	}
	var nodes []any
	for _, endpoint := range endpoints {
		ap, err := netip.ParseAddrPort(endpoint)
		if err != nil || ap.Port() == 0 {
			return nil, fmt.Errorf("节点地址无效")
		}
		name := "XEHN-WARP | " + endpoint
		if format == "xray" {
			nodes = append(nodes, map[string]any{
				"tag": name, "protocol": "wireguard", "settings": map[string]any{
					"secretKey": a.PrivateKey, "address": addresses, "mtu": mtu, "noKernelTun": true,
					"peers": []any{map[string]any{"endpoint": endpoint, "publicKey": a.PeerPublicKey, "keepAlive": keepalive, "allowedIPs": allowedIPsList(o.ipFamily())}},
				},
			})
		} else if format == "singbox" {
			nodes = append(nodes, map[string]any{
				"type": "wireguard", "tag": name, "system": false, "mtu": mtu, "address": addresses, "private_key": a.PrivateKey,
				"peers": []any{map[string]any{"address": ap.Addr().String(), "port": ap.Port(), "public_key": a.PeerPublicKey, "persistent_keepalive_interval": keepalive, "allowed_ips": allowedIPsList(o.ipFamily())}},
			})
		} else {
			return nil, fmt.Errorf("未知的导出格式")
		}
	}
	section := "outbounds"
	if format == "singbox" {
		section = "endpoints"
	}
	return json.MarshalIndent(map[string]any{section: nodes}, "", "  ")
}
