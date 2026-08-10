using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace VelocityShare.Server;

/// <summary>
/// SSL certificate validation logic shared between server and mobile clients.
/// Enforces exact host matching for localhost dev bypass and strict SSL policy in production.
/// </summary>
public static class CertificateValidator
{
    /// <summary>
    /// Validates an SSL certificate with the following rules:
    /// 1. Null certificate is always rejected.
    /// 2. Localhost loopback addresses (localhost, 127.0.0.1, ::1) bypass validation for development.
    /// 3. Production: requires zero SSL policy errors (valid chain, trusted root, not expired).
    /// 4. Chain must have at least 1 element.
    /// </summary>
    /// <param name="serverUrl">The server URL being connected to (used for localhost detection).</param>
    /// <param name="certificate">The SSL certificate presented by the server.</param>
    /// <param name="chain">The X.509 certificate chain.</param>
    /// <param name="sslPolicyErrors">The SSL policy errors reported by the framework.</param>
    /// <returns>True if the certificate is accepted; false otherwise.</returns>
    public static bool Validate(string? serverUrl, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        // No certificate at all — always reject
        if (certificate == null) return false;

        // Localhost dev bypass: only for exact loopback addresses
        if (!string.IsNullOrEmpty(serverUrl))
        {
            try
            {
                string host = new Uri(serverUrl).Host;
                if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                    return true;
            }
            catch
            {
                // Invalid URL — fall through to production validation
            }
        }

        // Production: require zero SSL policy errors (valid chain, trusted root, not expired)
        if (sslPolicyErrors != SslPolicyErrors.None)
            return false;

        // Verify the chain is valid and has at least 1 certificate
        if (chain == null || chain.ChainElements.Count < 1)
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether a given host is a loopback address (localhost development bypass).
    /// </summary>
    public static bool IsLoopbackHost(string host) =>
        host == "localhost" || host == "127.0.0.1" || host == "::1";
}
