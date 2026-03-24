<CsoundSynthesizer>
<CsOptions>
; no audio out to system — Unity handles output.
</CsOptions>
<CsInstruments>

sr = 48000 ; Sample rate (number of audio samples per second).
ksmps = 32 ; Number of samples in an audio vector or block of samples.
nchnls = 2 ; Number of audio channels.
0dbfs = 1 ; Which number to be set as zero decibel full scale.

instr 1
    ; 0..1 absorption coefficients from Unity.
    kAbsLow chnget "absLowCoeff"
    kAbsMid chnget "absMidCoeff"
    kAbsHigh chnget "absHighCoeff"

    ; Clamp alpha to avoid log10(0) when alpha >= 1.
    kAbsLow limit kAbsLow, 0, 0.99
    kAbsMid limit kAbsMid, 0, 0.99
    kAbsHigh limit kAbsHigh, 0, 0.99

    ; Thesis equation: Delta dB = 20 * log10(1 - alpha)
    kLowDb = 20 * log10(1 - kAbsLow)
    kMidDb = 20 * log10(1 - kAbsMid)
    kHighDb = 20 * log10(1 - kAbsHigh)

    ; Convert dB to linear amplitude and feed pareq with the linear gain value.
    kLowAmp ampdb kLowDb
    kMidAmp ampdb kMidDb
    kHighAmp ampdb kHighDb

    ; Unity host stereo input from the AudioSource on the CsoundUnity object.
    ; Keeps the original stereo image for true stereo processing.
    aInL, aInR ins

    ; Split left channel into low, mid, high bands using Butterworth bandpass filters.
    aLowL butterbp aInL, 125, 250
    aMidL butterbp aInL, 1000, 1500
    aHighL butterbp aInL, 4000, 4500

    ; Split right channel into low, mid, high bands using Butterworth bandpass filters.
    aLowR butterbp aInR, 125, 250
    aMidR butterbp aInR, 1000, 1500
    aHighR butterbp aInR, 4000, 4500

    ; Apply per-band attenuation using parametric EQ.
    ; pareq args: asig, centerFreq, gain, Q, mode
    aLowAttL pareq aLowL, 125, kLowAmp, 1.0, 0
    aMidAttL pareq aMidL, 1000, kMidAmp, 1.0, 0
    aHighAttL pareq aHighL, 4000, kHighAmp, 1.0, 0

    aLowAttR pareq aLowR, 125, kLowAmp, 1.0, 0
    aMidAttR pareq aMidR, 1000, kMidAmp, 1.0, 0
    aHighAttR pareq aHighR, 4000, kHighAmp, 1.0, 0

    ; Recombine and output true stereo.
    aOutL = aLowAttL + aMidAttL + aHighAttL
    aOutR = aLowAttR + aMidAttR + aHighAttR
    outs aOutL, aOutR
endin

</CsInstruments>
<CsScore>
i1 0 z
</CsScore>
</CsoundSynthesizer>