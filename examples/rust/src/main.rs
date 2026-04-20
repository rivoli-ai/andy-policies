// Copyright (c) Rivoli AI 2026. All rights reserved.
// Example: Using the Andy Policies API from Rust

use reqwest::header::{AUTHORIZATION, CONTENT_TYPE};

const API_URL: &str = "https://localhost:5112";
const TOKEN: &str = "YOUR_BEARER_TOKEN"; // Obtain from Andy Auth

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let client = reqwest::Client::builder()
        .danger_accept_invalid_certs(true) // Dev only
        .build()?;

    // List items
    let items: serde_json::Value = client
        .get(format!("{API_URL}/api/items"))
        .header(AUTHORIZATION, format!("Bearer {TOKEN}"))
        .send()
        .await?
        .json()
        .await?;
    println!("Items: {items}");

    // Create item
    let payload = serde_json::json!({
        "name": "Example Item",
        "description": "Created from Rust"
    });
    let created: serde_json::Value = client
        .post(format!("{API_URL}/api/items"))
        .header(AUTHORIZATION, format!("Bearer {TOKEN}"))
        .header(CONTENT_TYPE, "application/json")
        .json(&payload)
        .send()
        .await?
        .json()
        .await?;
    println!("Created: {created}");

    Ok(())
}
