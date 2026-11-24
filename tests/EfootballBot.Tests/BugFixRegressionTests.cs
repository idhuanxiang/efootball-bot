using System.IO;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

public class BugFixRegressionTests
{
    [Fact]
    public void VigemPad_DisconnectWhenNotConnected_IsIdempotent()
    {
        using var pad = new VigemPad();
        pad.Disconnect();
        pad.Disconnect();
        Assert.False(pad.IsConnected);
        Assert.Equal(-1, pad.UserIndex);
    }

    [Fact]
    public void CleanupStaleArtifacts_WhenClean_DoesNotThrow()
    {
        UpdateApplier.CleanupStaleArtifacts();
    }

    [Fact]
    public void AppConfig_CurrentSchema_PreservesDisabledPhysicalGamepad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"efootball-cfg-{Guid.NewGuid():N}.json");
        try
        {
            string json = $$"""
                {
                  "SchemaVersion": {{AppConfig.CurrentSchemaVersion}},
                  "HidHide": { "HidePhysicalGamepad": false }
                }
                """;
            File.WriteAllText(path, json);
            var cfg = AppConfig.LoadFrom(path);
            Assert.False(cfg.HidHide.HidePhysicalGamepad);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
