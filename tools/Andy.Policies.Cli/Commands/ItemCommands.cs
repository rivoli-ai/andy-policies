// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Andy.Policies.Cli.Commands;

public static class ItemCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Register(Command parent, Option<string> apiUrlOption, Option<string?> tokenOption)
    {
        var listCommand = new Command("list", "List all items");
        listCommand.SetHandler(async (apiUrl, token) =>
        {
            using var client = CreateClient(apiUrl, token);
            var response = await client.GetAsync("/api/items");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine(body);
        }, apiUrlOption, tokenOption);
        parent.AddCommand(listCommand);

        var getCommand = new Command("get", "Get item by ID");
        var idArg = new Argument<string>("id", "Item ID");
        getCommand.AddArgument(idArg);
        getCommand.SetHandler(async (apiUrl, token, id) =>
        {
            using var client = CreateClient(apiUrl, token);
            var response = await client.GetAsync($"/api/items/{id}");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine(body);
        }, apiUrlOption, tokenOption, idArg);
        parent.AddCommand(getCommand);

        var createCommand = new Command("create", "Create a new item");
        var nameOpt = new Option<string>("--name", "Item name") { IsRequired = true };
        var descOpt = new Option<string?>("--description", "Item description");
        createCommand.AddOption(nameOpt);
        createCommand.AddOption(descOpt);
        createCommand.SetHandler(async (apiUrl, token, name, desc) =>
        {
            using var client = CreateClient(apiUrl, token);
            var response = await client.PostAsJsonAsync("/api/items", new { Name = name, Description = desc });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine(body);
        }, apiUrlOption, tokenOption, nameOpt, descOpt);
        parent.AddCommand(createCommand);

        var deleteCommand = new Command("delete", "Delete an item");
        var deleteIdArg = new Argument<string>("id", "Item ID");
        deleteCommand.AddArgument(deleteIdArg);
        deleteCommand.SetHandler(async (apiUrl, token, id) =>
        {
            using var client = CreateClient(apiUrl, token);
            var response = await client.DeleteAsync($"/api/items/{id}");
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"Item {id} deleted.");
        }, apiUrlOption, tokenOption, deleteIdArg);
        parent.AddCommand(deleteCommand);
    }

    private static HttpClient CreateClient(string apiUrl, string? token)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
