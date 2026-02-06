namespace AaoDcsBiosRuntimeBridge;

public static class DcsBiosFrames
{
    private static readonly byte[] Sync = { 0x55, 0x55, 0x55, 0x55 };

    /// <summary>
    /// Legacy helper: one complete frame (SYNC + one u16 write access).
    /// </summary>
    public static byte[] MakeWriteN(ushort address, ushort word)
        => MakeFrame(new[] { MakeWriteAccessU16(address, word) });

    /// <summary>
    /// Legacy helper: one complete frame (SYNC + one byte write access).
    /// </summary>
    public static byte[] MakeWriteBytes(ushort address, byte[] payload)
        => MakeFrame(new[] { MakeWriteAccessBytes(address, payload) });

    /// <summary>
    /// Build a complete DCS-BIOS export frame: SYNC + concatenated write-access records.
    /// </summary>
    public static byte[] MakeFrame(IEnumerable<byte[]> writeAccesses)
    {
        int waLen = 0;
        foreach (var wa in writeAccesses)
            waLen += wa.Length;

        var frame = new byte[Sync.Length + waLen];
        Buffer.BlockCopy(Sync, 0, frame, 0, Sync.Length);

        int offset = Sync.Length;
        foreach (var wa in writeAccesses)
        {
            Buffer.BlockCopy(wa, 0, frame, offset, wa.Length);
            offset += wa.Length;
        }
        return frame;
    }

    /// <summary>
    /// DCS-BIOS write access record (NO SYNC):
    /// addr(lo,hi) + count(lo,hi) + payload bytes.
    /// For u16 write: count=0x0002, payload is u16 little-endian.
    /// </summary>
    public static byte[] MakeWriteAccessU16(ushort address, ushort word)
    {
        var wa = new byte[2 + 2 + 2];
        wa[0] = (byte)(address & 0xFF);
        wa[1] = (byte)((address >> 8) & 0xFF);

        wa[2] = 0x02; // count = 2 bytes
        wa[3] = 0x00;

        wa[4] = (byte)(word & 0xFF);
        wa[5] = (byte)((word >> 8) & 0xFF);
        return wa;
    }

    /// <summary>
    /// DCS-BIOS write access record (NO SYNC):
    /// addr(lo,hi) + count(lo,hi) + payload bytes.
    /// </summary>
    public static byte[] MakeWriteAccessBytes(ushort address, byte[] payload)
    {
        int len = payload.Length;
        if (len > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(payload), "payload too large");

        var wa = new byte[2 + 2 + len];
        wa[0] = (byte)(address & 0xFF);
        wa[1] = (byte)((address >> 8) & 0xFF);

        wa[2] = (byte)(len & 0xFF);
        wa[3] = (byte)((len >> 8) & 0xFF);

        Buffer.BlockCopy(payload, 0, wa, 4, len);
        return wa;
    }
}
