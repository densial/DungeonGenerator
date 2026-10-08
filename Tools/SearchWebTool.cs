using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

internal static class SearchWebTool
{
    private const int MaxSearchResults = 8;
    private const int MaxSearchQueryLength = 500;

    private static readonly HttpClient WebClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly Regex SearchTitleRegex = new(
        "<a\\b(?=[^>]*\\bclass=\"[^\"]*\\bresult__a\\b[^\"]*\")(?=[^>]*\\bhref=\"(?<url>[^\"]+)\")[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SearchSnippetRegex = new(
        "<a\\b[^>]*\\bclass=\"[^\"]*\\bresult__snippet\\b[^\"]*\"[^>]*>(?<snippet>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static async Task<string> SearchWebAsync(
        [Description("The web search query. Keep it focused and no longer than 500 characters.")] string query,
        [Description("A concise explanation of what fact or rule needs verification and why it is needed.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"[Tools] SearchWebAsync called: query='{ToolLog.OneLine(query)}', reason='{ToolLog.OneLine(reason)}'");
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("The search query cannot be empty.", nameof(query));
        }

        query = query.Trim();
        if (query.Length > MaxSearchQueryLength)
        {
            throw new ArgumentException("The search query must be 500 characters or fewer.", nameof(query));
        }

        var searchUri = new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
        Console.WriteLine($"[Tools] SearchWebAsync requesting: '{searchUri}'");
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DungeonGenerator", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await WebClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"[Tools] SearchWebAsync response: {(int)response.StatusCode} {response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var results = new StringBuilder($"Search results for: {query}\n\n");
        var resultCount = 0;
        foreach (Match titleMatch in SearchTitleRegex.Matches(html))
        {
            if (resultCount == MaxSearchResults)
            {
                break;
            }

            var title = CleanHtml(titleMatch.Groups["title"].Value);
            var url = NormalizeSearchUrl(titleMatch.Groups["url"].Value);
            var remainingHtml = html[titleMatch.Index..];
            var snippetMatch = SearchSnippetRegex.Match(remainingHtml);
            var snippet = snippetMatch.Success ? CleanHtml(snippetMatch.Groups["snippet"].Value) : "";

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            resultCount++;
            results.AppendLine($"{resultCount}. {title}");
            results.AppendLine($"   URL: {url}");
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                results.AppendLine($"   {snippet}");
            }

            results.AppendLine();
        }

        if (resultCount == 0)
        {
            Console.WriteLine("[Tools] SearchWebAsync completed: no results");
            return $"No web results found for: {query}";
        }

        Console.WriteLine($"[Tools] SearchWebAsync completed: {resultCount} results");
        return results.ToString().TrimEnd();
    }

    private static string CleanHtml(string value)
    {
        var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string NormalizeSearchUrl(string value)
    {
        var decodedValue = WebUtility.HtmlDecode(value);
        if (decodedValue.StartsWith("//", StringComparison.Ordinal))
        {
            decodedValue = $"https:{decodedValue}";
        }

        if (!Uri.TryCreate(decodedValue, UriKind.Absolute, out var uri))
        {
            return decodedValue;
        }

        var queryParameter = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .FirstOrDefault(parameter => parameter.Length == 2 && parameter[0] == "uddg");

        return queryParameter is null ? uri.ToString() : WebUtility.UrlDecode(queryParameter[1]);
    }
}
