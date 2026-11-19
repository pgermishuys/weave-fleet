using System.Text;
using QRCoder;

namespace WeaveFleet.Api;

/// <summary>
/// A QR code as text, for a terminal. Two rows of modules share one line with half blocks, so it's about as tall as
/// it is wide. Dark on light is drawn with explicit colours, so a light or a dark terminal theme makes no difference.
/// </summary>
public static class TerminalQr
{
    private const string Dark = "\u001b[30;47m";
    private const string Reset = "\u001b[0m";

    /// <summary>The lines of the code for <paramref name="text"/>, each indented by <paramref name="indent"/> spaces.</summary>
    public static IReadOnlyList<string> Lines(string text, int indent = 4)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var modules = data.ModuleMatrix;

        // A quiet zone of two modules all round (the spec asks for four; a terminal's own margin makes up the rest).
        const int quiet = 2;
        var size = modules.Count - 2 * 4; // QRCoder pads four modules; drop them and add our own.
        bool IsDark(int x, int y)
        {
            var (mx, my) = (x - quiet + 4, y - quiet + 4);
            return mx >= 4 && my >= 4 && mx < 4 + size && my < 4 + size && modules[my][mx];
        }

        var total = size + 2 * quiet;
        var pad = new string(' ', indent);
        var lines = new List<string>();
        for (var y = 0; y < total; y += 2)
        {
            var line = new StringBuilder(pad);
            line.Append(Dark);
            for (var x = 0; x < total; x++)
            {
                var top = IsDark(x, y);
                var bottom = y + 1 < total && IsDark(x, y + 1);
                // Black on white: a block is drawn in the foreground, so it is black where the module is dark.
                line.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }

            line.Append(Reset);
            lines.Add(line.ToString());
        }

        return lines;
    }
}
