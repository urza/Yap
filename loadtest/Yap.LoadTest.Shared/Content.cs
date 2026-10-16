using System.Text;

namespace Yap.LoadTest.Shared;

/// <summary>
/// Message text for the virtual users. The words carry no dot-joined pairs, because the server's link regex also
/// matches bare "name.com"-like words and would start a preview fetch for them.
/// </summary>
public static class Content
{
    private static readonly string[] Words =
        ("the quick lazy brown fox jumps over river mountain coffee morning evening train station garden window music " +
         "guitar keyboard update release branch merge deploy weekend kitchen recipe lemon pepper bread cheese movie " +
         "episode season finally maybe later today tomorrow yesterday really pretty slow fast loud quiet green blue " +
         "silver north south east west ticket flight hotel beach forest rain snow cloud sun moon star planet rocket " +
         "camera photo video stream podcast chapter page book shelf lamp chair table door street market bridge tower").Split(' ');

    private static readonly string[] Emoji =
        ["😀", "😂", "🤣", "😍", "🥰", "😎", "🤔", "😮", "😢", "😡", "👍", "👎", "👏", "🙏", "🔥", "❤️", "💯", "🎉", "🚀", "✨", "🍕", "☕", "🐱", "🐶", "🌧️", "🌞"];

    // Real public posts. YouTube and X unfurl from the server's IP; TikTok often answers with a challenge page,
    // which is a realistic failure the preview cache records for an hour. Replace the list with links.txt.
    public static readonly string[] DefaultLinks =
    [
        "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=jNQXAC9IVRw", "https://www.youtube.com/watch?v=9bZkp7q19f0",
        "https://www.youtube.com/watch?v=kJQP7kiw5Fk", "https://www.youtube.com/watch?v=RgKAFK5djSk", "https://www.youtube.com/watch?v=OPf0YbXqDm0",
        "https://www.youtube.com/watch?v=fJ9rUzIMcZQ", "https://www.youtube.com/watch?v=hT_nvWreIhg", "https://youtu.be/CevxZvSJLk8",
        "https://www.youtube.com/watch?v=YQHsXMglC9A", "https://www.youtube.com/watch?v=JGwWNGJdvx8", "https://www.youtube.com/watch?v=60ItHLz5WEA",
        "https://x.com/jack/status/20", "https://x.com/BarackObama/status/896523232098078720", "https://x.com/NASA/status/1545444990705135618",
        "https://x.com/SpaceX/status/1743325136264708470", "https://twitter.com/elonmusk/status/1519480761749016577",
        "https://www.tiktok.com/@khaby.lame/video/7156453812254878982", "https://www.tiktok.com/@nasa/video/7232117811836832046",
        "https://vm.tiktok.com/ZMjKq1y7v/", "https://www.tiktok.com/@gordonramsayofficial/video/7193512120346651910",
        "https://en.wikipedia.org/wiki/Load_testing", "https://github.com/dotnet/aspnetcore", "https://www.bbc.com/news",
    ];

    public static string Short(Random rng) => Sentence(rng, rng.Next(1, 9), rng.Next(3) == 0);

    /// <summary>Three to eight sentences, two paragraphs now and then; stays far below the 4000-character limit.</summary>
    public static string Long(Random rng)
    {
        var sb = new StringBuilder();
        var sentences = rng.Next(3, 9);
        for (var i = 0; i < sentences; i++)
        {
            sb.Append(Sentence(rng, rng.Next(6, 18), true));
            sb.Append(i == sentences / 2 && rng.Next(2) == 0 ? "\n\n" : " ");
        }
        return sb.ToString().TrimEnd();
    }

    public static string EmojiOnly(Random rng) => string.Concat(Enumerable.Range(0, rng.Next(1, 4)).Select(_ => Emoji[rng.Next(Emoji.Length)]));
    public static string OneEmoji(Random rng) => Emoji[rng.Next(Emoji.Length)];

    public static string WithCustomEmoji(Random rng, IReadOnlyList<string> shortcodes)
        => shortcodes.Count == 0 ? EmojiOnly(rng) : $"{Sentence(rng, rng.Next(0, 5), false)} :{shortcodes[rng.Next(shortcodes.Count)]}:".Trim();

    public static string Link(Random rng, IReadOnlyList<string> links)
        => rng.Next(3) == 0 ? links[rng.Next(links.Count)] : $"{Sentence(rng, rng.Next(2, 7), false)} {links[rng.Next(links.Count)]}";

    private static string Sentence(Random rng, int words, bool punctuate)
    {
        if (words == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            var w = Words[rng.Next(Words.Length)];
            sb.Append(i == 0 && punctuate ? char.ToUpperInvariant(w[0]) + w[1..] : w);
            if (i < words - 1) sb.Append(' ');
        }
        if (punctuate) sb.Append(rng.Next(10) switch { 0 => "!", 1 => "?", _ => "." });
        if (rng.Next(6) == 0) sb.Append(' ').Append(Emoji[rng.Next(Emoji.Length)]);
        return sb.ToString();
    }
}
