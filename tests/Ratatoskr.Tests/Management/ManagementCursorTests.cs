using AwesomeAssertions;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Tests.Management;

public class ManagementCursorTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 11, 12, 34, 56, 789, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    [Test]
    public void Encode_RoundTrips()
    {
        var encoded = new ManagementCursor(Instant, Id).Encode();

        ManagementCursor.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(new ManagementCursor(Instant, Id));
    }

    [Test]
    public void Encode_IsUrlSafeAndUnpadded()
    {
        // The cursor travels in a query string, so it must survive without escaping.
        var encoded = new ManagementCursor(Instant, Id).Encode();

        encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        Uri.EscapeDataString(encoded).Should().Be(encoded);
    }

    [Test]
    public void Encode_NormalisesToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 11, 14, 34, 56, 789, TimeSpan.FromHours(2));

        ManagementCursor.TryDecode(new ManagementCursor(local, Id).Encode(), out var decoded)
            .Should()
            .BeTrue();

        decoded!.Value.CreatedAt.Should().Be(local.ToUniversalTime());
        decoded.Value.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void Encode_OrdersLexicographicallyByTimestamp()
    {
        // Big-endian ticks mean a later row never sorts before an earlier one, which keeps the
        // encoded form debuggable: sorting a column of cursors sorts the rows they point at.
        var earlier = new ManagementCursor(Instant, Id).Encode();
        var later = new ManagementCursor(Instant.AddSeconds(1), Id).Encode();

        string.CompareOrdinal(earlier, later).Should().BeNegative();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task TryDecode_TreatsAbsentCursorAsStartOfList(string? absent)
    {
        ManagementCursor.TryDecode(absent, out var decoded).Should().BeTrue();
        decoded.Should().BeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("not base64 at all!")]
    [Arguments("AAAA")] // valid base64, wrong length
    [Arguments("A")] // impossible base64 length
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // right encoding, too long
    public async Task TryDecode_RejectsMalformedInput(string malformed)
    {
        ManagementCursor.TryDecode(malformed, out var decoded).Should().BeFalse();
        decoded.Should().BeNull();
        await Task.CompletedTask;
    }

    [Test]
    public void TryDecode_RejectsAnUnknownLayoutVersion()
    {
        // Byte 0 is the layout version. A cursor minted by a future build must be refused, not
        // silently reinterpreted as a timestamp from the wrong epoch.
        var bytes = Convert.FromBase64String(Pad(new ManagementCursor(Instant, Id).Encode()));
        bytes[0] = ManagementCursor.Version + 1;
        var forged = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        ManagementCursor.TryDecode(forged, out var decoded).Should().BeFalse();
        decoded.Should().BeNull();
    }

    private static string Pad(string urlSafe)
    {
        var normalized = urlSafe.Replace('-', '+').Replace('_', '/');
        return (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };
    }
}
