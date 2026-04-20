// Copyright (c) Rivoli AI 2026. All rights reserved.
// Example: Using the Andy Policies API from C#

using System.Net.Http.Headers;
using System.Net.Http.Json;

var apiUrl = "https://localhost:5112";
var token = "YOUR_BEARER_TOKEN"; // Obtain from Andy Auth

using var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};
using var client = new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

// List items
var items = await client.GetFromJsonAsync<object[]>("/api/items");
Console.WriteLine($"Found {items?.Length ?? 0} items");

// Create item
var response = await client.PostAsJsonAsync("/api/items", new { Name = "Example Item", Description = "Created from C#" });
response.EnsureSuccessStatusCode();
var created = await response.Content.ReadFromJsonAsync<dynamic>();
Console.WriteLine($"Created: {created}");
