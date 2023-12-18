// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Utilities.Encoding.Wav
{
    internal static class Constants
    {
        public const int WavHeaderSize = 44;
        private const string RIFF = "RIFF";
        public static char[] RIFF_ARRAY { get; } = RIFF.ToCharArray();
        private const string WAVE = "WAVE";
        public static char[] WAVE_ARRAY { get; } = WAVE.ToCharArray();
        private const string FMT = "fmt ";
        public static char[] FMT_ARRAY { get; } = FMT.ToCharArray();
        private const string DATA = "data";
        public static char[] DATA_ARRAY { get; } = DATA.ToCharArray();
    }
}
