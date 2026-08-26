namespace Fiesta.Emu.Zone.Random;

/// <summary>The server's RNG — `cWell512Random`, a WELL512 variant.
///
/// <para>Mob skill selection draws from this (`MobActionAttack::mab_Think` calls
/// `well512_GetRandom`), so a simulation that wants reproducible runs needs the same generator producing
/// the same sequence from the same state. A different RNG gives a plausible-looking simulation that
/// diverges from the server on every branch that consults chance.</para>
///
/// <para>Transcribed instruction-by-instruction from `0x0063CA00` rather than from the published WELL512a
/// reference, because the shift/xor sequence here does <b>not</b> match the textbook form and porting the
/// textbook version would have been subtly wrong. Verified against the real function under emulation —
/// see `tools/verify_well512.py`.</para></summary>
public sealed class cWell512Random
{
    /// <summary>16 x uint32 at `this+0x00`, with the index at `this+0x40`.</summary>
    private readonly uint[] _state = new uint[16];
    private uint _index;

    /// <summary>Scratch the original keeps as fields at `+0x44`, `+0x48`, `+0x4C`. Kept as fields rather
    /// than locals so the layout matches, and because `z1` is stored BEFORE it is combined with `z2` and
    /// the stored value is what the second phase reads back — a detail that is easy to lose when
    /// rewriting this as expressions.</summary>
    private uint _z0, _z1, _z2;

    public cWell512Random(uint[] state, uint index = 0)
    {
        if (state.Length != 16)
            throw new ArgumentException("WELL512 state is 16 words", nameof(state));
        Array.Copy(state, _state, 16);
        _index = index & 15;
    }

    /// <summary>`cWell512Random::well512_GetRandom()` — the no-argument overload, returning a double in
    /// [0, 1). The final step reads the new state word as UNSIGNED (the original adds 2^32 when the signed
    /// load came out negative) and scales by 1/2^32.</summary>
    public double well512_GetRandom()
    {
        var i = _index;

        _z0 = _state[(i - 1) & 15];

        var v = _state[i];
        var vm1 = _state[(i - 3) & 15];

        // (((v << 1) ^ vm1) << 15) ^ vm1 ^ v -- literally the +0x1C..+0x2C sequence.
        var t = ((((v + v) ^ vm1) << 15) ^ vm1) ^ v;
        _z1 = t;                                   // stored BEFORE the z2 combination

        var s7 = _state[(i - 7) & 15];
        _z2 = (s7 >> 11) ^ s7;

        _state[i] = t ^ _z2;

        var newV = _state[i];
        var z1 = _z1;                              // the pre-combination value, as the original reads back
        var z2 = _z2;

        var b = ((z2 << 10) ^ z1) << 13;
        b ^= newV & 0xFED22169u;
        b = (b << 3) ^ _z0;
        b = (b << 2) ^ _z0;
        b ^= newV;
        b ^= z1;

        _index = (i - 1) & 15;
        _state[_index] = b;

        return _state[_index] * (1.0 / 4294967296.0);
    }

    /// <summary>`cWell512Random::well512_GetRandom(unsigned int)` — a draw in [0, n).
    ///
    /// <para>Not a straightforward scale: the original multiplies the [0,1) double by <b>1e11</b>,
    /// truncates it into a 64-bit integer with the FPU rounding mode forced to chop, keeps only the
    /// <b>low 32 bits</b>, and takes the unsigned remainder modulo n.</para>
    ///
    /// <para>⚠️ `n == 0` executes a `div` by zero in the original, which faults. Nothing here is gained by
    /// inventing a safe answer the server does not give, so this throws.</para></summary>
    public uint well512_GetRandom(uint n)
    {
        if (n == 0)
            throw new DivideByZeroException("well512_GetRandom(0) divides by zero in the original");

        var scaled = well512_GetRandom() * 100_000_000_000.0;   // 1e11, the constant at 0x6C48F8
        var low = unchecked((uint)(long)scaled);                // truncate, then keep the low dword
        return low % n;
    }
}
