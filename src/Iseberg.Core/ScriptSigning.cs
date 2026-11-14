using System.Management.Automation;
using System.Security.Cryptography.X509Certificates;

namespace Iseberg.Core;

/// <summary>
/// The Authenticode signature state of a script. <see cref="Status"/> is the
/// System.Management.Automation.SignatureStatus name, such as Valid, NotSigned, HashMismatch or UnknownError.
/// </summary>
public sealed record ScriptSignature(string Status, string StatusMessage, string? Signer, string? Thumbprint, string? TimeStamper)
{
    public bool IsValid => Status == "Valid";
    public bool IsSigned => Status != "NotSigned";
}

/// <summary>Signs scripts with Authenticode and reads their signatures, in a runspace separate from the user's sessions.</summary>
public static class ScriptSigning
{
    public const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    /// <summary>The comment that starts an Authenticode signature block in a PowerShell script.</summary>
    public const string SignatureBlockMarker = "# SIG # Begin signature block";

    /// <summary>A certificate can sign scripts when it has a private key, allows code signing, and is currently valid.</summary>
    public static bool IsUsable(X509Certificate2 certificate, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || now < certificate.NotBefore || now > certificate.NotAfter) return false;
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        // Without an EKU extension a certificate is valid for every purpose.
        return usage is null || usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == CodeSigningOid);
    }

    /// <summary>Usable code-signing certificates from the current user's and the computer's personal stores.</summary>
    public static IReadOnlyList<X509Certificate2> FindCertificates()
    {
        var now = DateTime.Now;
        var found = new List<X509Certificate2>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var certificate in store.Certificates)
                {
                    if (IsUsable(certificate, now) && found.All(existing => existing.Thumbprint != certificate.Thumbprint)) found.Add(certificate);
                    else certificate.Dispose();
                }
            }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return found.OrderBy(certificate => certificate.Subject, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Signs a local script with SHA-256, including the chain without its root, and returns the resulting signature.</summary>
    public static Task<ScriptSignature> SignAsync(string path, X509Certificate2 certificate, string? timestampServer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Specify an absolute local file path.", nameof(path));
        return Task.Run(() =>
        {
            using var shell = PowerShell.Create();
            shell.AddCommand("Set-AuthenticodeSignature").AddParameter("LiteralPath", path).AddParameter("Certificate", certificate)
                .AddParameter("HashAlgorithm", "SHA256").AddParameter("IncludeChain", "NotRoot").AddParameter("ErrorAction", "Stop");
            if (!string.IsNullOrWhiteSpace(timestampServer)) shell.AddParameter("TimestampServer", timestampServer.Trim());
            using var registration = cancellationToken.Register(() => shell.Stop());
            var result = shell.Invoke<Signature>().FirstOrDefault()
                ?? throw new InvalidOperationException("Set-AuthenticodeSignature returned no signature.");
            cancellationToken.ThrowIfCancellationRequested();
            return Describe(result);
        }, cancellationToken);
    }

    /// <summary>Reads the Authenticode signature of a local script.</summary>
    public static Task<ScriptSignature> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() =>
        {
            using var shell = PowerShell.Create();
            shell.AddCommand("Get-AuthenticodeSignature").AddParameter("LiteralPath", path).AddParameter("ErrorAction", "Stop");
            using var registration = cancellationToken.Register(() => shell.Stop());
            var result = shell.Invoke<Signature>().FirstOrDefault()
                ?? throw new InvalidOperationException("Get-AuthenticodeSignature returned no signature.");
            cancellationToken.ThrowIfCancellationRequested();
            return Describe(result);
        }, cancellationToken);
    }

    private static ScriptSignature Describe(Signature signature) => new(signature.Status.ToString(), signature.StatusMessage ?? "",
        signature.SignerCertificate?.Subject, signature.SignerCertificate?.Thumbprint, signature.TimeStamperCertificate?.Subject);
}
