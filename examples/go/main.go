// Copyright (c) Rivoli AI 2026. All rights reserved.
// Example: Using the Andy Policies API from Go

package main

import (
	"bytes"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
)

const (
	apiURL = "https://localhost:5112"
	token  = "YOUR_BEARER_TOKEN" // Obtain from Andy Auth
)

func main() {
	client := &http.Client{
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{InsecureSkipVerify: true}, // Dev only
		},
	}

	// List items
	req, _ := http.NewRequest("GET", apiURL+"/api/items", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := client.Do(req)
	if err != nil {
		panic(err)
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	fmt.Printf("Items: %s\n", body)

	// Create item
	payload, _ := json.Marshal(map[string]string{
		"name":        "Example Item",
		"description": "Created from Go",
	})
	req, _ = http.NewRequest("POST", apiURL+"/api/items", bytes.NewBuffer(payload))
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Content-Type", "application/json")
	resp, err = client.Do(req)
	if err != nil {
		panic(err)
	}
	defer resp.Body.Close()
	body, _ = io.ReadAll(resp.Body)
	fmt.Printf("Created: %s\n", body)
}
