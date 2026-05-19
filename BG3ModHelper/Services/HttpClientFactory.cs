using System.Net.Http;

namespace BG3ModHelper.Services;

/// <summary>
/// Shared HttpClient for all services. Avoids socket exhaustion and unifies config.
/// HttpClient is thread-safe — single static instance is the standard pattern.
/// </summary>
internal static class HttpClientFactory
{
    public static readonly HttpClient Shared = new();
}
