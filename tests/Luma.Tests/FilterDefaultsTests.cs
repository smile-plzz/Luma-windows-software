using Luma.App;
using System.Text.Json;
using Xunit;
namespace Luma.Tests;
public sealed class FilterDefaultsTests
{
    [Fact] public void NewLibrariesShowAllSupportedFormats() => Assert.False(new AppSettings().CommonOnly);
    [Fact] public void UpgradeRevealsFilesWithoutLosingOtherPreferences()
    {
        var settings=AppSettings.FromJson("{\"CommonOnly\":true,\"ThumbnailSize\":240,\"Theme\":\"Dark\"}");
        Assert.False(settings.CommonOnly); Assert.Equal(240,settings.ThumbnailSize); Assert.Equal("Dark",settings.Theme);
        settings.CommonOnly=true;
        Assert.True(AppSettings.FromJson(JsonSerializer.Serialize(settings)).CommonOnly);
    }
    [Fact] public void ExplicitModernFilterChoiceSurvivesRestart()
        => Assert.True(AppSettings.FromJson("{\"CommonOnly\":true,\"FilterDefaultsVersion\":1}").CommonOnly);
}
