<CsoundSynthesizer>
<CsOptions>
; no audio out to system — Unity handles output
</CsOptions>
<CsInstruments>

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

instr 1
    ; 0..1 absorption coefficients from Unity.
    kAbsLow chnget "absLowCoeff"
    kAbsMid chnget "absMidCoeff"
    kAbsHigh chnget "absHighCoeff"

    ; Clamp them to 0..1
    kAbsLow limit kAbsLow, 0, 1
    kAbsMid limit kAbsMid, 0, 1
    kAbsHigh limit kAbsHigh, 0, 1

    ; Map absorption to attenuation in dB.
    kLowDb = -24 * kAbsLow
    kMidDb = -24 * kAbsMid
    kHighDb = -24 * kAbsHigh

    ; Test source. Replace with ins input later if needed.
    aSrc oscili 0.3, 440

    ; Split into low, mid, high bands using Butterworth bandpass filters.
    aLow butterbp aSrc, 125, 250 ; from 0 to 250 Hz
    aMid butterbp aSrc, 1000, 1500 ; from 250 to 1750 Hz
    aHigh butterbp aSrc, 4000, 4500 ; from 1750 Hz to 6250 Hz

    ; Apply per-band attenuation using parametric EQ.
    ; pareq args: asig, centerFreq, gainDb, Q, mode
    aLowAtt pareq aLow, 125, kLowDb, 1.0, 0
    aMidAtt pareq aMid, 1000, kMidDb, 1.0, 0
    aHighAtt pareq aHigh, 4000, kHighDb, 1.0, 0

    ; Recombine and output.
    aOut = aLowAtt + aMidAtt + aHighAtt
    outs aOut, aOut
endin

</CsInstruments>
<CsScore>
i1 0 z
</CsScore>
</CsoundSynthesizer>