using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Threading;

namespace ChatbotBlazor.Client.Services
{
    public static class BotService
    {
        private static readonly HttpClient _http = new HttpClient();
        private static readonly string apiKey = "AIzaSyCuNvc3cMNOjHILLZVTEFOZlVut58TKlGA";
        private static DateTime _lastRequestTime = DateTime.MinValue;
        private static readonly TimeSpan _minRequestInterval = TimeSpan.FromSeconds(2); // Minimum 2 seconds between requests

        static BotService()
        {
            _http.Timeout = TimeSpan.FromSeconds(60); // Increased timeout
        }

        public static async Task<string> GetReplyAsync(string userInput)
        {
            try
            {
                // Validate input
                if (string.IsNullOrWhiteSpace(userInput))
                {
                    return "Please provide a message.";
                }

                // Rate limiting - wait between requests
                await EnforceRateLimit();

                // Try multiple times with exponential backoff
                int maxRetries = 3;
                for (int retry = 0; retry <= maxRetries; retry++)
                {
                    try
                    {
                        var response = await MakeApiRequest(userInput);
                        _lastRequestTime = DateTime.Now;
                        return response;
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("TooManyRequests") && retry < maxRetries)
                    {
                        // Exponential backoff: wait longer between retries
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, retry + 2)); // 4s, 8s, 16s
                        await Task.Delay(delay);
                        continue;
                    }
                    catch (Exception ex) when (retry < maxRetries)
                    {
                        // Wait before retry for other errors
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        continue;
                    }
                }

                return "❌ Service temporarily unavailable. Please try again in a few minutes.";
            }
            catch (Exception ex)
            {
                return $"❌ Error: {ex.Message}";
            }
        }

        private static async Task EnforceRateLimit()
        {
            var timeSinceLastRequest = DateTime.Now - _lastRequestTime;
            if (timeSinceLastRequest < _minRequestInterval)
            {
                var waitTime = _minRequestInterval - timeSinceLastRequest;
                await Task.Delay(waitTime);
            }
        }

        private static async Task<string> MakeApiRequest(string userInput)
        {
            var requestBody = new
            {
                contents = new[]
                {
                    new {
                        parts = new[]
                        {
                            new { text = userInput }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.7,
                    topK = 40,
                    topP = 0.95,
                    maxOutputTokens = 1024, // Reduced to save quota
                    stopSequences = new string[] { }
                },
                safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                }
            };

            var requestJson = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}", // Using flash model for lower quota usage
                content
            );

            var responseJson = await response.Content.ReadAsStringAsync();

            // Handle rate limiting specifically
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("TooManyRequests");
            }

            // Check if the HTTP request was successful
            if (!response.IsSuccessStatusCode)
            {
                return $"❌ API request failed: {response.StatusCode}. Please try again in a few minutes.";
            }

            using var doc = JsonDocument.Parse(responseJson);

            // Check for candidates in response
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                
                // Check if content was blocked by safety filters
                if (candidate.TryGetProperty("finishReason", out var finishReason))
                {
                    var reason = finishReason.GetString();
                    if (reason == "SAFETY")
                    {
                        return "❌ I can't provide a response to that request due to safety guidelines.";
                    }
                    else if (reason == "RECITATION")
                    {
                        return "❌ I can't provide that response due to content policy.";
                    }
                }

                // Extract the response text
                if (candidate.TryGetProperty("content", out var contentProp) &&
                    contentProp.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var textProp))
                {
                    var reply = textProp.GetString();
                    
                    if (!string.IsNullOrWhiteSpace(reply))
                    {
                        return reply;
                    }
                }
                
                return "❌ Received empty response from AI. Please try again.";
            }
            else if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var errorMessage = "Unknown error";
                if (error.TryGetProperty("message", out var messageProp))
                {
                    errorMessage = messageProp.GetString() ?? "Unknown error";
                }
                
                // Handle quota exceeded specifically
                if (errorMessage.Contains("quota") || errorMessage.Contains("limit"))
                {
                    return "❌ Daily quota exceeded. Please try again tomorrow or check your API usage.";
                }
                
                return $"❌ API Error: {errorMessage}";
            }

            return "❌ Unexpected response from AI. Please try again.";
        }
    }
}