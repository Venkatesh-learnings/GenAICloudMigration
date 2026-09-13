using System.Security.Cryptography;

namespace UrlShortener.Domain;

public interface IShortCodeGenerator
{
    string Generate(int length = 7);
}

public class ShortCodeGenerator : IShortCodeGenerator
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string Generate(int length = 7)
    {
        Span<char> buffer = stackalloc char[length];
        Span<byte> randomBytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(randomBytes);

        for (var i = 0; i < length; i++)
        {
            buffer[i] = Alphabet[randomBytes[i] % Alphabet.Length];
        }

        return new string(buffer);
    }
}
