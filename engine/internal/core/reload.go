package core

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

// ReloadConfig keeps the process and selected endpoint; it never starts a core.
// Read back TUN because mihomo can acknowledge a reload without enabling it.
func ReloadConfig(ctx context.Context, port int, payload []byte, tun bool) error {
	client := httpClient(15 * time.Second)
	defer client.CloseIdleConnections()
	body, err := json.Marshal(map[string]string{"payload": string(payload)})
	if err != nil {
		return err
	}
	url := fmt.Sprintf("http://127.0.0.1:%d/configs", port)
	req, err := http.NewRequestWithContext(ctx, http.MethodPut, url+"?force=true", bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))
	resp.Body.Close()
	if resp.StatusCode != http.StatusNoContent {
		return fmt.Errorf("core reload: HTTP %d", resp.StatusCode)
	}
	req, err = http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	resp, err = client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("core readback: HTTP %d", resp.StatusCode)
	}
	var current struct {
		Tun *struct {
			Enable *bool `json:"enable"`
		} `json:"tun"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&current); err != nil {
		return err
	}
	if current.Tun == nil || current.Tun.Enable == nil || *current.Tun.Enable != tun {
		return fmt.Errorf("core reload: TUN 状态未生效")
	}
	return nil
}
