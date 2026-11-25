// Byte-exact standard input and output on every OS: no console encoding, so no byte order mark on Windows and no
// code page between a pipe and the value.
internal static class StdIo
{
    // encrypt NAME -: stdin's bytes, byte for byte (a trailing newline included), required to be UTF-8.
    public static byte[] ReadInput()
    {
        using Stream input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        try
        {
            Config.Utf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The value on stdin is not UTF-8.");
        }
        return bytes;
    }

    // decrypt NAME: the value's bytes and nothing else, without a newline.
    public static void WriteOutput(byte[] bytes)
    {
        using Stream output = Console.OpenStandardOutput();
        output.Write(bytes);
        output.Flush();
    }
}
