package main

import "encoding/json"

type metaResult struct {
	ip       string
	loc      string
	colo     string
	coloCity string
	coloISO  string
}

func parseMeta(body string) metaResult {
	var m struct {
		ClientIP string `json:"clientIp"`
		Country  string `json:"country"`
		Colo     struct {
			IATA string `json:"iata"`
			City string `json:"city"`
			CCA2 string `json:"cca2"`
		} `json:"colo"`
	}
	if err := json.Unmarshal([]byte(body), &m); err != nil {
		return metaResult{}
	}
	return metaResult{ip: m.ClientIP, loc: m.Country, colo: m.Colo.IATA, coloCity: m.Colo.City, coloISO: m.Colo.CCA2}
}
