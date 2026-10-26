using HitlRelay.Channels.Email;
using HitlRelay.Channels.Slack;
using HitlRelay.Channels.Telegram;
using HitlRelay.Channels.WhatsApp;
using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HitlRelay.Tests;

public class KeywordMatcherTests
{
    private static readonly List<KeywordDef> Keywords = new()
    {
        new() { Key = "approve", Label = "Approve", Synonyms = { "yes", "ok" } },
        new() { Key = "reject", Label = "Reject", Synonyms = { "no" } },
    };

    [Theory]
    [InlineData("approve", "approve")]
    [InlineData("  APPROVE  ", "approve")]   // trim + case-insensitive
    [InlineData("Reject", "reject")]          // label
    [InlineData("Yes", "approve")]            // synonym
    [InlineData("NO", "reject")]
    [InlineData("2", "reject")]               // numbered fallback
    public void Matches(string input, string expectedKey) =>
        Assert.Equal(expectedKey, KeywordMatcher.Match(Keywords, input)?.Key);

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("3")]
    public void NoMatch(string? input) => Assert.Null(KeywordMatcher.Match(Keywords, input));
}

public class TemplateAndValidationTests
{
    [Fact]
    public void RendersNestedVariables()
    {
        var vars = new Dictionary<string, object?> { ["order"] = new Dictionary<string, object?> { ["id"] = "A-7" } };
        Assert.Equal("Order A-7 / ", TemplateRenderer.Render("Order {{order.id}} / {{missing.x}}", vars));
    }

    [Fact]
    public void OpenEndedValidation()
    {
        var cfg = new HitlNodeConfig { MinLength = 5, ValidationRegex = "^[A-Z]+$", ValidationMessage = "Capitals only" };
        Assert.NotNull(ReplyValidator.ValidateOpenEnded(cfg, "ab"));
        Assert.Equal("Capitals only", ReplyValidator.ValidateOpenEnded(cfg, "abcdef"));
        Assert.Null(ReplyValidator.ValidateOpenEnded(cfg, "ABCDEF"));
    }
}

public class EmailTests
{
    private static EmailTokenService Tokens(FakeTimeProvider clock) =>
        new(Options.Create(new EmailOptions { LinkSigningKey = "0123456789abcdef0123456789abcdef" }), clock);

    [Fact]
    public void TokenRoundTrip()
    {
        var clock = new FakeTimeProvider();
        var svc = Tokens(clock);
        var id = Guid.NewGuid();
        var token = svc.Create(id, "approve|x", clock.GetUtcNow().UtcDateTime.AddHours(1)); // key may contain '|'
        Assert.True(svc.TryValidate(token, out var parsedId, out var key));
        Assert.Equal(id, parsedId);
        Assert.Equal("approve|x", key);
    }

    [Fact]
    public void TamperedOrExpiredTokenRejected()
    {
        var clock = new FakeTimeProvider();
        var svc = Tokens(clock);
        var token = svc.Create(Guid.NewGuid(), "approve", clock.GetUtcNow().UtcDateTime.AddHours(1));

        Assert.False(svc.TryValidate(token[..^2] + "xx", out _, out _));
        Assert.False(svc.TryValidate("garbage", out _, out _));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.False(svc.TryValidate(token, out _, out _));
    }

    [Fact]
    public void ReplyCleanerStripsQuotesAndSignature()
    {
        var body = "Ship it to dock 4\n\nOn Mon, 5 Jan 2026 at 10:00, HITL <h@x.com> wrote:\n> Please reply\n> thanks";
        Assert.Equal("Ship it to dock 4", EmailReplyCleaner.Clean(body));
        Assert.Equal("Yes", EmailReplyCleaner.Clean("Yes\n-- \nPriya\nACME"));
    }
}

public class ForgedWebhookTests
{
    private static HttpRequest RequestWith(params (string, string)[] headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        return ctx.Request;
    }

    [Fact]
    public void SlackSignatureChecked()
    {
        var clock = new FakeTimeProvider();
        var adapter = new SlackAdapter(new HttpClient(), Options.Create(new SlackOptions { SigningSecret = "secret" }), clock);
        var body = "payload=%7B%7D";
        var ts = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var good = "v0=" + Crypto.Hex(Crypto.HmacSha256("secret", $"v0:{ts}:{body}"));

        Assert.True(adapter.VerifyInbound(RequestWith(("X-Slack-Request-Timestamp", ts), ("X-Slack-Signature", good)), body));
        Assert.False(adapter.VerifyInbound(RequestWith(("X-Slack-Request-Timestamp", ts), ("X-Slack-Signature", "v0=bad")), body));

        // Correct signature but a timestamp older than 5 minutes is rejected (replay protection).
        var oldTs = (clock.GetUtcNow().ToUnixTimeSeconds() - 600).ToString();
        var oldSig = "v0=" + Crypto.Hex(Crypto.HmacSha256("secret", $"v0:{oldTs}:{body}"));
        Assert.False(adapter.VerifyInbound(RequestWith(("X-Slack-Request-Timestamp", oldTs), ("X-Slack-Signature", oldSig)), body));
    }

    [Fact]
    public void TelegramSecretHeaderChecked()
    {
        var adapter = new TelegramAdapter(new HttpClient(), Options.Create(new TelegramOptions { WebhookSecret = "s3cret" }),
            NullLogger<TelegramAdapter>.Instance);
        Assert.True(adapter.VerifyInbound(RequestWith(("X-Telegram-Bot-Api-Secret-Token", "s3cret")), "{}"));
        Assert.False(adapter.VerifyInbound(RequestWith(("X-Telegram-Bot-Api-Secret-Token", "nope")), "{}"));
        Assert.False(adapter.VerifyInbound(RequestWith(), "{}"));
    }

    [Fact]
    public void WhatsAppHmacChecked()
    {
        var adapter = new WhatsAppAdapter(new HttpClient(), Options.Create(new WhatsAppOptions { AppSecret = "app", VerifyToken = "vt" }),
            NullLogger<WhatsAppAdapter>.Instance);
        var body = "{\"entry\":[]}";
        var good = "sha256=" + Crypto.Hex(Crypto.HmacSha256("app", body));
        Assert.True(adapter.VerifyInbound(RequestWith(("X-Hub-Signature-256", good)), body));
        Assert.False(adapter.VerifyInbound(RequestWith(("X-Hub-Signature-256", "sha256=00")), body));
        Assert.True(adapter.VerifyHandshake("vt"));
        Assert.False(adapter.VerifyHandshake("other"));
    }

    [Fact]
    public void TelegramParsesButtonAndTypedReply()
    {
        var adapter = new TelegramAdapter(new HttpClient(), Options.Create(new TelegramOptions()), NullLogger<TelegramAdapter>.Instance);
        var click = adapter.ParseInbound(RequestWith(), """
            {"update_id":10,"callback_query":{"id":"cb1","from":{"id":42,"first_name":"Priya"},"data":"h|abc123|1",
             "message":{"message_id":7,"chat":{"id":42}}}}
            """);
        Assert.NotNull(click);
        Assert.Equal("abc123", click!.CorrelationShortCode);
        Assert.Equal(1, click.ActionIndex);
        Assert.Equal("cb1", click.AckToken);

        var typed = adapter.ParseInbound(RequestWith(), """
            {"update_id":11,"message":{"message_id":8,"from":{"id":42,"first_name":"Priya"},"chat":{"id":42},
             "text":"hello","reply_to_message":{"message_id":7}}}
            """);
        Assert.Equal("7", typed!.ExternalThreadId);
        Assert.Equal("hello", typed.Text);

        // Delivery of a non-reply event yields nothing.
        Assert.Null(adapter.ParseInbound(RequestWith(), """{"update_id":12,"my_chat_member":{}}"""));
    }
}
