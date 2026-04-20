// Copyright (c) Rivoli AI 2026. All rights reserved.
// Example: Using the Andy Policies API from Java

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;
import java.security.cert.X509Certificate;

public class Example {
    static final String API_URL = "https://localhost:5112";
    static final String TOKEN = "YOUR_BEARER_TOKEN"; // Obtain from Andy Auth

    public static void main(String[] args) throws Exception {
        // Trust all certs (dev only)
        SSLContext sslContext = SSLContext.getInstance("TLS");
        sslContext.init(null, new TrustManager[]{new X509TrustManager() {
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            public void checkClientTrusted(X509Certificate[] certs, String type) {}
            public void checkServerTrusted(X509Certificate[] certs, String type) {}
        }}, new java.security.SecureRandom());

        HttpClient client = HttpClient.newBuilder().sslContext(sslContext).build();

        // List items
        HttpRequest listRequest = HttpRequest.newBuilder()
            .uri(URI.create(API_URL + "/api/items"))
            .header("Authorization", "Bearer " + TOKEN)
            .GET().build();
        HttpResponse<String> listResponse = client.send(listRequest, HttpResponse.BodyHandlers.ofString());
        System.out.println("Items: " + listResponse.body());

        // Create item
        String json = "{\"name\":\"Example Item\",\"description\":\"Created from Java\"}";
        HttpRequest createRequest = HttpRequest.newBuilder()
            .uri(URI.create(API_URL + "/api/items"))
            .header("Authorization", "Bearer " + TOKEN)
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString(json)).build();
        HttpResponse<String> createResponse = client.send(createRequest, HttpResponse.BodyHandlers.ofString());
        System.out.println("Created: " + createResponse.body());
    }
}
