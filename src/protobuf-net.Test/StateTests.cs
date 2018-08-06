using System;
using Xunit;
#if PLAT_SPANS
namespace ProtoBuf
{
    public class StateTests
    {
        [Theory]
        [InlineData("0400000000", 4, 1)]
        [InlineData("04FFFFFFFF", 4, 1)]
        [InlineData("8100FFFFFF", 1, 2)]
        [InlineData("8101FFFFFF", 129, 2)]
        [InlineData("8080808001", 1 << 28, 5)]
        public void ReadFieldHeader(string hex, uint expectedTag, int expectedBytes)
        {
            ProtoReader.State state = default;
            state.Init(FromHex(hex));
            int actualBytes = state.ReadVarintUInt32(out var actualTag);
            Assert.Equal(expectedTag, actualTag);
            Assert.Equal(expectedBytes, actualBytes);
        }
        private static byte[] FromHex(string hex)
        {
            byte[] b = new byte[Math.Max(5, hex.Length / 2)];
            for (int i = 0; i < hex.Length / 2; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
#endif