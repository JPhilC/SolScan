using System.Net.Http;
using System.Text.Json;

namespace SolScan.Infrastructure.Telescope;

/// <summary>
/// Minimal ASCOM Alpaca REST client - ClientID/ClientTransactionID bookkeeping plus a
/// GetAsync&lt;T&gt;/PutAsync wrapper around the standard Alpaca JSON envelope. Near-verbatim port
/// of RASTA's AscomAlpacaClient (RASTA.Infrastructure.Telescope), adapted to thread a
/// CancellationToken through both calls to match SolScan.Core.Telescope.ITelescopeMount's own
/// CT-everywhere shape.
/// </summary>
public class AscomAlpacaClient
{
    private readonly HttpClient _httpClient = new();
    private readonly uint _clientId;
    private uint _transactionId;

    /// <summary>e.g. "http://127.0.0.1:11111/api/v1/telescope/0" - device number already appended
    /// (see AscomTelescopeMount.SetBaseUrl/SetDeviceNumber).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public AscomAlpacaClient()
    {
        // Session-wide ClientID (1-65535), same range Alpaca's own spec expects.
        _clientId = (uint)Random.Shared.Next(1, 65536);
    }

    private uint NextTransactionId()
    {
        _transactionId = _transactionId == uint.MaxValue ? 1 : _transactionId + 1;
        return _transactionId;
    }

    private string BuildUrl(string endpoint, params (string key, string value)[] extraQueryParams)
    {
        var query = string.Concat(extraQueryParams.Select(p => $"&{p.key}={Uri.EscapeDataString(p.value)}"));
        return $"{BaseUrl}/{endpoint}?ClientID={_clientId}&ClientTransactionID={NextTransactionId()}{query}";
    }

    public async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(endpoint);
        return await GetFromUrlAsync<T>(url, endpoint, cancellationToken);
    }

    /// <summary>Overload for endpoints that need an extra query parameter beyond ClientID/
    /// ClientTransactionID - e.g. Alpaca's <c>axisrates?Axis=0</c>. A separate overload (not an
    /// optional params array tacked onto the existing one) so every current call site - which all
    /// pass just (endpoint, ct) - keeps resolving to the plain overload above unambiguously.</summary>
    public async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken, params (string key, string value)[] extraQueryParams)
    {
        var url = BuildUrl(endpoint, extraQueryParams);
        return await GetFromUrlAsync<T>(url, endpoint, cancellationToken);
    }

    private async Task<T> GetFromUrlAsync<T>(string url, string endpoint, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        var result = JsonSerializer.Deserialize<AlpacaResponse<T>>(json)
            ?? throw new InvalidOperationException($"Invalid Alpaca response from {endpoint}");

        if (result.ErrorNumber != 0)
            throw new InvalidOperationException($"Alpaca error {result.ErrorNumber}: {result.ErrorMessage}");

        return result.Value;
    }

    public async Task PutAsync(string endpoint, CancellationToken cancellationToken, params (string key, string value)[] parameters)
    {
        var url = BuildUrl(endpoint);

        var content = new FormUrlEncodedContent(
            parameters.Select(p => new KeyValuePair<string, string>(p.key, p.value)));

        var response = await _httpClient.PutAsync(url, content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        var result = JsonSerializer.Deserialize<AlpacaResponse<object>>(json)
            ?? throw new InvalidOperationException($"Invalid Alpaca response from {endpoint}");

        if (result.ErrorNumber != 0)
            throw new InvalidOperationException($"Alpaca error {result.ErrorNumber}: {result.ErrorMessage}");
    }
}

public class AlpacaResponse<T>
{
    public uint ClientTransactionID { get; set; }
    public uint ServerTransactionID { get; set; }
    public int ErrorNumber { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public T Value { get; set; } = default!;
}
