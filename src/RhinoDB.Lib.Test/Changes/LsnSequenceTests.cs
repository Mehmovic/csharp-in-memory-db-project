namespace RhinoDB.Lib.Changes.Test;

public class LsnSequenceTests {
    [Test]
    public void Next_WithDefaultSeed_StartsAtOne() {
        var sequence = new LsnSequence();

        Assert.That(sequence.Next(), Is.EqualTo(1));
        Assert.That(sequence.Next(), Is.EqualTo(2));
        Assert.That(sequence.Next(), Is.EqualTo(3));
    }

    [Test]
    public void Next_WithASeed_ContinuesFromSeedPlusOne() {
        var sequence = new LsnSequence(seed: 41);

        Assert.That(sequence.Next(), Is.EqualTo(42));
        Assert.That(sequence.Next(), Is.EqualTo(43));
    }
}
