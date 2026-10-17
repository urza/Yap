using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Yap.LoadTest.Orchestrator;

/// <summary>
/// Full-screen drawing on the terminal's alternate buffer, the way top and htop draw: every frame starts at the
/// top-left corner, overwrites the last one line by line, and clears the rest of the screen. Spectre's Live display
/// moves the cursor up by the height of the previous frame instead; after a resize, or a minimised and restored
/// window in Windows Terminal, that count is wrong and the frames pile up under each other. A frame drawn from the
/// corner cannot pile up. On exit the person's own screen comes back, and the summary prints there.
/// </summary>
public sealed class Screen : IDisposable
{
    private readonly StringWriter _text = new();
    private readonly IAnsiConsole _renderer;
    private readonly TextWriter _out = Console.Out;

    /// <summary>True when stdout is a terminal that understands ANSI sequences. On Windows this call also turns the console's ANSI mode on.</summary>
    public static bool Supported => !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi;

    public Screen()
    {
        // Spectre renders the frame to a string, colours included; this class owns the cursor.
        _renderer = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(_text),
        });
        _out.Write("\e[?1049h\e[?25l");   // the alternate buffer, cursor hidden
        _out.Flush();
    }

    public void Draw(IRenderable frame)
    {
        var (width, height) = Size();
        // One column short of the edge: a line that reaches the last column makes some consoles wrap at once, and
        // the frame would be one row taller than counted. Spectre wraps longer text at this width, never the terminal.
        _renderer.Profile.Width = width - 1;
        _text.GetStringBuilder().Clear();
        _renderer.Write(frame);
        var lines = _text.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();   // Spectre writes Environment.NewLine: CRLF on Windows
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;   // the trailing line break
        count = Math.Min(count, height);   // a frame taller than the window loses its bottom lines, never its top
        var sb = new StringBuilder("\e[H");
        for (var i = 0; i < count; i++)
        {
            sb.Append(lines[i]).Append("\e[K");   // the rest of this line, left by the previous frame
            if (i < count - 1) sb.Append('\n');   // no break after the last line: on the last row it would scroll
        }
        sb.Append("\e[J");                        // the rest of the screen
        _out.Write(sb.ToString());
        _out.Flush();
    }

    private static (int Width, int Height) Size()
    {
        try { return (Math.Max(40, Console.WindowWidth), Math.Max(8, Console.WindowHeight)); }
        catch (IOException) { return (120, 40); }
    }

    public void Dispose() { _out.Write("\e[?25h\e[?1049l"); _out.Flush(); }
}
