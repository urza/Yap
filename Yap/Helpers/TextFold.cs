using System.Globalization;
using System.Text;

namespace Yap.Helpers;

/// <summary>One term of a search query, folded (<see cref="TextFold.Fold"/>). A term that is not Whole is found at
/// any place, also inside a longer word (SR-03). A Whole term stood in quotes: it is found only as whole words
/// (SR-33).</summary>
public readonly record struct SearchTerm(string Text, bool Whole);

/// <summary>
/// The fold of the message search (SR-04): two texts that differ only in case or in accent marks fold to the same
/// text, so "prilis" finds "Příliš". The text goes to its decomposed form, the marks leave, the rest goes to lower
/// case. A letter with a code point of its own (ł, ø, đ, ß) has no decomposed form and stays as it is. The client
/// folds the same way for its highlight (js/ui/search.js, fold); a difference there costs a highlight, never a result.
/// </summary>
public static class TextFold
{
    public static string Fold(string text)
    {
        if (Ascii.IsValid(text)) return text.ToLowerInvariant();   // no mark to remove, and no normalization to pay for
        var decomposed = Decompose(text);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(char.ToLowerInvariant(c));
        return folded.ToString();
    }

    // SR-33: the straight quote, and the marks that a phone keyboard types in its place: English “ ”, Czech and
    // German „ “, French « ». Each one opens or closes a quoted text; which mark closes which does not count.
    private static readonly char[] Quotes = ['"', '“', '”', '„', '«', '»'];

    /// <summary>The terms of a query, folded (SR-03, SR-33). A word outside quotes is a term of its own. A text in
    /// quotes is one whole term, with one space between its words. The client reads a query the same way for its
    /// highlight (js/ui/search.js, termsOf).</summary>
    public static SearchTerm[] Terms(string query)
    {
        var terms = new List<SearchTerm>();
        // Every second part stood in quotes. A quote that is not closed runs to the end: the search runs while the
        // user types, and the answer must not change when the closing mark arrives.
        var parts = Fold(query).Split(Quotes);   // Fold keeps the marks: none of them has a decomposed form
        for (var i = 0; i < parts.Length; i++)
        {
            var words = parts[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (i % 2 == 0) foreach (var word in words) terms.Add(new(word, Whole: false));
            else if (words.Length > 0) terms.Add(new(string.Join(' ', words), Whole: true));
        }
        return [.. terms];
    }

    /// <summary>Whether the text holds every term. The terms are folded already (<see cref="Terms"/>). An ASCII text is
    /// compared in place, with no folded copy of it: most messages are ASCII, and one search reads every message of
    /// its conversation. A term that is not ASCII cannot be in such a text.</summary>
    public static bool HoldsAll(string text, SearchTerm[] terms)
    {
        if (Ascii.IsValid(text)) return Array.TrueForAll(terms, term => Ascii.IsValid(term.Text) && Holds(text, term, StringComparison.OrdinalIgnoreCase));
        var folded = Fold(text);
        return Array.TrueForAll(terms, term => Holds(folded, term, StringComparison.Ordinal));
    }

    // A whole term counts only at a place where no letter or digit stands next to it (SR-33): "hi" is in "Hi!" and
    // not in "this". The first place can be inside a longer word while a later one is not, so every place is tried.
    private static bool Holds(string text, SearchTerm term, StringComparison comparison)
    {
        if (!term.Whole) return text.Contains(term.Text, comparison);
        for (var at = text.IndexOf(term.Text, comparison); at >= 0; at = text.IndexOf(term.Text, at + 1, comparison))
        {
            var end = at + term.Text.Length;
            if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
        }
        return false;
    }

    // Normalize throws on a lone surrogate and on a noncharacter such as U+FFFE, and a member can send either. One such
    // message must not fail every search of its conversation (plan review, 2026-11-12), and its other words must still
    // be found with and without their marks (PR review, 2026-11-12). So only the code points that cannot be normalized
    // leave, and the rest folds as usual.
    private static string Decompose(string text)
    {
        try { return text.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { }
        var clean = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())   // a lone surrogate comes out as U+FFFD, which normalizes
            if (!IsNoncharacter(rune.Value)) clean.Append(rune.ToString());
        try { return clean.ToString().Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { return text; }    // nothing known gets here; the text is then searched as it is, in lower case
    }

    // The 66 code points that Unicode reserves and no text should hold: U+FDD0 to U+FDEF, and the last two of each plane.
    private static bool IsNoncharacter(int value) => (value & 0xFFFE) == 0xFFFE || value is >= 0xFDD0 and <= 0xFDEF;
}
