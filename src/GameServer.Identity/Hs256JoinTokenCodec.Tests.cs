using System.Security.Cryptography;
using System.Text;
using GameServer.Identity.Testing;
using Xunit;

namespace GameServer.Identity;

public sealed class Hs256JoinTokenCodecTests
{
    private const string Secret = "test-signing-secret-please-rotate";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static Hs256JoinTokenCodec Codec(IClock clock, string secret = Secret) =>
        new(secret, clock, Ttl);

    [Fact]
    public void Issue_ThenVerify_RoundTripsClaims()
    {
        var clock = new FakeClock(Start);
        var codec = Codec(clock);

        var token = codec.Issue("tenant-a", "demo-game", "room-1", "player-1");
        var result = codec.Verify(token);

        Assert.True(result.IsOk);
        var claims = result.Claims!;
        Assert.Equal("tenant-a", claims.TenantId);
        Assert.Equal("demo-game", claims.GameId);
        Assert.Equal("room-1", claims.RoomId);
        Assert.Equal("player-1", claims.PlayerId);
        Assert.Equal(Start, claims.IssuedAtUtc);
        Assert.Equal(Start.Add(Ttl), claims.ExpiresAtUtc);
    }

    [Fact]
    public void Verify_AtExpiryBoundary_StillValid_ButRejectsAfter()
    {
        var clock = new FakeClock(Start);
        var codec = Codec(clock);
        var token = codec.Issue("tenant-a", "demo-game", "room-1", "player-1");

        clock.Set(Start.Add(Ttl)); // exactly at expiry — still valid
        Assert.True(codec.Verify(token).IsOk);

        clock.Advance(TimeSpan.FromSeconds(1)); // one second past expiry
        Assert.Equal(TokenVerificationStatus.Expired, codec.Verify(token).Status);
    }

    [Fact]
    public void Verify_TamperedPayload_FailsSignature()
    {
        var clock = new FakeClock(Start);
        var codec = Codec(clock);
        var token = codec.Issue("tenant-a", "demo-game", "room-1", "player-1");

        // Flip a character in the payload segment; the signature no longer matches.
        var parts = token.Split('.');
        var tamperedPayload = parts[1][0] == 'A' ? 'B' + parts[1][1..] : 'A' + parts[1][1..];
        var tampered = parts[0] + "." + tamperedPayload + "." + parts[2];

        Assert.Equal(TokenVerificationStatus.BadSignature, codec.Verify(tampered).Status);
    }

    [Fact]
    public void Verify_TokenSignedWithDifferentSecret_FailsSignature()
    {
        var clock = new FakeClock(Start);
        var token = Codec(clock, secret: "attacker-secret").Issue("tenant-a", "demo-game", "room-1", "player-1");

        // A verifier holding the real secret must reject a foreign-signed token.
        Assert.Equal(TokenVerificationStatus.BadSignature, Codec(clock).Verify(token).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.@@@.###")] // not valid base64url
    public void Verify_Malformed_IsRejected(string? token)
    {
        var codec = Codec(new FakeClock(Start));

        Assert.Equal(TokenVerificationStatus.Malformed, codec.Verify(token).Status);
    }

    [Fact]
    public void Verify_WellFormedSignatureOverGarbageJson_IsMalformed()
    {
        // A correctly-signed token whose payload is not valid claims JSON must be
        // reported as malformed, not Ok — signature validity is necessary, not sufficient.
        var clock = new FakeClock(Start);
        var codec = Codec(clock);
        var real = codec.Issue("tenant-a", "demo-game", "room-1", "player-1");
        var header = real.Split('.')[0];

        var forged = SignWellFormedButGarbage(header, rawPayload: "this-is-not-json", Secret);

        Assert.Equal(TokenVerificationStatus.Malformed, codec.Verify(forged).Status);
    }

    // Builds a token whose signature is valid for the given secret but whose payload
    // segment decodes to non-JSON — to prove a valid signature alone is not accepted.
    private static string SignWellFormedButGarbage(string headerSegment, string rawPayload, string secret)
    {
        var payloadSegment = Base64Url(Encoding.UTF8.GetBytes(rawPayload));
        var signingInput = headerSegment + "." + payloadSegment;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + signature;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
