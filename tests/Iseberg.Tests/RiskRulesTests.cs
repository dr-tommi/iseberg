using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class RiskRulesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-risk-" + Guid.NewGuid().ToString("N"));

    private static RiskFinding Single(string text) => Assert.Single(RiskRules.Find(text, RiskRules.Default));

    [Fact]
    public void TenantChangesAreHighRiskAndMarkTheCommandName()
    {
        const string text = "$route = Get-CsOnlineVoiceRoute\nSet-CsOnlineVoiceRoute -Identity Test -Priority 1";
        var finding = Single(text);
        Assert.Equal(RiskLevel.High, finding.Level);
        Assert.Equal("Set-CsOnlineVoiceRoute", text.Substring(finding.Start, finding.Length));
        Assert.Equal("Set-Cs*", finding.Pattern);
        Assert.False(finding.WhatIf);
    }

    [Fact]
    public void AliasesAreResolvedToTheirCommands()
    {
        var finding = Single("rm C:\\Temp\\old.txt");
        Assert.Equal("Remove-Item", finding.Command);
        Assert.Equal(RiskLevel.High, finding.Level);
    }

    [Fact]
    public void WhatIfLowersTheLevelByOne()
    {
        var high = Single("Grant-CsTeamsCallingPolicy -Identity user -PolicyName Policy -WhatIf");
        Assert.Equal(RiskLevel.Medium, high.Level);
        Assert.True(high.WhatIf);
        Assert.Empty(RiskRules.Find("Stop-Service Spooler -WhatIf", RiskRules.Default));
        Assert.Equal(RiskLevel.High, Single("Remove-Item x -WhatIf:$false").Level);
    }

    [Theory]
    [InlineData("Invoke-MgGraphRequest -Uri $uri", false)]
    [InlineData("Invoke-MgGraphRequest -Uri $uri -Method GET", false)]
    [InlineData("Invoke-MgGraphRequest -Method PATCH -Uri $uri -Body $body", true)]
    [InlineData("Invoke-MgGraphRequest -Method:Delete -Uri $uri", true)]
    [InlineData("Invoke-RestMethod $uri -Me 'Post' -Body $body", true)]
    public void WebRequestsAreHighRiskOnlyForWritingMethods(string text, bool flagged) =>
        Assert.Equal(flagged, RiskRules.Find(text, RiskRules.Default).Any(finding => finding.Level == RiskLevel.High));

    [Fact]
    public void EverydayCommandsAndNestedCallsAreHandled()
    {
        Assert.Empty(RiskRules.Find("Set-StrictMode -Version Latest\nSet-Location C:\\\n$o = New-Object PSObject\nGet-Item .", RiskRules.Default));
        var nested = RiskRules.Find("Get-ChildItem | ForEach-Object { Remove-Item $_.FullName }\nfunction F { Restart-Service W32Time }", RiskRules.Default);
        Assert.Equal(["Remove-Item", "Restart-Service"], nested.Select(finding => finding.Command));
        Assert.Equal([RiskLevel.High, RiskLevel.Medium], nested.Select(finding => finding.Level));
    }

    [Fact]
    public async Task TheRuleFileIsSeededOnceAndReadWithComments()
    {
        var path = Path.Combine(directory, "RiskRules.json");
        await RiskRules.EnsureFileAsync(path, TestContext.Current.CancellationToken);
        var loaded = await RiskRules.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(RiskRules.Default.High.Count, loaded.High.Count);
        Assert.Equal(["Post", "Patch", "Put", "Delete"], loaded.High.Single(rule => rule.Pattern == "Invoke-MgGraphRequest").Methods);

        await File.WriteAllTextAsync(path, """
            {
              // Only my own rule
              "High": [ { "Pattern": "Set-Thing", "Reason": "Mine" }, ],
            }
            """, TestContext.Current.CancellationToken);
        await RiskRules.EnsureFileAsync(path, TestContext.Current.CancellationToken);
        var custom = await RiskRules.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("Set-Thing", Assert.Single(custom.High).Pattern);
        Assert.Empty(custom.Medium);
        Assert.Throws<InvalidDataException>(() => RiskRules.Parse("""{ "High": [ { "Reason": "no pattern" } ] }"""));
        Assert.Throws<InvalidDataException>(() => RiskRules.Parse("not json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
