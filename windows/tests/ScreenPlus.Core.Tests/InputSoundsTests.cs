namespace ScreenPlus.Tests;

public class InputSoundsTests
{
    [Fact]
    public void SoundsHaveTheRightLengthAndLevel()
    {
        Assert.Equal(6240, InputSounds.MouseClick.Length);
        Assert.Equal(0.95f, InputSounds.MouseClick.Max(MathF.Abs), 4);
        Assert.Equal(6, InputSounds.RegularKeys.Length);
        Assert.Equal((int)(48_000 * 0.24), InputSounds.SpaceKey.Length);
        Assert.Equal(0.7f, InputSounds.SpaceKey.Max(MathF.Abs), 4);
        Assert.All(InputSounds.RegularKeys, k => Assert.Equal(9600, k.Length));
        // The keys differ from each other.
        Assert.NotEqual(InputSounds.RegularKeys[0], InputSounds.RegularKeys[1]);
    }

    [Fact]
    public void KeyKindsPickTheirSound()
    {
        var hits = InputSounds.KeyHits(TestData.Session());

        Assert.Same(InputSounds.RegularKeys[3], hits[0].Sound);
        Assert.Same(InputSounds.SpaceKey, hits[1].Sound);
        Assert.Same(InputSounds.EnterKey, hits[2].Sound);
    }

    [Fact]
    public void MixingInChunksMatchesMixingAtOnce()
    {
        var hits = new[]
        {
            new InputSounds.Hit(0.5, InputSounds.MouseClick),
            new InputSounds.Hit(0.52, InputSounds.SpaceKey),
            new InputSounds.Hit(1.99, InputSounds.MouseClick),  // cut off at the end
            new InputSounds.Hit(2.5, InputSounds.MouseClick),  // after the end
        };
        var track = new SoundTrack(hits, duration: 2);
        var whole = new float[2 * InputSounds.SampleRate];
        track.MixInto(0, whole);

        var chunked = new float[whole.Length];
        for (var start = 0; start < chunked.Length; start += 777)
            track.MixInto(start, chunked.AsSpan(start, Math.Min(777, chunked.Length - start)));

        Assert.Equal(whole, chunked);
        Assert.Equal(InputSounds.MouseClick[100], whole[24_000 + 100]);
        Assert.Equal(0, whole[23_999]);
        // 0.52 s is sample 24 960: the space key's 11th sample overlaps the click's 971st.
        Assert.Equal(InputSounds.MouseClick[970] + InputSounds.SpaceKey[10], whole[24_970], 5);
    }

    [Fact]
    public void PcmConversionClamps()
    {
        var pcm = new short[3];
        SoundTrack.ToPcm16([2f, -2f, 0.5f], pcm);

        Assert.Equal([short.MaxValue, -short.MaxValue, (short)16384], pcm);
    }
}
