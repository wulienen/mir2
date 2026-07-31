using NAudio.Wave;

namespace Client.MirSounds.Libraries
{
    internal static class AudioStreamFactory
    {
        public static WaveStream Create(Stream stream, string extension)
        {
            return extension?.ToLowerInvariant() switch
            {
                ".wav" => new WaveFileReader(stream),
                ".mp3" => new Mp3FileReader(stream),
                _ => throw new InvalidDataException($"Unsupported streamed audio type: {extension}")
            };
        }
    }
}
