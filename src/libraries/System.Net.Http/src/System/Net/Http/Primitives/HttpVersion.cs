using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http.Primitives
{
    public readonly struct HttpVersion
    {
        public static HttpVersion Version10 { get; } = new HttpVersion(BitConverter.IsLittleEndian ? 0x302E312F50545448UL : 0x485454502F312E30UL);
        public static HttpVersion Version11 { get; } = new HttpVersion(BitConverter.IsLittleEndian ? 0x312E312F50545448UL : 0x485454502F312E31UL);

        internal readonly ulong _encoded;

        internal HttpVersion(ulong encoded)
        {
            _encoded = encoded;
        }
    }
}
