// Zero Unity dependencies. Pure .NET.
using System.Threading.Tasks;

namespace BurstStrike.Net.Server.Auth
{
    /// <summary>
    /// Result of auth token validation against the web/lobby server.
    /// </summary>
    public struct AuthResult
    {
        /// <summary>Whether the token is valid.</summary>
        public bool IsValid;
        /// <summary>Web-server-assigned unique player id.</summary>
        public int PlayerId;
        /// <summary>Player display name (for server logs).</summary>
        public string PlayerName;
        /// <summary>If invalid, the reason.</summary>
        public string DenyReason;

        public static AuthResult Accept(int playerId, string name = null)
            => new AuthResult { IsValid = true, PlayerId = playerId, PlayerName = name ?? $"Player{playerId}" };

        public static AuthResult Deny(string reason)
            => new AuthResult { IsValid = false, DenyReason = reason };
    }

    /// <summary>
    /// Abstract web-server auth validation.
    /// The frame-sync server calls this when a client sends JoinRequest.
    /// Implementations:
    /// - AllowAllAuthValidator: development/test (always accepts)
    /// - HttpAuthValidator: production (calls web API to validate token)
    /// </summary>
    public interface IAuthValidator
    {
        Task<AuthResult> ValidateAsync(string authToken);
    }

    /// <summary>
    /// Development auth validator — accepts all connections.
    /// Assigns monotonically increasing player ids.
    /// </summary>
    public sealed class AllowAllAuthValidator : IAuthValidator
    {
        private int _nextId = 1;

        public Task<AuthResult> ValidateAsync(string authToken)
        {
            int id = System.Threading.Interlocked.Increment(ref _nextId);
            return Task.FromResult(AuthResult.Accept(id, $"Dev{id}"));
        }
    }

    /// <summary>
    /// Production auth validator stub — calls web API.
    /// Replace the Validate body with your actual HTTP call.
    /// </summary>
    public sealed class HttpAuthValidator : IAuthValidator
    {
        private readonly string _webServerUrl;

        public HttpAuthValidator(string webServerUrl)
        {
            _webServerUrl = webServerUrl;
        }

        public async Task<AuthResult> ValidateAsync(string authToken)
        {
            if (string.IsNullOrEmpty(authToken))
                return AuthResult.Deny("Empty auth token");

            // TODO: Implement actual HTTP call to web server:
            // var response = await httpClient.PostAsync($"{_webServerUrl}/api/auth/validate",
            //     new StringContent(JsonSerializer.Serialize(new { token = authToken })));
            // var result = JsonSerializer.Deserialize<WebAuthResponse>(await response.Content.ReadAsStringAsync());
            // return result.Valid ? AuthResult.Accept(result.PlayerId, result.Name) : AuthResult.Deny(result.Reason);

            await Task.Yield(); // simulate async
            return AuthResult.Deny("HttpAuthValidator not implemented");
        }
    }
}
