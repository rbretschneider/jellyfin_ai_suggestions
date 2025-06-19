using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GeminiSearch.Controllers;

/// <summary>
/// Gemini search controller.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/GeminiSearch")]
public class GeminiController : ControllerBase
{
    private readonly ILogger<GeminiController> _logger;
    private readonly ILibraryManager _libraryManager;
    private static readonly HttpClient _httpClient = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{GeminiController}"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public GeminiController(ILogger<GeminiController> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Search for movies and TV shows using Gemini AI.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="mediaType">The type of media to search for (movie, tv, or both).</param>
    /// <param name="resultCount">The number of results to return.</param>
    /// <returns>Search results with library matching information.</returns>
    [HttpGet("search")]
    public async Task<ActionResult<GeminiSearchResponse>> Search([FromQuery] string query, [FromQuery] string? mediaType = "both", [FromQuery] int resultCount = 25)
    {
        try
        {
            _logger.LogInformation("Search endpoint hit. Raw query parameter: '{Query}', MediaType: '{MediaType}', ResultCount: {ResultCount}",
                query ?? "NULL", mediaType ?? "NULL", resultCount);

            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.LogWarning("Query is null or empty");
                return BadRequest("Query cannot be empty");
            }

            // Validate and sanitize parameters
            mediaType = mediaType?.ToLower() ?? "both";
            if (mediaType != "movie" && mediaType != "tv" && mediaType != "both")
            {
                mediaType = "both";
            }

            if (resultCount < 5) resultCount = 5;
            if (resultCount > 50) resultCount = 50;

            _logger.LogInformation("Gemini search request received for query: {Query}, MediaType: {MediaType}, ResultCount: {ResultCount}",
                query, mediaType, resultCount);

            var config = Plugin.Instance?.PluginConfiguration;
            if (config == null)
            {
                _logger.LogError("Plugin instance or configuration is null");
                return StatusCode(500, "Plugin configuration not available");
            }

            if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
            {
                _logger.LogWarning("Gemini API key not configured");
                return BadRequest("Gemini API key not configured");
            }

            _logger.LogInformation("Starting Gemini search with API key configured");

            // Get Gemini recommendations
            var geminiResults = await GetGeminiRecommendations(query, config.GeminiApiKey, mediaType, resultCount);

            _logger.LogInformation("Received {Count} results from Gemini", geminiResults.Count);

            // Check what's in the user's library
            var libraryResults = CheckLibraryMatches(geminiResults);

            var response = new GeminiSearchResponse
            {
                Query = query,
                Results = libraryResults
            };

            _logger.LogInformation("Returning {Count} results to client", response.Results.Count);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Gemini search for query: {Query}", query);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    private async Task<List<MediaRecommendation>> GetGeminiRecommendations(string query, string apiKey, string mediaType, int resultCount)
    {
        try
        {
            _logger.LogInformation("Calling Gemini API for query: {Query}, MediaType: {MediaType}, ResultCount: {ResultCount}",
                query, mediaType, resultCount);

            // Build media type constraint for the prompt
            string mediaTypeConstraint = "";

            switch (mediaType)
            {
                case "movie":
                    mediaTypeConstraint = "IMPORTANT: Return ONLY movies, no TV shows or series.";
                    break;
                case "tv":
                    mediaTypeConstraint = "IMPORTANT: Return ONLY TV shows/series, no movies.";
                    break;
                case "both":
                    mediaTypeConstraint = "Return a mix of both movies and TV shows.";
                    break;
            }

            var requestPayload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = $@"Based on the query ""{query}"", recommend {(mediaType == "both" ? "movies and TV shows" : mediaType == "movie" ? "movies" : "TV shows")} that match the request. 

{mediaTypeConstraint}

Return exactly {resultCount} results. Return ONLY a JSON array with objects containing ""title"", ""year"", ""type"" (""movie"" or ""tv""), and ""description"". No other text.

For type field: use ""movie"" for movies and ""tv"" for TV shows/series.

Example format:
[
  {{""title"": ""The Lion King"", ""year"": 1994, ""type"": ""movie"", ""description"": ""Disney animated classic about a young lion prince""}},
  {{""title"": ""Peppa Pig"", ""year"": 2004, ""type"": ""tv"", ""description"": ""British animated series for preschoolers""}}
]"
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(requestPayload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";
            _logger.LogInformation("Making request to Gemini API");

            var response = await _httpClient.PostAsync(apiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Gemini API error: Status={Status}, Content={Content}", response.StatusCode, errorContent);
                throw new Exception($"Gemini API request failed: {response.StatusCode} - {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Received response from Gemini API, length: {Length}", responseContent.Length);
            _logger.LogInformation("Full Gemini response: {Response}", responseContent);

            // Use JsonDocument for more robust parsing
            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;

            if (!root.TryGetProperty("candidates", out var candidatesElement) || candidatesElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("No candidates array found in response");
                return new List<MediaRecommendation>();
            }

            var candidates = candidatesElement.EnumerateArray().ToList();
            if (candidates.Count == 0)
            {
                _logger.LogWarning("Candidates array is empty");
                return new List<MediaRecommendation>();
            }

            var firstCandidate = candidates[0];
            if (!firstCandidate.TryGetProperty("content", out var contentElement))
            {
                _logger.LogWarning("No content found in first candidate");
                return new List<MediaRecommendation>();
            }

            if (!contentElement.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("No parts array found in content");
                return new List<MediaRecommendation>();
            }

            var parts = partsElement.EnumerateArray().ToList();
            if (parts.Count == 0)
            {
                _logger.LogWarning("Parts array is empty");
                return new List<MediaRecommendation>();
            }

            var firstPart = parts[0];
            if (!firstPart.TryGetProperty("text", out var textElement))
            {
                _logger.LogWarning("No text found in first part");
                return new List<MediaRecommendation>();
            }

            var recommendationsText = textElement.GetString();
            if (string.IsNullOrWhiteSpace(recommendationsText))
            {
                _logger.LogWarning("Recommendations text is null or empty");
                return new List<MediaRecommendation>();
            }

            _logger.LogInformation("Gemini response text: {Text}", recommendationsText);

            // Extract JSON from the response (remove any markdown formatting)
            var jsonStart = recommendationsText.IndexOf('[');
            var jsonEnd = recommendationsText.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonText = recommendationsText.Substring(jsonStart, jsonEnd - jsonStart + 1);
                _logger.LogInformation("Extracted JSON: {Json}", jsonText);

                try
                {
                    var recommendations = JsonSerializer.Deserialize<List<MediaRecommendation>>(jsonText, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    // Filter results based on media type if specified (double-check Gemini's output)
                    if (mediaType != "both" && recommendations != null)
                    {
                        recommendations = recommendations.Where(r =>
                            string.Equals(r.Type, mediaType, StringComparison.OrdinalIgnoreCase) ||
                            (mediaType == "tv" && (r.Type.ToLower().Contains("tv") || r.Type.ToLower().Contains("series")))
                        ).ToList();

                        _logger.LogInformation("Filtered to {Count} recommendations matching type '{MediaType}'",
                            recommendations.Count, mediaType);
                    }

                    _logger.LogInformation("Successfully parsed {Count} recommendations", recommendations?.Count ?? 0);
                    return recommendations ?? new List<MediaRecommendation>();
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse JSON: {Json}", jsonText);
                    return new List<MediaRecommendation>();
                }
            }

            _logger.LogWarning("Could not extract valid JSON from Gemini response. Full text: {Text}", recommendationsText);
            return new List<MediaRecommendation>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Gemini API");
            throw;
        }
    }

    private List<SearchResult> CheckLibraryMatches(List<MediaRecommendation> recommendations)
    {
        var results = new List<SearchResult>();
        _logger.LogInformation("Checking library matches for {Count} recommendations", recommendations.Count);

        foreach (var recommendation in recommendations)
        {
            _logger.LogInformation("Processing recommendation: {Title} ({Year}) - {Type}",
                recommendation.Title, recommendation.Year, recommendation.Type);

            var searchResult = new SearchResult
            {
                Title = recommendation.Title,
                Year = recommendation.Year,
                Type = recommendation.Type,
                Description = recommendation.Description,
                InLibrary = false
            };

            // Search for matches in the library
            var query = new InternalItemsQuery
            {
                SearchTerm = recommendation.Title,
                IncludeItemTypes = recommendation.Type.ToLower() == "movie"
                    ? new[] { BaseItemKind.Movie }
                    : new[] { BaseItemKind.Series },
                Limit = 5
            };

            try
            {
                var libraryItems = _libraryManager.GetItemsResult(query);
                _logger.LogInformation("Library search for '{Title}' returned {Count} items",
                    recommendation.Title, libraryItems.TotalRecordCount);

                var match = libraryItems.Items.FirstOrDefault(item =>
                    string.Equals(item.Name, recommendation.Title, StringComparison.OrdinalIgnoreCase) ||
                    (item.ProductionYear.HasValue && Math.Abs(item.ProductionYear.Value - recommendation.Year) <= 1 &&
                     item.Name.Contains(recommendation.Title, StringComparison.OrdinalIgnoreCase)));

                if (match != null)
                {
                    _logger.LogInformation("Found library match: {Name} ({Year})", match.Name, match.ProductionYear);
                    searchResult.InLibrary = true;
                    searchResult.JellyfinId = match.Id.ToString();
                    searchResult.JellyfinPath = match.Path;
                }
                else
                {
                    _logger.LogInformation("No library match found for: {Title}", recommendation.Title);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching library for: {Title}", recommendation.Title);
            }

            results.Add(searchResult);
        }

        return results;
    }
}

/// <summary>
/// Gemini search response.
/// </summary>
public class GeminiSearchResponse
{
    /// <summary>
    /// Gets or sets the original query.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the search results.
    /// </summary>
    public List<SearchResult> Results { get; set; } = new();
}

/// <summary>
/// Individual search result.
/// </summary>
public class SearchResult
{
    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Gets or sets the media type.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the item is in the user's library.
    /// </summary>
    public bool InLibrary { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin item ID if in library.
    /// </summary>
    public string? JellyfinId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin file path if in library.
    /// </summary>
    public string? JellyfinPath { get; set; }
}

/// <summary>
/// Media recommendation from Gemini.
/// </summary>
public class MediaRecommendation
{
    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Gets or sets the type.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}