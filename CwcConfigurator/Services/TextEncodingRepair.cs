using System.Text;

namespace CwcConfigurator.Services;

/// <summary>
/// Repairs texts that went through a UTF-8 → Windows-1252 misread (once or more): "mÃ¥ling" / "mÃƒÂ¥ling" →
/// "måling". TIA read the pushed DB sources (UTF-8 without BOM) as ANSI, so every push → pull round added a layer.
/// A text is changed only when its Windows-1252 bytes are valid UTF-8, so correct texts ("Grüße") stay as they are.
/// </summary>
public static class TextEncodingRepair
{
    private static readonly Encoding Ansi = CreateAnsi();
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Fix(string? value)
    {
        var text = value ?? "";
        for (var round = 0; round < 4 && NeedsCheck(text); round++)
        {
            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(Ansi.GetBytes(text));
            }
            catch (Exception ex) when (ex is EncoderFallbackException or DecoderFallbackException or ArgumentException)
            {
                break;
            }

            if (decoded == text)
            {
                break;
            }

            text = decoded;
        }

        return text;
    }

    // Mojibake of a UTF-8 sequence always starts with a Windows-1252 lead byte character (Â, Ã, Å, Ä, Ð …).
    private static bool NeedsCheck(string text)
    {
        foreach (var c in text)
        {
            if (c is >= 'Â' and <= 'ô')
            {
                return true;
            }
        }

        return false;
    }

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
