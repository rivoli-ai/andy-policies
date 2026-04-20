# Copyright (c) Rivoli AI 2026. All rights reserved.
# Example: Using the Andy Policies API from PowerShell

$ApiUrl = "https://localhost:5112"
$Token = "YOUR_BEARER_TOKEN" # Obtain from Andy Auth

$Headers = @{
    "Authorization" = "Bearer $Token"
    "Content-Type" = "application/json"
}

# Skip cert validation (dev only)
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
}

# List items
$items = Invoke-RestMethod -Uri "$ApiUrl/api/items" -Headers $Headers -Method Get
Write-Host "Found $($items.Count) items"

# Create item
$body = @{ name = "Example Item"; description = "Created from PowerShell" } | ConvertTo-Json
$created = Invoke-RestMethod -Uri "$ApiUrl/api/items" -Headers $Headers -Method Post -Body $body
Write-Host "Created: $($created.name) ($($created.id))"
