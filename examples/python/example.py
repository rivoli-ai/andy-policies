# Copyright (c) Rivoli AI 2026. All rights reserved.
# Example: Using the Andy Policies API from Python

import requests
import urllib3

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

API_URL = "https://localhost:5112"
TOKEN = "YOUR_BEARER_TOKEN"  # Obtain from Andy Auth

headers = {"Authorization": f"Bearer {TOKEN}"}

# List items
response = requests.get(f"{API_URL}/api/items", headers=headers, verify=False)
response.raise_for_status()
items = response.json()
print(f"Found {len(items)} items")

# Create item
payload = {"name": "Example Item", "description": "Created from Python"}
response = requests.post(f"{API_URL}/api/items", json=payload, headers=headers, verify=False)
response.raise_for_status()
created = response.json()
print(f"Created: {created['name']} ({created['id']})")
