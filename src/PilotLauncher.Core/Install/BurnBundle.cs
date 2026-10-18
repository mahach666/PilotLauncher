namespace PilotLauncher.Core.Install;

/// <summary>
/// WiX Burn setup executables carry their payloads in a Cabinet appended to the exe. This finds the biggest one
/// and copies it out; no code from the setup is run.
/// </summary>
public static class BurnBundle
{
    private const int HeaderSize = 36;
    private static readonly byte[] Signature = "MSCF\0\0\0\0"u8.ToArray();

    public static (long Offset, long Size)? FindLargestCabinet(string exePath)
    {
        using var file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var length = file.Length;
        var buffer = new byte[(1 << 20) + Signature.Length];
        var carry = 0;
        long bufferStart = 0;
        (long Offset, long Size)? best = null;

        while (true)
        {
            var read = file.Read(buffer, carry, buffer.Length - carry);
            if (read == 0)
                break;
            var filled = carry + read;
            var span = buffer.AsSpan(0, filled);
            var from = 0;
            int hit;
            while ((hit = span[from..].IndexOf(Signature)) >= 0)
            {
                var position = bufferStart + from + hit;
                if (TryReadHeader(file, position, length, out var size) && (best is null || size > best.Value.Size))
                    best = (position, size);
                from += hit + 1;
            }

            // keep the tail so a signature split across two reads is still found
            carry = Math.Min(Signature.Length - 1, filled);
            buffer.AsSpan(filled - carry, carry).CopyTo(buffer);
            bufferStart += filled - carry;
        }

        return best;
    }

    public static void ExtractLargestCabinet(string exePath, string cabinetPath)
    {
        var (offset, size) = FindLargestCabinet(exePath)
                             ?? throw new InvalidDataException($"No embedded cabinet found in {exePath}; not a WiX Burn setup?");
        using var source = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        source.Position = offset;
        using var target = new FileStream(cabinetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        var left = size;
        while (left > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of {exePath}.");
            target.Write(buffer, 0, read);
            left -= read;
        }
    }

    private static bool TryReadHeader(FileStream file, long position, long fileLength, out long size)
    {
        size = 0;
        if (position + HeaderSize > fileLength)
            return false;
        Span<byte> header = stackalloc byte[HeaderSize];
        var saved = file.Position;
        try
        {
            file.Position = position;
            file.ReadExactly(header);
        }
        finally
        {
            file.Position = saved;
        }

        // reserved1/2/3 are zero, format version is 1.3, the cabinet lies inside the file
        if (BitConverter.ToUInt32(header[12..]) != 0 || BitConverter.ToUInt32(header[20..]) != 0 || header[24] != 3 || header[25] != 1)
            return false;
        size = BitConverter.ToUInt32(header[8..]);
        var filesOffset = BitConverter.ToUInt32(header[16..]);
        return size >= HeaderSize && filesOffset < size && position + size <= fileLength;
    }
}
