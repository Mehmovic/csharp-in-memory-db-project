using System.Globalization;

using RhinoDB.Core;

namespace RhinoDB.Lib.Hosting.Test;

public class RhinoHostOptionsTests {
    [Test]
    public void Parse_WithoutTheColdPathFlag_Fails() {
        var result = RhinoHostOptions.Parse([], "game");

        Assert.That(result.IsError(), Is.True);
    }

    [Test]
    public void Parse_WithOnlyColdPath_DefaultsToRunModeAndNoUpToLsn() {
        var result = RhinoHostOptions.Parse(["--game.cold-path=/some/dir"], "game").Unwrap();

        Assert.That(result.ColdPath, Is.EqualTo("/some/dir"));
        Assert.That(result.Mode, Is.EqualTo(RhinoRunMode.Run));
        Assert.That(result.ReplayUpToLsn, Is.Null);
    }

    [Test]
    public void Parse_WithModeAndUpToLsn_ParsesBoth() {
        var result = RhinoHostOptions.Parse(["--game.cold-path=/some/dir", "--game.mode=replay", "--game.replay-upto-lsn=42"], "game").Unwrap();

        Assert.That(result.Mode, Is.EqualTo(RhinoRunMode.Replay));
        Assert.That(result.ReplayUpToLsn, Is.EqualTo(42L));
    }

    [Test]
    public void Parse_WithAnUnknownMode_Fails() {
        var result = RhinoHostOptions.Parse(["--game.cold-path=/some/dir", "--game.mode=bogus"], "game");

        Assert.That(result.IsError(), Is.True);
    }

    [Test]
    public void Parse_WithANonIntegerUpToLsn_Fails() {
        var result = RhinoHostOptions.Parse(["--game.cold-path=/some/dir", "--game.replay-upto-lsn=notanumber"], "game");

        Assert.That(result.IsError(), Is.True);
    }

    [Test]
    public void Parse_IgnoresFlagsBelongingToAnotherPrefix() {
        var result = RhinoHostOptions.Parse(
            ["--other.cold-path=/wrong/dir", "--other.mode=replay", "--game.cold-path=/right/dir"], "game").Unwrap();

        Assert.That(result.ColdPath, Is.EqualTo("/right/dir"));
        Assert.That(result.Mode, Is.EqualTo(RhinoRunMode.Run), "A different prefix's --mode must not leak into this one's options.");
    }

    [Test]
    public void Parse_IgnoresAFlagThatDoesNotMatchAnyKnownFlagName() {
        // e.g. the hosting application's own unrelated flag, like --port.
        var result = RhinoHostOptions.Parse(["--game.cold-path=/some/dir", "--port=8080"], "game").Unwrap();

        Assert.That(result.ColdPath, Is.EqualTo("/some/dir"));
    }

    // ---- prune-older-than: an operator's wall clock is read as LOCAL, not assumed UTC ----

    [Test]
    public void ResolveUtcTicks_AnUnqualifiedTimestamp_IsReadAsLocalTimeNotAsUtc() {
        // The bug this pins: treating a bare "08:00" as UTC shifts a UTC+02:00 operator's intent
        // by two hours, and a prune cutoff two hours wrong deletes the wrong segments.
        var wallClock = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock)).UtcTicks;
        var text = wallClock.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var resolved = RhinoHostOptions.ResolveUtcTicks(text).Unwrap();

        Assert.That(resolved, Is.EqualTo(expected));
    }

    [Test]
    public void ResolveUtcTicks_AZuluSuffix_IsTakenAsUtcVerbatim() {
        var text = "2026-09-14T08:00:00Z";

        var resolved = RhinoHostOptions.ResolveUtcTicks(text).Unwrap();

        Assert.That(resolved, Is.EqualTo(new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    [Test]
    public void ResolveUtcTicks_AnExplicitOffset_IsHonouredAndConvertedToUtc() {
        // +02:00 at 08:00 wall clock is 06:00 UTC - and must NOT be reinterpreted as local.
        var resolved = RhinoHostOptions.ResolveUtcTicks("2026-09-14T08:00:00+02:00").Unwrap();

        Assert.That(resolved, Is.EqualTo(new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    [Test]
    public void ResolveUtcTicks_AnExplicitOffsetWinsEvenWhenItDisagreesWithTheLocalZone() {
        // Deterministic regardless of where the test runs: -05:00 is never this machine's offset
        // in a way that could accidentally produce the same answer, and the point is the offset
        // in the text is authoritative.
        var resolved = RhinoHostOptions.ResolveUtcTicks("2026-09-14T08:00:00-05:00").Unwrap();

        Assert.That(resolved, Is.EqualTo(new DateTimeOffset(2026, 9, 14, 13, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    [Test]
    public void ResolveUtcTicks_AnUnparseableValue_FailsLoudlyWithTheFlagNameInTheMessage() {
        var result = RhinoHostOptions.ResolveUtcTicks("last tuesday-ish", "--game.wal-prune-older-than");

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        var thrown = Assert.Throws<ArgumentException>(result.ThrowIfError);
        Assert.That(thrown!.Message, Does.Contain("--game.wal-prune-older-than"),
            "the operator has to be able to tell WHICH flag was wrong, and which forms are accepted.");
    }

    [Test]
    public void ResolveUtcTicks_AMissingValue_FailsRatherThanDefaultingToSomething() {
        Assert.That(RhinoHostOptions.ResolveUtcTicks(null).IsError(), Is.True);
        Assert.That(RhinoHostOptions.ResolveUtcTicks("   ").IsError(), Is.True);
    }

    [Test]
    public void Parse_PruneOlderThanWithoutAnOffset_StoresTheLocallyResolvedUtcTicks() {
        var wallClock = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock)).UtcTicks;
        var text = wallClock.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var parsed = RhinoHostOptions.Parse(["--game.cold-path=db", $"--game.wal-prune-older-than={text}"], "game").Unwrap();

        Assert.That(parsed.WalPruneOlderThanUtcTicks, Is.EqualTo(expected));
    }
}
