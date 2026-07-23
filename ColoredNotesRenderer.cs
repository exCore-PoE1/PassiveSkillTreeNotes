using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace PassiveSkillTreeNotes;

internal static class ColoredNotesRenderer
{
    internal readonly record struct StyledCharacter(char Value, Vector4? Color);

    public static IReadOnlyList<StyledCharacter> Parse(string notes)
    {
        var result = new List<StyledCharacter>(notes.Length);
        Vector4? color = null;

        for (var index = 0; index < notes.Length;)
        {
            if (notes[index] == '^')
            {
                if (index + 7 < notes.Length &&
                    (notes[index + 1] == 'x' || notes[index + 1] == 'X') &&
                    TryParseColor(notes.AsSpan(index + 2, 6), out var parsedColor))
                {
                    color = parsedColor;
                    index += 8;
                    continue;
                }

                if (index + 1 < notes.Length && notes[index + 1] == '7')
                {
                    color = null;
                    index += 2;
                    continue;
                }
            }

            if (notes[index] != '\r')
            {
                result.Add(new StyledCharacter(notes[index], color));
            }

            index++;
        }

        return result;
    }

    public static void Draw(IReadOnlyList<StyledCharacter> characters)
    {
        var availableWidth = Math.Max(50f, ImGui.GetContentRegionAvail().X);
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var defaultColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var x = 0f;
        var y = 0f;
        var index = 0;

        while (index < characters.Count)
        {
            var character = characters[index].Value;

            if (character == '\n')
            {
                NewLine(ref x, ref y, lineHeight);
                index++;
                continue;
            }

            if (character is ' ' or '\t')
            {
                var spaceCount = character == '\t' ? 4 : 1;
                var whitespaceWidth = ImGui.CalcTextSize(new string(' ', spaceCount)).X;

                if (x > 0 && x + whitespaceWidth > availableWidth)
                {
                    NewLine(ref x, ref y, lineHeight);
                }
                else
                {
                    x += whitespaceWidth;
                }

                index++;
                continue;
            }

            var wordEnd = index;
            while (wordEnd < characters.Count && !char.IsWhiteSpace(characters[wordEnd].Value))
            {
                wordEnd++;
            }

            var wordWidth = Measure(characters, index, wordEnd);
            if (x > 0 && x + wordWidth > availableWidth)
            {
                NewLine(ref x, ref y, lineHeight);
            }

            if (wordWidth <= availableWidth)
            {
                DrawRange(
                    drawList,
                    characters,
                    index,
                    wordEnd,
                    origin,
                    defaultColor,
                    ref x,
                    y);
            }
            else
            {
                DrawLongWord(
                    drawList,
                    characters,
                    index,
                    wordEnd,
                    origin,
                    defaultColor,
                    availableWidth,
                    lineHeight,
                    ref x,
                    ref y);
            }

            index = wordEnd;
        }

        ImGui.Dummy(new Vector2(availableWidth, y + lineHeight));
    }

    private static void DrawLongWord(
        ImDrawListPtr drawList,
        IReadOnlyList<StyledCharacter> characters,
        int start,
        int end,
        Vector2 origin,
        Vector4 defaultColor,
        float availableWidth,
        float lineHeight,
        ref float x,
        ref float y)
    {
        for (var index = start; index < end; index++)
        {
            var text = characters[index].Value.ToString();
            var width = ImGui.CalcTextSize(text).X;
            if (x > 0 && x + width > availableWidth)
            {
                NewLine(ref x, ref y, lineHeight);
            }

            var color = characters[index].Color ?? defaultColor;
            drawList.AddText(origin + new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(color), text);
            x += width;
        }
    }

    private static void DrawRange(
        ImDrawListPtr drawList,
        IReadOnlyList<StyledCharacter> characters,
        int start,
        int end,
        Vector2 origin,
        Vector4 defaultColor,
        ref float x,
        float y)
    {
        var segmentStart = start;
        while (segmentStart < end)
        {
            var color = characters[segmentStart].Color;
            var segmentEnd = segmentStart + 1;
            while (segmentEnd < end && characters[segmentEnd].Color == color)
            {
                segmentEnd++;
            }

            var text = GetText(characters, segmentStart, segmentEnd);
            drawList.AddText(
                origin + new Vector2(x, y),
                ImGui.ColorConvertFloat4ToU32(color ?? defaultColor),
                text);
            x += ImGui.CalcTextSize(text).X;
            segmentStart = segmentEnd;
        }
    }

    private static float Measure(
        IReadOnlyList<StyledCharacter> characters,
        int start,
        int end)
    {
        var width = 0f;
        var segmentStart = start;

        while (segmentStart < end)
        {
            var color = characters[segmentStart].Color;
            var segmentEnd = segmentStart + 1;
            while (segmentEnd < end && characters[segmentEnd].Color == color)
            {
                segmentEnd++;
            }

            width += ImGui.CalcTextSize(GetText(characters, segmentStart, segmentEnd)).X;
            segmentStart = segmentEnd;
        }

        return width;
    }

    private static string GetText(
        IReadOnlyList<StyledCharacter> characters,
        int start,
        int end)
    {
        var text = new char[end - start];
        for (var index = start; index < end; index++)
        {
            text[index - start] = characters[index].Value;
        }

        return new string(text);
    }

    private static void NewLine(ref float x, ref float y, float lineHeight)
    {
        x = 0f;
        y += lineHeight;
    }

    private static bool TryParseColor(ReadOnlySpan<char> value, out Vector4 color)
    {
        color = default;
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new Vector4(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            1f);
        return true;
    }
}
