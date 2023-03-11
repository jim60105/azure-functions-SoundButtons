﻿using SoundButtons.Models;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace SoundButtons.Services;

internal class OpenAIService
{
    private readonly HttpClient _client;
    public string OpenAIEndpoint { get; } = "https://api.openai.com/v1/";
    private static string _apiKey = "";

    public OpenAIService()
    {
        _client = new HttpClient
        {
            BaseAddress = new(OpenAIEndpoint)
        };
        _apiKey = Environment.GetEnvironmentVariable("OpenAI_ApiKey");
    }

    public async Task<TranscriptionsResponse> SpeechToTextAsync(string path)
    {
        if (!CheckApiKey()) return new();

        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var content = new MultipartFormDataContent
        {
            { new StreamContent(fileStream), "file", Path.GetFileName(path) },
            { new StringContent("whisper-1"), "model" },
            { new StringContent("Remove superfluous words, the speaker's self-proclaimed may be \"艦長\", \"ユリ\""), "prompt" },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent("0.1"), "temperature" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = content;
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TranscriptionsResponse>();
    }

    private static bool CheckApiKey() => !string.IsNullOrEmpty(_apiKey);
}