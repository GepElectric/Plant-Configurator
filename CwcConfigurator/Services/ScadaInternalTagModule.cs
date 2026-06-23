using System.IO;
using System.Text.RegularExpressions;

namespace CwcConfigurator.Services;

/// <summary>
/// Splits a WinCC Unified script-module export (e.g. <c>AUX_FUNCTIONS.hmi.js</c>) into
/// the part the app owns — the body of the <c>_SET_INTERNAL_TAG</c> function — and the
/// surrounding header (global definition area + the auto-generated marker + the function
/// signature) and footer (closing brace) that must be preserved verbatim on push.
///
/// Only the function body is round-tripped through <see cref="ScadaConfigFileService"/>;
/// everything else in the module (other functions, global area, the protected marker
/// comment) is kept byte-for-byte so import back into TIA stays valid.
/// </summary>
public sealed class ScadaInternalTagModule
{
    public const string DefaultFunctionName = "_SET_INTERNAL_TAG";

    private static readonly Regex SignatureRegex = new(
        @"export\s+function\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^)]*\)\s*\{",
        RegexOptions.Compiled);

    private ScadaInternalTagModule(string functionName, string header, string body, string footer)
    {
        FunctionName = functionName;
        Header = header;
        Body = body;
        Footer = footer;
    }

    /// <summary>Name of the function whose body is owned by the app.</summary>
    public string FunctionName { get; }

    /// <summary>Everything up to and including the function's opening brace.</summary>
    public string Header { get; }

    /// <summary>The function body (sections + <c>let</c> + <c>SetTagValue</c> lines).</summary>
    public string Body { get; }

    /// <summary>The closing brace and any trailing content (other functions, blank lines).</summary>
    public string Footer { get; }

    public static ScadaInternalTagModule Split(string moduleText, string functionName = DefaultFunctionName)
    {
        if (moduleText is null)
        {
            throw new ArgumentNullException(nameof(moduleText));
        }

        var match = FindFunction(moduleText, functionName)
                    ?? throw new InvalidOperationException(
                        $"Function '{functionName}' was not found in the exported script module.");

        var bodyStart = match.Index + match.Length;
        var closeIndex = FindMatchingBrace(moduleText, bodyStart - 1);
        if (closeIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the closing brace of function '{functionName}'.");
        }

        var header = moduleText.Substring(0, bodyStart);
        var body = moduleText.Substring(bodyStart, closeIndex - bodyStart);
        var footer = moduleText.Substring(closeIndex);
        return new ScadaInternalTagModule(functionName, header, body, footer);
    }

    public static ScadaInternalTagModule SplitFile(string path, string functionName = DefaultFunctionName)
    {
        return Split(File.ReadAllText(path), functionName);
    }

    /// <summary>Reassembles the module with a new function body, preserving header and footer.</summary>
    public string Rewrap(string newBody)
    {
        return Header + newBody + Footer;
    }

    private static Match? FindFunction(string text, string functionName)
    {
        foreach (Match candidate in SignatureRegex.Matches(text))
        {
            if (candidate.Groups["name"].Value.Equals(functionName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the index of the brace that closes the one at <paramref name="openBraceIndex"/>,
    /// counting nesting while skipping string literals and comments so braces inside
    /// quoted text or comments do not throw off the match.
    /// </summary>
    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        char stringQuote = '\0';

        for (var i = openBraceIndex; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (stringQuote != '\0')
            {
                if (c == '\\')
                {
                    i++; // skip escaped char
                }
                else if (c == stringQuote)
                {
                    stringQuote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    break;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    break;
                case '"':
                case '\'':
                case '`':
                    stringQuote = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }
}
