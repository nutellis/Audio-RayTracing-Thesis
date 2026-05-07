using System.Drawing.Printing;
using NUnit.Framework;
using UnityEngine;

public class ButterBandPassFilterTests
{
    [Test]
    public void Process_ReturnsZero_WhenBandWidthIsZeroOrNegative()
    {
        var filter = new ButterBandPassFilter();

        Assert.AreEqual(0f, filter.Process(1f, 1000f, 0f));
        Assert.AreEqual(0f, filter.Process(1f, 1000f, -10f));
    }

    [Test]
    public void Process_ReturnsFiniteValue_ForValidBandPassSettings()
    {
        var filter = new ButterBandPassFilter();

        float output = filter.Process(1f, 1000f, 100f);

        Assert.That(output, Is.Not.NaN);
        Assert.That(output, Is.Not.EqualTo(float.PositiveInfinity));
        Assert.That(output, Is.Not.EqualTo(float.NegativeInfinity));
    }

    [Test]
    public void Process_IsDeterministic_ForFreshFiltersWithSameInputs()
    {
        const float input = 0.25f;
        const float centerFreq = 1000f;
        const float bandWidth = 100f;

        var firstFilter = new ButterBandPassFilter();
        var secondFilter = new ButterBandPassFilter();

        float firstOutput = firstFilter.Process(input, centerFreq, bandWidth);
        float secondOutput = secondFilter.Process(input, centerFreq, bandWidth);

        Assert.That(firstOutput, Is.EqualTo(secondOutput).Within(1e-6f));
    }

    [Test]
    public void Band_ShouldRespondToMatchingFrequency()
    {
        var filter = new ButterBandPassFilter();

        float sampleRate = 48000f;
        float freq = 1000f;

        float sum = 0f;
        float max = float.MinValue;

        for (int i = 0; i < 1024; i++)
        {
            float input = Mathf.Sin(2f * Mathf.PI * freq * i / sampleRate);
            float output = filter.Process(input, 1000f, 100f);

            sum += Mathf.Abs(output);
            max = Mathf.Max(max, Mathf.Abs(output));
        }

        float avg = sum / 1024f;

        TestContext.WriteLine($"Input frequency: {freq} Hz");
        TestContext.WriteLine($"Peak output: {max}");
        TestContext.WriteLine($"Average energy: {avg}");

        Assert.That(max, Is.GreaterThan(0.01f));
    }
}