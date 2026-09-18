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
}
