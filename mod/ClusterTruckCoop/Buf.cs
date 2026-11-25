using System.Runtime.InteropServices;
using System.Text;

namespace ClusterTruckCoop
{
    // Packet writer. This Mono's BinaryWriter.Write(float) allocates a byte[4] per call: with ~100 trucks at 30 Hz
    // that was ~60k allocations a second on the host. Same wire format as BinaryWriter (BinaryReader reads it).
    class Buf
    {
        public readonly byte[] b = new byte[2048];
        public int n;

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits { [FieldOffset(0)] public float f; [FieldOffset(0)] public int i; }

        public void Write(byte v) { b[n++] = v; }
        public void Write(sbyte v) { b[n++] = (byte)v; }
        public void Write(int v) { b[n++] = (byte)v; b[n++] = (byte)(v >> 8); b[n++] = (byte)(v >> 16); b[n++] = (byte)(v >> 24); }
        public void Write(float v) { Write(new FloatBits { f = v }.i); }

        // shortcut: one length byte, so UTF-8 text under 128 bytes only (names are capped at 20 chars); longer text needs the 7-bit length encoding
        public void Write(string s)
        {
            int len = Encoding.UTF8.GetBytes(s, 0, s.Length, b, n + 1);
            b[n] = (byte)len;
            n += len + 1;
        }
    }
}
