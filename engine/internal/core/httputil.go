package core

import (
	"net"
	"net/http"
	"time"
)

func httpClient(timeout time.Duration) *http.Client {
	return &http.Client{
		Timeout: timeout,
		Transport: &http.Transport{
			DialContext: (&net.Dialer{Timeout: timeout}).DialContext,
		},
	}
}
