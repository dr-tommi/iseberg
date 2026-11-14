using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ScriptSigningTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-signing-" + Guid.NewGuid().ToString("N"));

    /// <summary>A self-signed certificate that exists only in memory; no certificate store is touched.</summary>
    internal static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter, bool codeSigning = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Iseberg Test Signing", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var usage = new OidCollection { new Oid(codeSigning ? ScriptSigning.CodeSigningOid : "1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usage, false));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    [Fact]
    public void OnlyCurrentCodeSigningCertificatesWithAPrivateKeyAreUsable()
    {
        var now = DateTime.Now;
        using var valid = CreateCertificate(now.AddDays(-1), now.AddDays(1));
        using var expired = CreateCertificate(now.AddDays(-10), now.AddDays(-1));
        using var server = CreateCertificate(now.AddDays(-1), now.AddDays(1), codeSigning: false);
        using var publicOnly = X509CertificateLoader.LoadCertificate(valid.Export(X509ContentType.Cert));
        Assert.True(ScriptSigning.IsUsable(valid, now));
        Assert.False(ScriptSigning.IsUsable(expired, now));
        Assert.False(ScriptSigning.IsUsable(server, now));
        Assert.False(ScriptSigning.IsUsable(publicOnly, now));
    }

    [Fact]
    public async Task SigningAddsASignatureThatEditingInvalidates()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "signed.ps1");
        await File.WriteAllTextAsync(path, "Write-Output 'Grüße'\r\n", TestContext.Current.CancellationToken);
        Assert.Equal("NotSigned", (await ScriptSigning.GetAsync(path, TestContext.Current.CancellationToken)).Status);

        using var certificate = CreateCertificate(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var signed = await ScriptSigning.SignAsync(path, certificate, null, TestContext.Current.CancellationToken);
        // The self-signed root is not trusted, but the signature itself is intact.
        Assert.Equal("UnknownError", signed.Status);
        Assert.Equal(certificate.Thumbprint, signed.Thumbprint);
        Assert.Null(signed.TimeStamper);
        Assert.Contains(ScriptSigning.SignatureBlockMarker, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, text.Replace("Grüße", "Hallo"), TestContext.Current.CancellationToken);
        Assert.Equal("HashMismatch", (await ScriptSigning.GetAsync(path, TestContext.Current.CancellationToken)).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
