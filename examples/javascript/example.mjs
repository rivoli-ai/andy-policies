// Copyright (c) Rivoli AI 2026. All rights reserved.
// Example: Using the Andy Policies API from JavaScript (Node.js)

const API_URL = "https://localhost:5112";
const TOKEN = "YOUR_BEARER_TOKEN"; // Obtain from Andy Auth

process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0"; // Dev only

const headers = {
  Authorization: `Bearer ${TOKEN}`,
  "Content-Type": "application/json",
};

// List items
const listResponse = await fetch(`${API_URL}/api/items`, { headers });
const items = await listResponse.json();
console.log(`Found ${items.length} items`);

// Create item
const createResponse = await fetch(`${API_URL}/api/items`, {
  method: "POST",
  headers,
  body: JSON.stringify({ name: "Example Item", description: "Created from JavaScript" }),
});
const created = await createResponse.json();
console.log(`Created: ${created.name} (${created.id})`);
