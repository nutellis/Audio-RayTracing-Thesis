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
    kAbs125Hz chnget "abs125HzCoeff"
    kAbs250Hz chnget "abs250HzCoeff"
    kAbs500Hz chnget "abs500HzCoeff"
    kAbs1000Hz chnget "abs1000HzCoeff"
    kAbs2000Hz chnget "abs2000HzCoeff"
    kAbs4000Hz chnget "abs4000HzCoeff"

    kCenterFreq125Hz chnget "kCenterFreq125Hz"
    kCenterFreq250Hz chnget "kCenterFreq250Hz"
    kCenterFreq500Hz chnget "kCenterFreq500Hz"
    kCenterFreq1000Hz chnget "kCenterFreq1000Hz"
    kCenterFreq2000Hz chnget "kCenterFreq2000Hz"
    kCenterFreq4000Hz chnget "kCenterFreq4000Hz"

    kBand125Hz chnget "kBand125Hz"
    kBand250Hz chnget "kBand250Hz"
    kBand500Hz chnget "kBand500Hz"
    kBand1000Hz chnget "kBand1000Hz"
    kBand2000Hz chnget "kBand2000Hz"
    kBand4000Hz chnget "kBand4000Hz"

    ; Clamp alpha values to 0..1 to avoid invalid log10() calculations and negative gain.
    kAbs125Hz limit kAbs125Hz, 0, 1
    kAbs250Hz limit kAbs250Hz, 0, 1
    kAbs500Hz limit kAbs500Hz, 0, 1
    kAbs1000Hz limit kAbs1000Hz, 0, 1
    kAbs2000Hz limit kAbs2000Hz, 0, 1
    kAbs4000Hz limit kAbs4000Hz, 0, 1

    ; Paper equation: Delta dB = 20 * log10(1 - alpha)
    k125HzDb = 20 * log10(1 - kAbs125Hz)
    k250HzDb = 20 * log10(1 - kAbs250Hz)
    k500HzDb = 20 * log10(1 - kAbs500Hz)
    k1000HzDb = 20 * log10(1 - kAbs1000Hz)
    k2000HzDb = 20 * log10(1 - kAbs2000Hz)
    k4000HzDb = 20 * log10(1 - kAbs4000Hz)

    ; Convert dB to linear amplitude gain for direct scaling of each band.
    k125HzAmp ampdb k125HzDb
    k250HzAmp ampdb k250HzDb
    k500HzAmp ampdb k500HzDb
    k1000HzAmp ampdb k1000HzDb
    k2000HzAmp ampdb k2000HzDb
    k4000HzAmp ampdb k4000HzDb

    ; Unity host stereo input from the AudioSource on the CsoundUnity object.
    ; Keeps the original stereo image for true stereo processing.
    aInL, aInR ins

    ; Test input: sum six sine tones to verify each frequency band attenuation.
    ; Keep per-tone amplitude low so the summed signal stays below full scale.
    ; a125Test oscili 0.8, 125
    ; a250Test oscili 0.8, 250
    ; a500Test oscili 0.8, 500
    ; a1000Test oscili 0.8, 1000
    ; a2000Test oscili 0.8, 2000
    ; a4000Test oscili 0.8, 4000
    ; aTestMix = a125Test + a250Test + a500Test + a1000Test + a2000Test + a4000Test
    ; aInL = aTestMix
    ; aInR = aTestMix


    ; Split left channel into 125Hz, 250Hz, 500Hz, 1000Hz, 2000Hz, and 4000Hz bands using Butterworth bandpass filters.
    a125HzL butterbp aInL, kCenterFreq125Hz, kBand125Hz
    a250HzL butterbp aInL, kCenterFreq250Hz, kBand250Hz
    a500HzL butterbp aInL, kCenterFreq500Hz, kBand500Hz
    a1000HzL butterbp aInL, kCenterFreq1000Hz, kBand1000Hz
    a2000HzL butterbp aInL, kCenterFreq2000Hz, kBand2000Hz
    a4000HzL butterbp aInL, kCenterFreq4000Hz, kBand4000Hz

    ; Split right channel into 125Hz, 250Hz, 500Hz, 1000Hz, 2000Hz, and 4000Hz bands using Butterworth bandpass filters.
    a125HzR butterbp aInR, kCenterFreq125Hz, kBand125Hz
    a250HzR butterbp aInR, kCenterFreq250Hz, kBand250Hz
    a500HzR butterbp aInR, kCenterFreq500Hz, kBand500Hz
    a1000HzR butterbp aInR, kCenterFreq1000Hz, kBand1000Hz
    a2000HzR butterbp aInR, kCenterFreq2000Hz, kBand2000Hz
    a4000HzR butterbp aInR, kCenterFreq4000Hz, kBand4000Hz

    ; Apply per-band attenuation by directly scaling each band with linear gain.
    a125HzAttL = a125HzL * k125HzAmp
    a250HzAttL = a250HzL * k250HzAmp
    a500HzAttL = a500HzL * k500HzAmp
    a1000HzAttL = a1000HzL * k1000HzAmp
    a2000HzAttL = a2000HzL * k2000HzAmp
    a4000HzAttL = a4000HzL * k4000HzAmp

    a125HzAttR = a125HzR * k125HzAmp
    a250HzAttR = a250HzR * k250HzAmp
    a500HzAttR = a500HzR * k500HzAmp
    a1000HzAttR = a1000HzR * k1000HzAmp
    a2000HzAttR = a2000HzR * k2000HzAmp
    a4000HzAttR = a4000HzR * k4000HzAmp

    ; Recombine and output true stereo.
    aOutL = a125HzAttL + a250HzAttL + a500HzAttL + a1000HzAttL + a2000HzAttL + a4000HzAttL
    aOutR = a125HzAttR + a250HzAttR + a500HzAttR + a1000HzAttR + a2000HzAttR + a4000HzAttR
    outs aOutL, aOutR
endin

</CsInstruments>
<CsScore>
i1 0 z
</CsScore>
</CsoundSynthesizer>