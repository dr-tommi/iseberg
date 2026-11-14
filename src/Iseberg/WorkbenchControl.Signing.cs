using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    /// <summary>Signing and checking work on saved local scripts while the PowerShell tab is idle.</summary>
    private bool CanSignScript => displayedFile?.File is { Path: not null, IsRemote: false } &&
        displayedSession?.Engine.State == SessionState.Ready && !windowClosed;

    private async Task SignScriptAsync()
    {
        if (!CanSignScript || displayedFile is not { } tab) return;
        if (tab.File.IsDirty)
        {
            if (await Dialogs.ChooseAsync(HostWindow, UiText.Get("SignScript").Replace("_", "").TrimEnd('.'),
                    string.Format(UiText.Get("SaveBeforeSigning"), tab.File.Name), UiText.Get("SaveChoice"), UiText.Get("Cancel"))
                != UiText.Get("SaveChoice") || !await SaveFileAsync(tab) || tab.File.IsDirty)
                return;
        }
        var path = tab.File.Path!;
        var certificates = ScriptSigning.FindCertificates();
        var window = new SignWindow(certificates, settings.TimestampServer);
        try
        {
            var request = await window.ShowDialog<SignRequest?>(HostWindow);
            if (request is null) return;
            if (settings.TimestampServer != request.TimestampServer)
            {
                settings.TimestampServer = request.TimestampServer;
                await SaveSettingsAsync();
            }
            var signature = await ScriptSigning.SignAsync(path, request.Certificate, request.TimestampServer);
            // The signature block was appended on disk; load it so the editor and the file agree again.
            if (Workbench.Sessions.SelectMany(session => session.Files).Contains(tab)) await ReloadFileAsync(tab, confirm: false);
            ShowCorrectionNotice(string.Format(UiText.Get("ScriptSigned"), DescribeSignature(signature)));
        }
        finally
        {
            foreach (var certificate in certificates.Concat(window.LoadedCertificates)) certificate.Dispose();
        }
    }

    private async Task CheckSignatureAsync()
    {
        if (!CanSignScript || displayedFile?.File.Path is not { } path) return;
        var signature = await ScriptSigning.GetAsync(path);
        var lines = new List<string> { DescribeSignature(signature) };
        if (signature.Signer is not null) lines.Add(string.Format(UiText.Get("SignatureSigner"), signature.Signer));
        if (signature.Thumbprint is not null) lines.Add(string.Format(UiText.Get("SignatureThumbprint"), signature.Thumbprint));
        if (signature.IsSigned)
            lines.Add(signature.TimeStamper is null ? UiText.Get("SignatureNoTimestamp") : string.Format(UiText.Get("SignatureTimestamp"), signature.TimeStamper));
        if (displayedFile?.File.IsDirty == true) lines.Add(UiText.Get("SignatureOfSavedFile"));
        await Dialogs.ChooseAsync(HostWindow, UiText.Get("CheckSignature").Replace("_", ""), string.Join(Environment.NewLine, lines), UiText.Get("OK"));
    }

    /// <summary>After saving a script that carries a signature block, says when the edits invalidated it.</summary>
    private async Task NoteSignatureAfterSaveAsync(ScriptTab tab)
    {
        if (tab.File is not { Path: { } path, IsRemote: false } || !tab.File.Text.Contains(ScriptSigning.SignatureBlockMarker, StringComparison.Ordinal))
            return;
        var signature = await ScriptSigning.GetAsync(path);
        if (signature.Status == "HashMismatch" && displayedFile == tab) ShowCorrectionNotice(UiText.Get("SignatureInvalidatedBySave"));
    }

    /// <summary>A localized description of a signature status, falling back to PowerShell's own message.</summary>
    public static string DescribeSignature(ScriptSignature signature)
    {
        var key = "Signature" + signature.Status;
        try { return UiText.Get(key); }
        catch (System.Resources.MissingManifestResourceException) { return $"{signature.Status}: {signature.StatusMessage}"; }
    }
}
