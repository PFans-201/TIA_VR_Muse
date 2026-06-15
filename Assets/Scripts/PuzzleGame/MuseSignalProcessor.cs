using System;
using System.Collections.Generic;

/// Plugin-independent EEG signal processing for the Muse S Athena — a C# port of the
/// validated Python bridge (Tools/muse_bridge.py). It takes decoded microvolt samples
/// for the four EEG channels and produces the same baseline-corrected cognitive-load
/// "stress" signal, including the Unity-coordinated dual baseline (rest + active-VR).
///
/// It does NOT touch Bluetooth — MuseDirectAdapter feeds it samples from whatever BLE
/// plugin is in use. Keeping the maths here means it can be unit-tested and reused
/// regardless of the transport, and validated by comparing its output against
/// muse_bridge.py on the same headset.
///
/// Channel order matches the BLE characteristics: 0=TP9, 1=AF7, 2=AF8, 3=TP10.
public class MuseSignalProcessor
{
    public enum BaselinePhase { Idle, Rest, ActiveVR, Streaming }

    public const int   SampleRate   = 256;        // Hz
    public const int   WindowSize   = 1024;       // 4 s @ 256 Hz, power of 2 for the FFT
    private const double RmsMin = 0.5, RmsMax = 800.0;
    private const double RailUv = 990.0;          // 12-bit saturation (~±1000 µV) => bad contact
    private const double SmoothAlpha = 0.25;
    private const int    Theta = 1, Alpha = 2;    // indices into the band vector
    private static readonly string[] ChannelNames = { "TP9", "AF7", "AF8", "TP10" };
    // [delta, theta, alpha, beta, gamma]
    private static readonly (double lo, double hi)[] Bands =
        { (1, 4), (4, 8), (8, 13), (13, 30), (30, 45) };

    public struct Reading
    {
        public BaselinePhase phase;
        public float stress;
        public float thetaZ, alphaZ, cli;
        public bool  contact;             // any channel produced a usable window
        public string usedChannels;       // e.g. "AF7+AF8"
    }

    private readonly CircularBuffer[] _buffers;
    private readonly double[] _hann;
    private readonly double _hannPower;

    private BaselinePhase _phase = BaselinePhase.Idle;
    private readonly List<double[]>[] _restAccum;
    private readonly List<double[]>[] _activeAccum;
    private double[][] _baseMean;   // per channel band means (null if no baseline)
    private double[][] _baseStd;
    private float _smoothed = 0.5f;

    public MuseSignalProcessor()
    {
        _buffers = new CircularBuffer[ChannelNames.Length];
        _restAccum = new List<double[]>[ChannelNames.Length];
        _activeAccum = new List<double[]>[ChannelNames.Length];
        for (int i = 0; i < ChannelNames.Length; i++)
        {
            _buffers[i] = new CircularBuffer(WindowSize * 2);
            _restAccum[i] = new List<double[]>();
            _activeAccum[i] = new List<double[]>();
        }
        _hann = new double[WindowSize];
        for (int n = 0; n < WindowSize; n++)
            _hann[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (WindowSize - 1)));
        foreach (var w in _hann) _hannPower += w * w;
    }

    public BaselinePhase Phase => _phase;

    // ── Sample input (called from the BLE notification callbacks) ──────────────
    public void PushSamples(int channel, IReadOnlyList<float> samples)
    {
        if (channel < 0 || channel >= _buffers.Length) return;
        _buffers[channel].Add(samples);
    }

    // ── Baseline control (called by TutorialManager) ───────────────────────────
    public void StartRestBaseline()   { _phase = BaselinePhase.Rest;     ClearAccum(_restAccum); }
    public void StopRestBaseline()    { _phase = BaselinePhase.Idle; }
    public void StartActiveBaseline() { _phase = BaselinePhase.ActiveVR; ClearAccum(_activeAccum); }

    /// Turns the collected active-VR samples into the reference baseline and switches
    /// to streaming. Returns false (and stays Idle) if no channel had a stable baseline.
    public bool FinalizeBaseline()
    {
        int maxWindows = 0;
        for (int c = 0; c < _activeAccum.Length; c++)
            maxWindows = Math.Max(maxWindows, _activeAccum[c].Count);
        int minNeeded = Math.Max(3, maxWindows / 2);

        var mean = new double[ChannelNames.Length][];
        var std  = new double[ChannelNames.Length][];
        bool any = false;
        for (int c = 0; c < _activeAccum.Length; c++)
        {
            if (_activeAccum[c].Count < minNeeded) continue;
            MeanStd(_activeAccum[c], out mean[c], out std[c]);
            any = true;
        }
        if (!any) { _phase = BaselinePhase.Idle; return false; }
        _baseMean = mean; _baseStd = std;
        _phase = BaselinePhase.Streaming;
        return true;
    }

    public void Reset()
    {
        _phase = BaselinePhase.Idle;
        _baseMean = _baseStd = null;
        _smoothed = 0.5f;
        ClearAccum(_restAccum); ClearAccum(_activeAccum);
    }

    // ── Per-tick update (call at the update rate, e.g. once a second) ──────────
    public Reading Tick()
    {
        var bp = ComputePerChannelBandPowers();   // index -> band vector for good channels

        switch (_phase)
        {
            case BaselinePhase.Rest:
                foreach (var kv in bp) _restAccum[kv.Key].Add(kv.Value);
                return new Reading { phase = _phase, stress = 0.5f, contact = bp.Count > 0,
                                     usedChannels = Join(bp.Keys) };

            case BaselinePhase.ActiveVR:
                foreach (var kv in bp) _activeAccum[kv.Key].Add(kv.Value);
                return new Reading { phase = _phase, stress = 0.5f, contact = bp.Count > 0,
                                     usedChannels = Join(bp.Keys) };

            case BaselinePhase.Streaming:
                return Stream(bp);

            default:
                return new Reading { phase = _phase, stress = 0.5f, contact = bp.Count > 0,
                                     usedChannels = Join(bp.Keys) };
        }
    }

    private Reading Stream(Dictionary<int, double[]> bp)
    {
        double tzSum = 0, azSum = 0; int n = 0;
        var used = new List<int>();
        foreach (var kv in bp)
        {
            int c = kv.Key;
            if (_baseMean == null || _baseMean[c] == null) continue;
            double[] m = _baseMean[c], s = _baseStd[c];
            tzSum += s[Theta] > 1e-9 ? (kv.Value[Theta] - m[Theta]) / s[Theta] : 0.0;
            azSum += s[Alpha] > 1e-9 ? (kv.Value[Alpha] - m[Alpha]) / s[Alpha] : 0.0;
            used.Add(c); n++;
        }
        if (n == 0)
            return new Reading { phase = _phase, stress = _smoothed, contact = false,
                                 usedChannels = "" };

        float thetaZ = (float)(tzSum / n);
        float alphaZ = (float)(azSum / n);
        float cli    = (thetaZ - alphaZ) / 2f;
        float raw    = 1f / (1f + (float)Math.Exp(-cli / Sensitivity));
        float a      = (float)SmoothAlpha;
        _smoothed = Math.Max(0f, Math.Min(1f, a * raw + (1f - a) * _smoothed));
        return new Reading { phase = _phase, stress = _smoothed, thetaZ = thetaZ,
                             alphaZ = alphaZ, cli = cli, contact = true,
                             usedChannels = Join(used) };
    }

    /// Stress sensitivity — higher = a stronger z-score is needed to move off 0.5.
    public float Sensitivity = 1.5f;

    /// Exposed for the numerical cross-check against the Python bridge.
    /// `window.Length` must equal WindowSize.
    public double[] ComputeBandPowers(double[] window)
    {
        if (window.Length != WindowSize)
            throw new ArgumentException($"window must be {WindowSize} samples");
        return BandPowers(window);
    }

    // ── DSP ────────────────────────────────────────────────────────────────────
    private Dictionary<int, double[]> ComputePerChannelBandPowers()
    {
        var result = new Dictionary<int, double[]>();
        var window = new double[WindowSize];
        for (int c = 0; c < _buffers.Length; c++)
        {
            if (!_buffers[c].CopyLast(window)) continue;   // not enough samples yet

            double sumSq = 0, rail = 0;
            foreach (var v in window) { sumSq += v * v; if (Math.Abs(v) >= RailUv) rail++; }
            double rms = Math.Sqrt(sumSq / WindowSize);
            if (rail / WindowSize >= 0.10 || rms < RmsMin || rms > RmsMax) continue;

            double[] bp = BandPowers(window);
            bool finite = true;
            foreach (var b in bp) if (double.IsNaN(b) || double.IsInfinity(b)) finite = false;
            if (finite) result[c] = bp;
        }
        return result;
    }

    private double[] BandPowers(double[] samples)
    {
        // detrend + Hann window
        double mean = 0; foreach (var v in samples) mean += v; mean /= samples.Length;
        var re = new double[WindowSize];
        var im = new double[WindowSize];
        for (int i = 0; i < WindowSize; i++) re[i] = (samples[i] - mean) * _hann[i];

        Fft(re, im);   // in-place radix-2

        int half = WindowSize / 2;
        var psd = new double[half + 1];
        double norm = SampleRate * _hannPower;
        for (int k = 0; k <= half; k++)
        {
            double p = (re[k] * re[k] + im[k] * im[k]) / norm;
            if (k != 0 && k != half) p *= 2.0;            // one-sided
            psd[k] = p;
        }

        double df = (double)SampleRate / WindowSize;      // Hz per bin
        var bands = new double[Bands.Length];
        for (int b = 0; b < Bands.Length; b++)
        {
            double sum = 0;
            for (int k = 0; k <= half; k++)
            {
                double f = k * df;
                if (f >= Bands[b].lo && f < Bands[b].hi) sum += psd[k];
            }
            bands[b] = sum * df;
        }
        return bands;
    }

    /// In-place iterative radix-2 Cooley–Tukey FFT (length must be a power of two).
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1.0, curIm = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, bIdx = i + k + len / 2;
                    double tRe = re[bIdx] * curRe - im[bIdx] * curIm;
                    double tIm = re[bIdx] * curIm + im[bIdx] * curRe;
                    re[bIdx] = re[a] - tRe; im[bIdx] = im[a] - tIm;
                    re[a] += tRe;           im[a] += tIm;
                    double nRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                }
            }
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────
    private static void ClearAccum(List<double[]>[] accum)
    {
        foreach (var list in accum) list.Clear();
    }

    private static void MeanStd(List<double[]> samples, out double[] mean, out double[] std)
    {
        int bands = samples[0].Length, n = samples.Count;
        mean = new double[bands]; std = new double[bands];
        for (int b = 0; b < bands; b++)
        {
            double sum = 0; foreach (var s in samples) sum += s[b];
            mean[b] = sum / n;
            double var = 0; foreach (var s in samples) var += (s[b] - mean[b]) * (s[b] - mean[b]);
            std[b] = Math.Sqrt(var / n);
        }
    }

    private static string Join(IEnumerable<int> channels)
    {
        var names = new List<string>();
        foreach (var c in channels) names.Add(ChannelNames[c]);
        return string.Join("+", names);
    }

    /// Decodes one 20-byte Muse EEG notification into 6 microvolt samples
    /// (2-byte sequence header + 6 × 12-bit, scaled (raw − 0x800) × 125/256).
    public static float[] DecodePacket(byte[] payload)
    {
        var outv = new float[12];
        int o = 0;
        for (int i = 2; i < 20; i += 3)
        {
            int v1 = (payload[i] << 4) | (payload[i + 1] >> 4);
            int v2 = ((payload[i + 1] & 0xF) << 8) | payload[i + 2];
            outv[o++] = (float)((v1 - 0x800) * 125.0 / 256.0);
            outv[o++] = (float)((v2 - 0x800) * 125.0 / 256.0);
        }
        return outv;
    }

    // Minimal circular buffer of floats (single-producer via PushSamples, consumed on Tick).
    private class CircularBuffer
    {
        private readonly float[] _data;
        private int _head;      // next write position
        private int _count;
        private readonly object _lock = new object();

        public CircularBuffer(int capacity) { _data = new float[capacity]; }

        public void Add(IReadOnlyList<float> samples)
        {
            lock (_lock)
            {
                foreach (var s in samples)
                {
                    _data[_head] = s;
                    _head = (_head + 1) % _data.Length;
                    if (_count < _data.Length) _count++;
                }
            }
        }

        /// Copies the most recent dst.Length samples in chronological order.
        /// Returns false if fewer than dst.Length samples are available.
        public bool CopyLast(double[] dst)
        {
            lock (_lock)
            {
                int need = dst.Length;
                if (_count < need) return false;
                int start = (_head - need + _data.Length) % _data.Length;
                for (int i = 0; i < need; i++)
                    dst[i] = _data[(start + i) % _data.Length];
                return true;
            }
        }
    }
}
