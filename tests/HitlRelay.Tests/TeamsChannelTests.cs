using HitlRelay.Channels.Teams;
using HitlRelay.Core;
using HitlRelay.Models;
using Xunit;

namespace HitlRelay.Tests;

public class TeamsChannelTests
{
    [Theory]
    [InlineData("Order ", "A-1001", "Order A-1001")]
    [InlineData("Order ", "A/1001:x?", "Order A-1001-x-")]   // forbidden characters replaced
    [InlineData("", "  .weird.  ", "weird")]                 // no leading/trailing dots or spaces
    public void ChannelNamesAreSanitised(string prefix, string key, string expected) =>
        Assert.Equal(expected, TeamsChannelProvisioner.ChannelName(prefix, key));

    [Fact]
    public void ChannelNamesAreCappedAt50Characters() =>
        Assert.True(TeamsChannelProvisioner.ChannelName("Order ", new string('x', 200)).Length <= 50);

    private static HitlNodeConfig ChannelModeConfig()
    {
        var cfg = Harness.KeywordConfig(ChannelKeys.Teams);
        cfg.Recipient = "";
        cfg.TeamsChannelKey = "{{order.id}}";
        return cfg;
    }

    [Fact]
    public async Task ChannelKeyIsRenderedFromWorkflowVariables_AndRecipientIsOptional()
    {
        using var h = new Harness();
        var cfg = ChannelModeConfig();
        var req = await h.CreateAsync(cfg);

        Assert.Equal("A-7", req.Config.TeamsChannelKey);       // rendered, stored with the request
        Assert.Equal("channel:A-7", req.Recipient);
        Assert.Equal("{{order.id}}", cfg.TeamsChannelKey);     // the caller's config is not mutated
    }

    [Fact]
    public async Task EachRequestForTheSameOrderIsItsOwnPostWithTheSameKey()
    {
        using var h = new Harness();
        var first = await h.CreateAsync(ChannelModeConfig(), "run-1");
        var second = await h.CreateAsync(ChannelModeConfig(), "run-2");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Config.TeamsChannelKey, second.Config.TeamsChannelKey);
        Assert.Equal(2, h.Channels.Sent.Count);                // two sends = two posts
        Assert.NotEqual(first.ExternalMessageId, second.ExternalMessageId);
    }

    [Fact]
    public async Task ChannelKeyIsRejectedOnOtherChannels()
    {
        using var h = new Harness();
        var cfg = Harness.KeywordConfig(ChannelKeys.Slack);
        cfg.TeamsChannelKey = "A-7";
        await Assert.ThrowsAsync<ArgumentException>(() => h.CreateAsync(cfg));
    }

    [Fact]
    public async Task EmptyRenderedKeyIsRejected()
    {
        using var h = new Harness();
        var cfg = ChannelModeConfig();
        cfg.TeamsChannelKey = "{{missing.id}}";
        await Assert.ThrowsAsync<ArgumentException>(() => h.CreateAsync(cfg));
    }
}
